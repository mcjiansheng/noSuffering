using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using NoSuffering.Config;

namespace NoSuffering.Shops;

// Native payloads are encoded with the game's converters; companion JSON contains
// only our records and strings. Prices are the base prices, before live relic hooks.
public sealed record ShopEntryRecord(string? OriginalCard, string? Card, string? Model, int Cost, bool OnSale,
    string[]? ModifyingRelics = null, string Group = "", CardType? CardType = null, CardRarity? CardRarity = null,
    string[]? CardPool = null, string? TemplateModel = null);
public sealed record ShopPlayerRecord(ulong Seed, bool Purchased, ShopEntryRecord[] Entries, bool RemovalUsed,
    bool HasRemoval = true);
public sealed record ShopRecord(string Context, Dictionary<ulong, ShopPlayerRecord> Players);

public static class ShopService
{
    private static readonly FieldInfo StreamsField = AccessTools.Field(typeof(PlayerRngSet), "_rngs");
    private static readonly FieldInfo PriceField = AccessTools.Field(typeof(MerchantEntry), "_cost");
    private static readonly FieldInfo OwnerField = AccessTools.Field(typeof(MerchantEntry), "_player");
    private static readonly FieldInfo CardPoolField = AccessTools.Field(typeof(MerchantCardEntry), "_cardPool");
    private static readonly FieldInfo CardTypeField = AccessTools.Field(typeof(MerchantCardEntry), "_cardType");
    private static readonly FieldInfo CardRarityField = AccessTools.Field(typeof(MerchantCardEntry), "_cardRarity");
    private static readonly FieldInfo OddsRngField = AccessTools.Field(typeof(AbstractOdds), "_rng");
    private static ShopRecord? _state;
    private static bool _generating;
    private static bool _busy;
    private static readonly Dictionary<ulong, int> PurchasesInFlight = [];
    private static readonly HashSet<ulong> PendingRestore = [];
    private static readonly ConditionalWeakTable<MerchantRoom, object> RestoredEntries = new();
    private static RunState State => RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("没有活动运行状态");
    private static string Context => $"{State.Rng.Seed}:{State.MapLocation}";
    public static bool HasPendingPurchases => PurchasesInFlight.Values.Any(count => count > 0);

    public static string? GetUnavailableReason(ulong playerId, HostRules rules)
    {
        if (!RunManager.Instance.IsInProgress || State.CurrentRoom is not MerchantRoom room) return "当前不是商店";
        if (!rules.EnableShopReroll) return "此功能已关闭";
        if (_busy || PurchasesInFlight.GetValueOrDefault(playerId) > 0) return "操作同步中";
        if (!room.Inventories.Any(i => i.Player.NetId == playerId)) return "玩家不在此商店中";
        if (_state?.Context == Context && _state.Players.TryGetValue(playerId, out var record) && record.Purchased)
            return "购买后不能刷新商店";
        return null;
    }

    public static ShopRecord? CaptureState()
    {
        if (_state is null || !RunManager.Instance.IsInProgress || _state.Context != Context)
            return null;
        // During native multiplayer loading the host must send the saved stock
        // before either peer enters the room and invokes its inventory factory.
        if (State.CurrentRoom is not MerchantRoom)
            return PendingRestore.Count > 0 ? Clone(_state) : null;
        if (RunManager.Instance.IsInProgress && State.CurrentRoom is MerchantRoom room && _state.Context == Context)
        {
            var inventory = room.Inventories.Single(i => i.Player.NetId == LocalContext.GetMe(State)!.NetId);
            Store(inventory, _state.Players.GetValueOrDefault(inventory.Player.NetId)?.Seed ?? 0);
        }
        return Clone(_state);
    }

    // Ready packets may contribute ONLY the authenticated sender's native stock.
    // Remote native inventories do not receive native Courier restocks or clears.
    public static ShopRecord? CaptureOwnedState()
    {
        var state = CaptureState();
        if (state == null || !RunManager.Instance.IsInProgress || State.CurrentRoom is not MerchantRoom || state.Context != Context)
            return null;
        ulong id = LocalContext.GetMe(State)!.NetId;
        return new ShopRecord(state.Context, new() { [id] = state.Players[id] });
    }

