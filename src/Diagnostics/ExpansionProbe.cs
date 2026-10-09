using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using NoSuffering.Shops;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Invoked only by GameplayProbe's isolated-lab argument gate. Direct native room
// entries are fixtures, not a playthrough, physical-pointer or multiplayer test.
public static class ExpansionProbe
{
    private static readonly Dictionary<string, string> Results = new();
    private static readonly Dictionary<string, object> State = new();

    public static async Task Run(SceneTree tree)
    {
        string[] checks = ["initial_ancient_options_claim_proceed", "shop_targeted_refresh_native_prices",
            "shop_failed_purchase_unlocked", "shop_courier_purchase_locks", "boss_reject_act_two",
            "boss_reject_nonboss", "boss_health_additive_damage", "boss_health_normal_restart",
            "boss_health_new_order_restart", "boss_health_native_save_continue"];
        foreach (var check in checks) Results[check] = "UNEXECUTED";
        foreach (var check in new[] { "shop_entry_relic_effects", "shop_reroll_continue_no_entry_effects", "shop_purchase_continue_no_entry_effects" })
            Results[check] = "UNEXECUTED";
        if (FrameworkProbe.IsPresent) Results["actual_modconfig_integration"] = "UNEXECUTED";
        var stage = checks[0];
        try
        {
            await AwaitStartup();
            if (FrameworkProbe.IsPresent)
            {
                stage = "actual_modconfig_integration";
                State["actual_modconfig"] = await FrameworkProbe.Run(tree);
                Pass(stage, "actual framework registered 14 settings; both sync directions and persistence verified; native Mods controls instantiated through engine");
                stage = checks[0];
            }
            var game = NGame.Instance!;
            var manager = RunManager.Instance;
            Require(!manager.IsInProgress, "Expansion fixture requires no active run.");
            ConfigStore.ChangeRules(r => r with { EnableAncientOptionsReroll = true,
                OptionsCostMode = RefreshCostMode.Free, EnableCombatRestart = true,
                EnableCombatReroll = true, EnableBossHealthIncrease = true, EnableShopReroll = true });
            var run = RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(), UnlockState.all, 1UL)],
                ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList(), [], GameMode.Standard, 0, "NOSUFFERINGEXPANSION");
            manager.SetUpNewSingleplayer(run, true);
            await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p => p.Character)), "run assets");
            await Timed(manager.FinalizeStartingRelics(), "starting relics");
            manager.Launch();
            game.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            foreach (var ftue in new[] { "map_select_ftue", "merchant_ftue", "combat_reward_ftue", "combat_rules_ftue" })
                SaveManager.Instance.MarkFtueAsComplete(ftue);
            await Timed(manager.EnterAct(0, false), "native act-one setup");
            HostCoordinator.OnRunReady();
            await Timed(manager.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord), "native initial ancient entry");
            await ClaimInitialAncient();
            Pass(stage, "initial ancient personal options refreshed, native reward and Proceed handlers opened map");

            stage = checks[1];
            await TestShop(checks);

            stage = checks[4];
            await Timed(manager.EnterAct(1, false), "act-two boss fixture");
            await Timed(manager.EnterMapCoord(Bridge.State.Map.BossMapPoint.coord), "native act-two boss entry");
            await Opening();
            var actTwoHp = EnemyHealth();
            Require(BossHealthService.GetUnavailableReason(ConfigStore.Rules) != null, "Act-two boss exposed increase action.");
            bool rejected = false;
            try { await BossHealthService.ExecuteAsync(25); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && EnemyHealth() == actTwoHp, "Act-two boss increase was not rejected without mutation.");
            Pass(stage, "native act-two boss rejected, enemy HP unchanged");

            stage = checks[5];
            await Timed(manager.EnterAct(2, false), "native act-three setup");
            var monster = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Monster)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(monster.coord), "native act-three ordinary combat");
            await Opening();
            var ordinaryHp = EnemyHealth();
            rejected = false;
            try { await BossHealthService.ExecuteAsync(25); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && EnemyHealth() == ordinaryHp, "Act-three ordinary combat accepted boss increase.");
            Pass(stage, "act-three native ordinary encounter rejected, enemy HP unchanged");

            stage = checks[6];
            await Timed(manager.EnterMapCoord(Bridge.State.Map.BossMapPoint.coord), "native act-three boss entry");
            await Opening();
            Require(BossHealthService.GetUnavailableReason(ConfigStore.Rules) == null, "Act-three boss increase unavailable.");
            var bossRoom = (CombatRoom)Bridge.State.CurrentRoom!;
            var targets = bossRoom.Enemies.Where(e => e.IsAlive).ToList();
            var target = targets.First(e => e.CurrentHp > 1);
            // Fixture-only direct native HP change creates existing damage without
            // killing a boss or claiming any gameplay/progression verification.
            target.SetCurrentHpInternal(target.CurrentHp - 1);
            var before = targets.ToDictionary(e => e.CombatId!.Value, e => (e.MaxHp, e.CurrentHp));
            var baseline = CombatService.Baseline;
            await IncreaseBoss(25);
            foreach (var enemy in targets)
            {
                var old = before[enemy.CombatId!.Value];
                int increase = (int)Math.Ceiling(old.MaxHp * 0.25m);
                Require(enemy.MaxHp == old.MaxHp + increase && enemy.CurrentHp == old.CurrentHp + increase,
                    "Boss increase failed additive percentage or changed existing damage.");
            }
            var expectedMax = targets.ToDictionary(e => e.CombatId!.Value, e => e.MaxHp);
            Require(CombatService.Baseline == baseline, "Boss increase mutated immutable combat baseline.");
            State["boss_expected_max_hp"] = expectedMax;
            State["boss_context"] = BossHealthService.CaptureState()!.ContextId;
            Pass(stage, "25% of existing effective max HP added to every living enemy; existing damage retained");

            stage = checks[7];
            await Operation(CoreOperation.CombatRestart);
            CheckBossMax(expectedMax);
            Require(CombatService.Attempt == 0 && CombatService.Baseline == baseline, "Normal restart changed initial attempt/baseline.");
            Pass(stage, "native restart retained exact adjusted max HP without compounding");

            stage = checks[8];
            await Operation(CoreOperation.CombatReroll);
            CheckBossMax(expectedMax);
            Require(CombatService.Attempt == 1 && CombatService.Baseline == baseline, "Order refresh changed immutable baseline.");
            var refreshed = Digests();
            await Operation(CoreOperation.CombatRestart);
            CheckBossMax(expectedMax);
            Require(CombatService.Attempt == 1 && Digests() == refreshed, "Normal restart lost refreshed draw order.");
            Pass(stage, "order refresh and subsequent normal restart retained HP and active draw attempt");

            stage = checks[9];
            await Timed(CompanionStore.SaveNativeCurrent(), "native disk save");
            var saved = SaveManager.Instance.LoadRunSave();
            Require(saved.Success && saved.SaveData != null && CompanionStore.Error == null, "Native disk/companion pairing failed.");
            Require(BossHealthService.CaptureState()?.Enemies.Count == expectedMax.Count, "Disk companion lost boss adjustment.");
            var loaded = RunState.FromSerializable(saved.SaveData!);
            manager.EventSynchronizer.Dispose();
            manager.OneOffSynchronizer.Dispose();
            manager.CleanUp();
            await Timed(manager.SetUpSavedSingleplayer(loaded, saved.SaveData!), "native saved setup");
            game.ReactionContainer.InitializeNetworking(manager.NetService);
            await Timed(game.LoadRun(loaded, saved.SaveData!.PreFinishedRoom), "native disk continue");
            await Opening();
            CheckBossMax(expectedMax);
            Require(CombatService.Attempt == 1 && Digests() == refreshed, "Native continue lost refreshed attempt/order.");
            Pass(stage, "actual disk save/read and same-process native continue retained boss max HP and refreshed order");
        }
        catch (Exception error)
        {
            if (!Results.Values.Any(value => value.StartsWith("FAIL", StringComparison.Ordinal))) Results[stage] = "FAIL: " + error.Message;
            Log.Error("[NoSuffering] EXPANSION_PROBE " + stage + " " + error);
        }
        finally
        {
            bool success = Results.Values.All(value => value.StartsWith("PASS", StringComparison.Ordinal));
            var report = JsonSerializer.Serialize(new { scope = "isolated native-engine singleplayer fixtures; direct room/act entry; no physical pointer, full playthrough, fresh-process or multiplayer acceptance",
                process_id = System.Environment.ProcessId, results = Results, state = State });
            Log.Info("[NoSuffering] EXPANSION_PROBE_RESULTS " + report);
            File.WriteAllText(Path.Combine(OS.GetUserDataDir(), "nosuffering-expansion-probe.json"), report);
            tree.Quit(success ? 0 : 1);
        }
    }

    private static async Task ClaimInitialAncient()
    {
        await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "initial ancient buttons");
        NEventOptionButton? reward = null;
        for (int count = 1; reward == null; count++)
        {
            await Operation(CoreOperation.AncientOptionsReroll);
            await Wait(() => NEventRoom.Instance?.Layout?.OptionButtons.Any() == true, "refreshed initial ancient buttons");
            reward = NEventRoom.Instance!.Layout!.OptionButtons.FirstOrDefault(button => button.Option.Relic is { } relic &&
                (relic.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE" ||
                 relic.GetType().GetMethod(nameof(RelicModel.AfterObtained))!.DeclaringType == typeof(RelicModel)) && !button.Option.IsLocked);
            Require(reward != null || count < 6, "No prompt-free initial ancient reward in fixture.");
        }
        State["initial_ancient_reward"] = reward.Option.Relic!.Id.Entry;
        var local = RunManager.Instance.EventSynchronizer.GetLocalEvent();
        reward.Call(NEventOptionButton.MethodName.OnRelease);
        await Wait(() => local.IsFinished && NEventRoom.Instance!.Layout!.OptionButtons.Any(b => b.Option.IsProceed), "initial ancient reward completion");
        await Timed(RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks(), "initial ancient native tasks");
        Require(((EventRoom)Bridge.State.CurrentRoom!).IsPreFinished, "Ancient claim did not finish room.");
        NEventRoom.Instance!.Layout!.OptionButtons.Single(b => b.Option.IsProceed).Call(NEventOptionButton.MethodName.OnRelease);
        await Bridge.Frame();
        Require(NMapScreen.Instance is { IsOpen: true, IsTravelEnabled: true }, "Native ancient Proceed failed to open actionable map.");
    }

    private static async Task TestShop(string[] checks)
    {
        var stage = checks[1];
        try
        {
            var manager = RunManager.Instance;
            stage = "shop_entry_relic_effects";
            var fixturePlayer = Bridge.State.Players.Single();
            await Timed(RelicCmd.Obtain<MealTicket>(fixturePlayer), "native Meal Ticket fixture");
            await Timed(RelicCmd.Obtain<MawBank>(fixturePlayer), "native Maw Bank fixture");
            fixturePlayer.Creature.SetCurrentHpInternal(fixturePlayer.Creature.MaxHp - 30);
            int entryHp = fixturePlayer.Creature.CurrentHp;
            int entryGold = fixturePlayer.Gold;
            var shop = Bridge.State.Map.GetAllMapPoints().Where(p => p.PointType == MapPointType.Shop)
                .OrderBy(p => p.coord.row).ThenBy(p => p.coord.col).First();
            await Timed(manager.EnterMapCoord(shop.coord), "native shop fixture entry");
            var room = (MerchantRoom)Bridge.State.CurrentRoom!;
            var inventory = room.GetLocalInventory();
            var player = inventory.Player;
            Require(player.Creature.CurrentHp == entryHp + 15 && player.Gold == entryGold + 12,
                "Ordinary shop entry failed native Meal Ticket healing or active Maw Bank gold.");
            Pass(stage, "ordinary native entry applied Meal Ticket +15 HP and active Maw Bank +12 gold exactly once");
            stage = checks[1];
            Require(ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) == null, "Fresh shop refresh unavailable.");
            var entry = inventory.CharacterCardEntries.First(card => !card.IsOnSale);
            var rawPrice = (int)HarmonyLib.AccessTools.Field(typeof(MerchantEntry), "_cost").GetValue(entry)!;
            Require(entry.Cost == rawPrice, "Fixture unexpectedly begins with another shop-price modifier.");
            await Timed(RelicCmd.Obtain<MembershipCard>(player), "native Membership Card fixture");
            Require(entry.Cost == (int)(rawPrice * 0.5m), "Native Membership Card discount not reflected in existing stock.");
            await Timed(RelicCmd.Obtain<TheCourier>(player), "native Courier fixture");
            Require(entry.Cost == (int)(rawPrice * 0.5m * 0.8m), "Native Courier and Membership Card prices do not stack.");
            var shopsRng = RngState(player.PlayerRng.Shops);
            var rewardsRng = RngState(player.PlayerRng.Rewards);
            var before = JsonSerializer.Serialize(ShopService.CaptureState());
            var coord = Bridge.State.CurrentMapCoord;
            await Operation(CoreOperation.ShopReroll);
            Require(ReferenceEquals(room, Bridge.State.CurrentRoom) && Bridge.State.CurrentMapCoord == coord,
                "Shop refresh changed native room or map location.");
            var refreshed = room.GetLocalInventory();
            Require(!ReferenceEquals(inventory, refreshed) && refreshed.Player.NetId == player.NetId,
                "Shop refresh failed to replace the targeted player's inventory.");
            Require(JsonSerializer.Serialize(ShopService.CaptureState()) != before, "Shop refresh did not commit new stock record.");
            Require(RngState(player.PlayerRng.Shops) == shopsRng &&
                RngState(player.PlayerRng.Rewards) == rewardsRng,
                $"Shop refresh consumed unrelated native player RNG streams. Shops before={shopsRng} after={RngState(player.PlayerRng.Shops)}; Rewards before={rewardsRng} after={RngState(player.PlayerRng.Rewards)}");
            foreach (var offer in refreshed.AllEntries.Where(offer => offer.IsStocked))
            {
                int raw = (int)HarmonyLib.AccessTools.Field(typeof(MerchantEntry), "_cost").GetValue(offer)!;
                Require(offer.Cost == (int)(raw * 0.5m * 0.8m), "Refreshed native offer lost live relic pricing.");
            }
            State["shop_refreshed_prices"] = refreshed.AllEntries.Where(offer => offer.IsStocked).Select(offer => offer.Cost).ToArray();
            Pass(stage, "native targeted inventory regenerated; Membership Card and Courier live discounts retained; Shops/Rewards RNG unchanged");

            stage = "shop_reroll_continue_no_entry_effects";
            Require(!player.Relics.OfType<MawBank>().Single().HasItemBeenBought && player.Creature.CurrentHp < player.Creature.MaxHp,
                "Rerolled shop fixture lost active Maw Bank or existing injury.");
            room = await ContinueShopWithoutEntryEffects();
            inventory = room.GetLocalInventory();
            player = inventory.Player;
            refreshed = inventory;
            Require(ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) == null &&
                !player.Relics.OfType<MawBank>().Single().HasItemBeenBought,
                "Rerolled stock continue lost unpurchased/active Maw Bank state.");
            Pass(stage, "actual disk save/read and native continue after shop reroll retained injury and gold; no repeated Meal Ticket/Maw Bank entry effect");

            stage = checks[2];
            await Timed(PlayerCmd.SetGold(0, player), "failed-purchase gold fixture");
            var card = refreshed.CharacterCardEntries.First(offer => offer.IsStocked && offer.Cost > 0);
            var failed = card.OnTryPurchaseWrapper(refreshed);
            await Timed(failed, "native insufficient-gold purchase");
            Require(!failed.Result && ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) == null,
                "Failed native purchase locked shop refresh.");
            await Operation(CoreOperation.ShopReroll);
            Require(ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) == null,
                "Failed purchase prevented a real subsequent refresh.");
            Pass(stage, "actual insufficient-gold native purchase failed; a subsequent targeted refresh succeeded");

            stage = checks[3];
            refreshed = room.GetLocalInventory();
            card = refreshed.CharacterCardEntries.First(offer => offer.IsStocked && offer.Cost > 0);
            await Timed(PlayerCmd.SetGold(10000, player), "successful-purchase gold fixture");
            int oldGold = player.Gold;
            int oldDeck = player.Deck.Cards.Count;
            var purchase = card.OnTryPurchaseWrapper(refreshed);
            await Timed(purchase, "native Courier card purchase");
            Require(purchase.Result && player.Deck.Cards.Count == oldDeck + 1 && player.Gold < oldGold,
                "Native card purchase did not mutate deck/gold.");
            Require(card.IsStocked, "Native Courier did not restock the bought entry.");
            Require(ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) != null,
                "Successful Courier-restocked purchase did not lock shop refresh.");
            var afterPurchase = JsonSerializer.Serialize(ShopService.CaptureState());
            bool rejected = false;
            try { await ShopService.ExecuteAsync(123UL, player.NetId, ConfigStore.Rules); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && JsonSerializer.Serialize(ShopService.CaptureState()) == afterPurchase,
                "Purchase lock allowed refresh or changed stock on rejection.");
            Pass(stage, "actual native card purchase changed deck/gold; Courier restocked entry; refresh still rejected");

            stage = "shop_purchase_continue_no_entry_effects";
            Require(player.Relics.OfType<MawBank>().Single().HasItemBeenBought && player.Creature.CurrentHp < player.Creature.MaxHp,
                "Purchased shop fixture lost native Maw Bank deactivation or existing injury.");
            room = await ContinueShopWithoutEntryEffects();
            player = room.GetLocalInventory().Player;
            Require(ShopService.GetUnavailableReason(player.NetId, ConfigStore.Rules) != null &&
                player.Relics.OfType<MawBank>().Single().HasItemBeenBought,
                "Purchased stock continue lost purchase lock or native Maw Bank state.");
            Pass(stage, "actual purchased-stock disk save/read and native continue retained injury, gold, purchase lock and spent Maw Bank");
        }
        catch (Exception error)
        {
            Results[stage] = "FAIL: " + error.Message;
            throw;
        }
    }

    private static async Task<MerchantRoom> ContinueShopWithoutEntryEffects()
    {
        var manager = RunManager.Instance;
        var player = ((MerchantRoom)Bridge.State.CurrentRoom!).GetLocalInventory().Player;
        int hp = player.Creature.CurrentHp;
        int gold = player.Gold;
        await Timed(CompanionStore.SaveNativeCurrent(), "native current shop disk save");
        var saved = SaveManager.Instance.LoadRunSave();
        Require(saved.Success && saved.SaveData != null && CompanionStore.Error == null,
            "Native shop disk/companion pairing failed.");
        var loaded = RunState.FromSerializable(saved.SaveData!);
        manager.EventSynchronizer.Dispose();
        manager.OneOffSynchronizer.Dispose();
        manager.CleanUp();
        await Timed(manager.SetUpSavedSingleplayer(loaded, saved.SaveData!), "native saved shop setup");
        NGame.Instance!.ReactionContainer.InitializeNetworking(manager.NetService);
        await Timed(NGame.Instance.LoadRun(loaded, saved.SaveData!.PreFinishedRoom), "native saved shop continue");
        Require(Bridge.State.CurrentRoom is MerchantRoom, "Native saved shop continue did not reopen merchant.");
        var room = (MerchantRoom)Bridge.State.CurrentRoom!;
        var restored = room.GetLocalInventory().Player;
        Require(restored.Creature.CurrentHp == hp && restored.Gold == gold,
            $"Shop continue repeated entry effects: HP {hp}->{restored.Creature.CurrentHp}, gold {gold}->{restored.Gold}.");
        return room;
    }

    private static async Task IncreaseBoss(int percent)
    {
        var revision = HostCoordinator.WorldRevision;
        HostCoordinator.Submit(CoreOperation.BossHealthIncrease, percent: percent);
        await Wait(() => !HostCoordinator.Busy, "boss health operation", 60000);
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成",
            "Boss health coordinator operation failed: " + HostCoordinator.Status);
    }

    private static string RngState(MegaCrit.Sts2.Core.Random.Rng rng)
    {
#if STS2_STABLE
        return $"{rng.Seed}:{rng.Counter}";
#else
        return JsonSerializer.Serialize(rng.ToSerializable(), new JsonSerializerOptions { IncludeFields = true });
#endif
    }

    private static void CheckBossMax(Dictionary<uint, int> expected)
    {
        var enemies = ((CombatRoom)Bridge.State.CurrentRoom!).Enemies.ToDictionary(e => e.CombatId!.Value, e => e.MaxHp);
        Require(enemies.Count == expected.Count && expected.All(pair => enemies.GetValueOrDefault(pair.Key) == pair.Value),
            "Native reconstructed boss max HP differs or compounded.");
    }
    private static string EnemyHealth() => JsonSerializer.Serialize(((CombatRoom)Bridge.State.CurrentRoom!).Enemies
        .Select(e => new { id = e.CombatId, max = e.MaxHp, hp = e.CurrentHp }).ToArray());
    private static string Digests() => string.Join("/", CombatService.CurrentOrderDigests.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));
    private static Task Opening() => Wait(() => CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsStarting &&
        RunManager.Instance.ActionQueueSynchronizer.CombatState == ActionSynchronizerCombatState.PlayPhase &&
        CombatService.CurrentOrderDigests.Count == Bridge.State.Players.Count, "native opening draw", 60000);
    private static async Task AwaitStartup()
    {
#if STS2_STABLE
        await Wait(() => NGame.Instance?.MainMenu?.IsNodeReady() == true, "native main menu", 60000);
        await Timed(PreloadManager.LoadCommonAndMainMenuAssets(), "deferred startup assets");
#else
        await Wait(() => NGame.Instance?.GameStartupComplete.IsCompleted == true, "game startup", 60000);
        await NGame.Instance!.GameStartupComplete;
#endif
    }
    private static async Task Operation(CoreOperation kind, long checkpoint = 0)
    {
        var revision = HostCoordinator.WorldRevision;
        HostCoordinator.Submit(kind, checkpoint);
        await Wait(() => !HostCoordinator.Busy, kind.ToString(), 60000);
        Require(HostCoordinator.WorldRevision == revision + 1 && HostCoordinator.Status == "操作完成", kind + " failed: " + HostCoordinator.Status);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pass(string check, string detail) { Results[check] = "PASS: " + detail; Log.Info("[NoSuffering] EXPANSION_PROBE " + check + " " + Results[check]); }
    private static async Task Timed(Task task, string detail) { await Wait(() => task.IsCompleted, detail, 60000); await task; }
    private static async Task Wait(Func<bool> done, string detail, ulong timeout = 30000)
    {
        var deadline = Time.GetTicksMsec() + timeout;
        while (!done()) { if (Time.GetTicksMsec() >= deadline) throw new TimeoutException("Timed out: " + detail); await Bridge.Frame(); }
    }
}