    public static void MergeOwnedState(ShopRecord? state, ulong authenticatedPlayerId)
    {
        if (state == null) return;
        if (!RunManager.Instance.IsInProgress || State.CurrentRoom is not MerchantRoom || state.Context != Context ||
            state.Players.Count != 1 || !state.Players.TryGetValue(authenticatedPlayerId, out var stock) ||
            !State.Players.Any(p => p.NetId == authenticatedPlayerId))
            throw new InvalidOperationException("商店同步快照不属于当前玩家或位置");
        if (_state?.Context != Context) _state = new ShopRecord(Context, []);
        bool purchased = stock.Purchased || (_state.Players.GetValueOrDefault(authenticatedPlayerId)?.Purchased ?? false);
        _state.Players[authenticatedPlayerId] = stock with { Purchased = purchased,
            Entries = stock.Entries.Select(CloneEntry).ToArray() };
    }

    // Use on an already-entered room after collecting authenticated Ready records.
    // RestoreState is reserved for the native load path and arms factory replay.
    public static void ApplySynchronizedState(ShopRecord? state)
    {
        if (state == null) return;
        if (state.Context != Context) throw new InvalidOperationException("商店同步快照位置已改变");
        _state = Clone(state);
    }

    public static void RestoreState(ShopRecord? record, bool live = false)
    {
        // A live metadata sync does not own or cancel already-running native
        // purchase tasks, and must not arm room-entry factory reconstruction.
        if (live) { ApplySynchronizedState(record); return; }
        _state = record is null ? null : Clone(record);
        RestoredEntries.Clear();
        PendingRestore.Clear();
        if (record != null) foreach (var id in record.Players.Keys) PendingRestore.Add(id);
        PurchasesInFlight.Clear();
    }

    private static ShopRecord Clone(ShopRecord record) => record with
    {
        Players = record.Players.ToDictionary(p => p.Key, p => p.Value with {
            Entries = p.Value.Entries.Select(CloneEntry).ToArray() })
    };
    private static ShopEntryRecord CloneEntry(ShopEntryRecord entry) => entry with
    {
        ModifyingRelics = entry.ModifyingRelics?.ToArray(), CardPool = entry.CardPool?.ToArray()
    };

    public static Task ExecuteAsync(ulong seed, ulong playerId, HostRules rules)
    {
        if (GetUnavailableReason(playerId, rules) is { } reason) throw new InvalidOperationException(reason);
        var room = (MerchantRoom)State.CurrentRoom!;
        int index = room.Inventories.FindIndex(i => i.Player.NetId == playerId);
        var player = room.Inventories[index].Player;
        // Rollbacks must not leak partially generated relic removals into the run.
        var personalBag = player.RelicGrabBag.ToSerializable();
        var sharedBag = State.SharedRelicGrabBag.ToSerializable();
        _busy = true;
        try
        {
            MerchantInventory inventory;
            _generating = true;
            try { inventory = WithRng(player, seed, () => MerchantInventory.CreateForNormalMerchant(player)); }
            catch
            {
                player.RelicGrabBag.LoadFromSerializable(personalBag);
                State.SharedRelicGrabBag.LoadFromSerializable(sharedBag);
                throw;
            }
            finally { _generating = false; }
            var oldInventory = room.Inventories[index];
            room.Inventories[index] = inventory;
            Store(inventory, seed);
            // Replace only this client's native presentation when it owns the stock.
            // No room Exit/Enter, room ID change, or another player's stock redraw.
            if (LocalContext.GetMe(State)?.NetId == playerId && NMerchantRoom.Instance is { } node)
            {
                bool open = node.Inventory.IsOpen;
                NRun.Instance?.SetCurrentRoom(NMerchantRoom.Create(room, State.Players));
                if (open) NMerchantRoom.Instance?.OpenInventory();
            }
            foreach (var entry in oldInventory.CardEntries)
            {
                if (entry.CreationResult is not { } result) continue;
                State.RemoveCard(result.originalCard);
                if (!ReferenceEquals(result.Card, result.originalCard)) State.RemoveCard(result.Card);
            }
            return Task.CompletedTask;
        }
        finally { _busy = false; }
    }

    private static T WithRng<T>(Player player, ulong seed, Func<T> action)
    {
        var streams = (Dictionary<PlayerRngType, Rng>)StreamsField.GetValue(player.PlayerRng)!;
        var shops = streams[PlayerRngType.Shops];
        var rewards = streams[PlayerRngType.Rewards];
        var cardOdds = player.PlayerOdds.CardRarity;
        var oddsRng = (Rng)OddsRngField.GetValue(cardOdds)!;
        try
        {
            streams[PlayerRngType.Shops] = CreateRng(seed, $"nosuffering_shop_{player.NetId}");
            streams[PlayerRngType.Rewards] = CreateRng(seed, $"nosuffering_shop_rarity_{player.NetId}");
            // Native CardRarityOdds caches Rewards at player creation. Swapping
            // the dictionary alone leaves shop rarity rolls on the live stream.
            OddsRngField.SetValue(cardOdds, streams[PlayerRngType.Rewards]);
            return action();
        }
        finally
        {
            streams[PlayerRngType.Shops] = shops;
            streams[PlayerRngType.Rewards] = rewards;
            OddsRngField.SetValue(cardOdds, oddsRng);
        }
    }

    private static Rng CreateRng(ulong seed, string name)
    {
#if STS2_STABLE
        return new Rng(unchecked((uint)seed), name);
#else
        return new Rng(seed, name);
#endif
    }

    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, JsonSerializationUtility.Options);
    private static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonSerializationUtility.Options)
        ?? throw new InvalidOperationException("商店存档条目为空");

    private static void Store(MerchantInventory inventory, ulong seed)
    {
        if (_state?.Context != Context) _state = new ShopRecord(Context, []);
        bool purchased = _state.Players.GetValueOrDefault(inventory.Player.NetId)?.Purchased ?? false;
        ShopEntryRecord CardRecord(MerchantCardEntry e, string group) => new(
            e.CreationResult is { } r ? Encode(r.originalCard.ToSerializable()) : null,
            e.CreationResult is { } c ? Encode(c.Card.ToSerializable()) : null,
            null, (int)PriceField.GetValue(e)!, e.IsOnSale,
            e.CreationResult?.ModifyingRelics.Select(r => r.Id.ToString()).ToArray(), group,
            (CardType?)CardTypeField.GetValue(e), (CardRarity?)CardRarityField.GetValue(e),
            ((IEnumerable<CardModel>)CardPoolField.GetValue(e)!).Select(c => c.Id.ToString()).ToArray());
        var entries = inventory.CharacterCardEntries.Select(e => CardRecord(e, "character"))
            .Concat(inventory.ColorlessCardEntries.Select(e => CardRecord(e, "colorless")))
            .Concat(inventory.RelicEntries.Select(e => new ShopEntryRecord(null, null,
                e.Model is { } r ? Encode(r.ToSerializable()) : null, (int)PriceField.GetValue(e)!, false, Group: "relic")))
            .Concat(inventory.PotionEntries.Select(e => new ShopEntryRecord(null, null,
                e.Model is { } p ? Encode(p.ToSerializable(0)) : null, (int)PriceField.GetValue(e)!, false, Group: "potion"))).ToArray();
        var oldEntries = _state.Players.GetValueOrDefault(inventory.Player.NetId)?.Entries ?? [];
        foreach (var group in new[] { "relic", "potion" })
        {
            var previous = oldEntries.Where(e => e.Group == group).ToArray();
            int slot = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].Group != group) continue;
                entries[i] = entries[i] with { TemplateModel = entries[i].Model ??
                    (slot < previous.Length ? previous[slot].TemplateModel ?? previous[slot].Model : null) };
                slot++;
            }
        }
        _state.Players[inventory.Player.NetId] = new ShopPlayerRecord(seed, purchased, entries,
            inventory.CardRemovalEntry?.Used ?? false, inventory.CardRemovalEntry != null);
    }

    private static MerchantInventory Rehydrate(Player player, ShopPlayerRecord record)
    {
        var inventory = new MerchantInventory(player);
        var colored = (List<MerchantCardEntry>)AccessTools.Field(typeof(MerchantInventory), "_characterCardEntries").GetValue(inventory)!;
        var colorless = (List<MerchantCardEntry>)AccessTools.Field(typeof(MerchantInventory), "_colorlessCardEntries").GetValue(inventory)!;
        var potions = (List<MerchantPotionEntry>)AccessTools.Field(typeof(MerchantInventory), "_potionEntries").GetValue(inventory)!;
        foreach (var data in record.Entries.Where(e => e.Group is "character" or "colorless"))
        {
            if (data.CardPool == null || (data.CardType == null && data.CardRarity == null))
                throw new InvalidOperationException("商店存档缺少原生卡牌生成信息");
            var pool = data.CardPool.Select(id => ModelDb.GetById<CardModel>(ModelId.Deserialize(id))).ToList();
            var entry = data.CardType is { } type ? new MerchantCardEntry(player, inventory, pool, type)
                : new MerchantCardEntry(player, inventory, pool, data.CardRarity!.Value);
            if (data.OriginalCard != null)
            {
                var original = State.LoadCard(Decode<SerializableCard>(data.OriginalCard), player);
                var result = new CardCreationResult(original);
                if (data.Card != data.OriginalCard || data.ModifyingRelics is { Length: > 0 })
                {
                    var modified = State.LoadCard(Decode<SerializableCard>(data.Card!), player);
                    if (data.ModifyingRelics is { Length: > 0 })
                        foreach (var id in data.ModifyingRelics)
                            result.ModifyCard(modified, player.Relics.Single(r => r.Id.ToString() == id));
                    else result.ModifyCard(modified);
                }
                AccessTools.Property(typeof(MerchantCardEntry), nameof(MerchantCardEntry.CreationResult)).SetValue(entry, result);
            }
            AccessTools.Property(typeof(MerchantCardEntry), nameof(MerchantCardEntry.IsOnSale)).SetValue(entry, data.OnSale);
            PriceField.SetValue(entry, data.Cost);
            (data.Group == "character" ? colored : colorless).Add(entry);
        }
        foreach (var data in record.Entries.Where(e => e.Group == "relic"))
        {
            // A sold slot still needs its native entry for navigation/restock behavior.
            var template = data.Model ?? data.TemplateModel ?? throw new InvalidOperationException("商店存档缺少已售遗物模型");
            var model = RelicModel.FromSerializable(Decode<SerializableRelic>(template));
            var entry = new MerchantRelicEntry(model, player);
            if (data.Model == null) AccessTools.Property(typeof(MerchantRelicEntry), nameof(MerchantRelicEntry.Model)).SetValue(entry, null);
            PriceField.SetValue(entry, data.Cost);
            inventory.AddRelicEntry(entry);
        }
        foreach (var data in record.Entries.Where(e => e.Group == "potion"))
        {
            var template = data.Model ?? data.TemplateModel ?? throw new InvalidOperationException("商店存档缺少已售药水模型");
            var entry = new MerchantPotionEntry(PotionModel.FromSerializable(Decode<SerializablePotion>(template)), player);
            if (data.Model == null) AccessTools.Property(typeof(MerchantPotionEntry), nameof(MerchantPotionEntry.Model)).SetValue(entry, null);
            PriceField.SetValue(entry, data.Cost);
            potions.Add(entry);
        }
        if (record.Entries.Any(e => e.Group is not ("character" or "colorless" or "relic" or "potion")))
            throw new InvalidOperationException("商店存档包含不支持的自定义商品组");
        if (record.HasRemoval)
        {
            var removal = new MerchantCardRemovalEntry(player);
            if (record.RemovalUsed) removal.SetUsed();
            AccessTools.Property(typeof(MerchantInventory), nameof(MerchantInventory.CardRemovalEntry)).SetValue(inventory, removal);
        }
        var update = (Action<PurchaseStatus, MerchantEntry>)Delegate.CreateDelegate(typeof(Action<PurchaseStatus, MerchantEntry>), inventory,
            AccessTools.Method(typeof(MerchantInventory), "UpdateEntries"));
        foreach (var entry in inventory.AllEntries) entry.PurchaseCompleted += update;
        return inventory;
    }

    private static void MarkPurchased(ulong playerId)
    {
        if (!RunManager.Instance.IsInProgress || State.CurrentRoom is not MerchantRoom room) return;
        var inventory = room.Inventories.SingleOrDefault(i => i.Player.NetId == playerId);
        if (inventory == null) return;
        if (_state?.Context != Context || !_state.Players.ContainsKey(playerId) || LocalContext.GetMe(State)!.NetId == playerId)
            Store(inventory, _state?.Players.GetValueOrDefault(playerId)?.Seed ?? 0);
        _state!.Players[playerId] = _state.Players[playerId] with { Purchased = true };
    }

    private static void FinishPurchase(ulong playerId)
    {
        int remaining = PurchasesInFlight.GetValueOrDefault(playerId) - 1;
        if (remaining <= 0) PurchasesInFlight.Remove(playerId);
        else PurchasesInFlight[playerId] = remaining;
    }

    private static async Task<bool> TrackPurchase(Task<bool> task, ulong playerId)
    {
        PurchasesInFlight[playerId] = PurchasesInFlight.GetValueOrDefault(playerId) + 1;
        bool success;
        try
        {
            success = await task;
            // Other shop mods may replace the native wrapper and deliberately
            // omit InvokePurchaseCompleted (for example, gifting an item).
            if (success) MarkPurchased(playerId);
        }
        finally { FinishPurchase(playerId); }
        // Nested removal wrappers share this tracker. Only the outer completion
        // persists, after native restock/selection tasks have all finished.
        if (success && !PurchasesInFlight.ContainsKey(playerId) && LocalContext.GetMe(State)?.NetId == playerId)
            await NoSuffering.Multiplayer.HostCoordinator.PersistShopPurchaseAsync();
        return success;
    }

    [HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.CreateForNormalMerchant))]
    private static class InventoryPatch
    {
        private static bool Prefix(Player player, ref MerchantInventory __result)
        {
            if (_generating || !PendingRestore.Contains(player.NetId) || _state?.Context != Context ||
                !_state.Players.TryGetValue(player.NetId, out var saved)) return true;
            __result = WithRng(player, saved.Seed, () => Rehydrate(player, saved));
            PendingRestore.Remove(player.NetId);
            if (State.CurrentRoom is MerchantRoom room && !room.Inventories.Any(i => i.Player.NetId == player.NetId))
                RestoredEntries.GetValue(room, _ => new object());
            return false;
        }
        private static void Postfix(MerchantInventory __result)
        {
            if (!_generating) Store(__result, _state?.Context == Context ? _state.Players.GetValueOrDefault(__result.Player.NetId)?.Seed ?? 0 : 0);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterRoomEntered))]
    private static class RestoredEntryEffectsPatch
    {
        private static bool Prefix(AbstractRoom room, ref Task __result)
        {
            // The paired native snapshot already contains entry effects. Native
            // load must still construct the merchant's room/UI, but must not
            // heal Meal Ticket or pay Maw Bank a second time. Ordinary entry and
            // pre-entry rollback never replay saved stock and have no marker.
            if (room is not MerchantRoom merchant || !RestoredEntries.Remove(merchant)) return true;
            __result = Task.CompletedTask;
            return false;
        }
    }

    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.InvokePurchaseCompleted))]
    private static class SuccessfulPurchasePatch
    {
        private static void Prefix(MerchantEntry __instance) => MarkPurchased(((Player)OwnerField.GetValue(__instance)!).NetId);
    }

    [HarmonyPatch(typeof(RewardSynchronizer), "HandleGoldLostMessage")]
    private static class RemotePurchasePatch
    {
        // Native GoldLost is emitted only after card/relic/potion acquisition succeeds.
        private static void Postfix(ulong senderId) => MarkPurchased(senderId);
    }

    [HarmonyPatch(typeof(OneOffSynchronizer), "DoMerchantCardRemoval")]
    private static class RemovalPatch
    {
        private static void Postfix(Player player, ref Task<bool> __result) => __result = TrackPurchase(__result, player.NetId);
    }

    [HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
    private static class PurchaseInFlightPatch
    {
        private static bool Prefix(ref Task<bool> __result)
        {
            if (!_busy && !NoSuffering.Multiplayer.HostCoordinator.Busy) return true;
            __result = Task.FromResult(false);
            return false;
        }
        private static void Postfix(MerchantEntry __instance, ref Task<bool> __result) =>
            __result = TrackPurchase(__result, ((Player)OwnerField.GetValue(__instance)!).NetId);
    }

    [HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper),
        [typeof(MerchantInventory), typeof(bool), typeof(bool)])]
    private static class RemovalInputPatch
    {
        private static bool Prefix(ref Task<bool> __result)
        {
            if (!_busy && !NoSuffering.Multiplayer.HostCoordinator.Busy) return true;
            __result = Task.FromResult(false);
            return false;
        }
        private static void Postfix(MerchantCardRemovalEntry __instance, ref Task<bool> __result) =>
            __result = TrackPurchase(__result, ((Player)OwnerField.GetValue(__instance)!).NetId);
    }
}
