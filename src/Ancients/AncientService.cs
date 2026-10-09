using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Saves;
using NoSuffering.Multiplayer;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Players;
#if STS2_STABLE
using MegaCrit.Sts2.Core.Entities.Rngs;
#endif
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Config;

namespace NoSuffering.Ancients;

// Companion-save data: pair this with the same native run commit. The HP values are
// generation context only; never use them to restore or refund current HP.
public sealed record AncientRecord(string Context, string AncientId, ulong Seed,
    int AncientRerolls, int OptionsRerolls, bool Committed, Dictionary<ulong, int> GenerationHp)
{
    public Dictionary<ulong, AncientPlayerRecord> Players { get; init; } = [];
}
public sealed record AncientPlayerRecord(ulong Seed, int GenerationHp, int Rerolls, bool Committed, bool Finished, string? EventRng = null, string? RewardsRng = null, int ChoiceRevision = 0);

public static class AncientService
{
    private static readonly FieldInfo EventsField = AccessTools.Field(typeof(EventSynchronizer), "_events");
    private static readonly FieldInfo PendingTasksField = AccessTools.Field(typeof(EventSynchronizer), "_pendingOptionTasks");
    private static readonly FieldInfo CanonicalField = AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent");
    private static readonly FieldInfo SharedSubset = AccessTools.Field(typeof(ActModel), "_sharedAncientSubset");
    private static readonly FieldInfo RoomsField = AccessTools.Field(typeof(ActModel), "_rooms");
    private static readonly MethodInfo InitialState = AccessTools.Method(typeof(AncientEventModel), "SetInitialEventState");
    private static AncientRecord? _state;
    private static EventRoom? _committedRoom;
    private static bool _busy;
    private static bool _restorePending;

    public static bool Available => UnavailableReason == null;
    public static string? UnavailableReason
    {
        get
        {
            var manager = RunManager.Instance;
            if (!manager.IsInProgress || State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel)
                return "当前不是先古之民事件";
            if (_busy) return "操作同步中";
            if (room.IsPreFinished || ReferenceEquals(_committedRoom, room) ||
                manager.EventSynchronizer.Events.Any(e => e.IsFinished || e.CurrentOptions.Any(o => o.WasChosen)))
                return "已有玩家确认奖励";
            if (_state is { Committed: true } && _state.Context == Context) return "已有玩家确认奖励";
            return null;
        }
    }

    public static string? GetUnavailableReason(bool replaceAncient, ulong playerId, HostRules rules)
    {
        if (replaceAncient && UnavailableReason is { } reason) return reason;
        if (!replaceAncient && PersonalUnavailableReason(playerId) is { } personal) return personal;
        if (replaceAncient ? !rules.EnableAncientReroll : !rules.EnableAncientOptionsReroll) return "此功能已关闭";
        var player = State.Players.SingleOrDefault(p => p.NetId == playerId);
        if (player == null) return "玩家不在本局中";
        var mode = replaceAncient ? rules.AncientCostMode : rules.OptionsCostMode;
        int cost = mode == RefreshCostMode.Free ? 0 : replaceAncient ? rules.AncientHpCost : rules.OptionsHpCost;
        if (cost != 0 && player.Creature.CurrentHp <= cost) return "生命不足";
        if (replaceAncient)
        {
            var current = ((EventRoom)State.CurrentRoom!).CanonicalEvent!;
            var shared = (List<AncientEventModel>?)SharedSubset.GetValue(State.Act) ?? [];
            if (!State.Act.GetUnlockedAncients(State.UnlockState).Concat(shared).Any(a => a.Id != current.Id && a.IsAllowed(State)))
                return "没有可替换的先古之民";
        }
        return null;
    }

    private static string? PersonalUnavailableReason(ulong playerId)
    {
        if (!RunManager.Instance.IsInProgress || State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel)
            return "当前不是先古之民事件";
        if (_busy) return "操作同步中";
        var player = State.Players.SingleOrDefault(p => p.NetId == playerId);
        if (player == null) return "玩家不在本局中";
        var model = RunManager.Instance.EventSynchronizer.GetEventForPlayer(player);
        if (room.IsPreFinished || model.IsFinished || model.CurrentOptions.Any(o => o.WasChosen) ||
            _state?.Context == Context && _state.Players.GetValueOrDefault(playerId)?.Committed == true)
            return "你已确认奖励";
        return null;
    }

    public static string ChoiceContext(ulong playerId)
    {
        var state = _state?.Context == Context ? _state : null;
        var player = state?.Players.GetValueOrDefault(playerId);
        return $"{Context}:{state?.AncientRerolls ?? 0}:{player?.Rerolls ?? 0}:{player?.ChoiceRevision ?? 0}";
    }
    public static bool IsAncientRoom => RunManager.Instance.IsInProgress && State.CurrentRoom is EventRoom { CanonicalEvent: AncientEventModel };
    public static void Choose(ulong playerId, int index, string context)
    {
        if (!IsAncientRoom || context != ChoiceContext(playerId)) throw new InvalidOperationException("先古奖励已刷新，请重新选择");
        var player = State.Players.Single(p => p.NetId == playerId);
        var model = RunManager.Instance.EventSynchronizer.GetEventForPlayer(player);
        if (model.IsFinished || index < 0 || index >= model.CurrentOptions.Count || model.CurrentOptions[index].WasChosen)
            throw new InvalidOperationException("先古奖励选项已失效");
        AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent").Invoke(RunManager.Instance.EventSynchronizer, [player, index]);
    }

    private static RunState State => RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("没有活动运行状态");
    private static string Context => $"{State.Rng.Seed}:{State.MapLocation}";
    public static AncientRecord? CaptureState() => _state is null ? null : _state with { GenerationHp = new(_state.GenerationHp), Players = new(_state.Players.ToDictionary(p => p.Key, p => p.Value with { Finished = CurrentFinished(p.Key, p.Value.Finished) })) };
    private static bool CurrentFinished(ulong id, bool fallback) => IsAncientRoom && _state?.Context == Context
        ? RunManager.Instance.EventSynchronizer.GetEventForPlayer(State.Players.Single(p => p.NetId == id)).IsFinished : fallback;
    public static void RestoreState(AncientRecord? state)
    {
        _state = state is null ? null : state with { GenerationHp = new(state.GenerationHp), Players = new(state.Players) };
        _restorePending = state != null;
        _committedRoom = null;
    }

    public static void MarkLiveStateSynchronized()
    {
        // A rules/timeline broadcast updates the existing models' companion data;
        // it is not a request to regenerate those already-live event instances.
        if (IsAncientRoom && _state?.Context == Context &&
            ((EventRoom)State.CurrentRoom!).CanonicalEvent.Id.Entry == _state.AncientId &&
            RunManager.Instance.EventSynchronizer.Events.Count == State.Players.Count)
            _restorePending = false;
    }
    public static bool HasUnfinishedCommittedChoices => IsAncientRoom && _state?.Context == Context &&
        _state.Players.Any(p => p.Value.Committed && !CurrentFinished(p.Key, p.Value.Finished));
    public static async Task WaitForPendingChoicesAsync(CancellationToken cancellation)
    {
        if (!IsAncientRoom) return;
        // The native task owns any card/relic selection UI. Await it before
        // pausing the action executor; never freeze the UI needed to finish it.
        var tasks = ((List<Task>)PendingTasksField.GetValue(RunManager.Instance.EventSynchronizer)!).ToArray();
        // Native exit owns clearing its task list. A timed-out preparation must
        // not leave a background native drain that later clears newer choices.
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20), cancellation); }
        catch (TimeoutException) { throw new InvalidOperationException("请先完成正在选择的先古奖励"); }
    }

    // Called on every peer ONLY by the host-authorized, ordered coordinator. The
    // coordinator owns operation IDs, deduplication, input lock and native saves.
    public static async Task ExecuteAsync(bool replaceAncient, ulong seed, ulong hostPlayerId, HostRules rules)
    {
        if (GetUnavailableReason(replaceAncient, hostPlayerId, rules) is { } unavailable) throw new InvalidOperationException(unavailable);
        if (replaceAncient ? !rules.EnableAncientReroll : !rules.EnableAncientOptionsReroll)
            throw new InvalidOperationException("此功能已关闭");
        var manager = RunManager.Instance;
        var run = State;
        var oldRoom = (EventRoom)run.CurrentRoom!;
        var host = run.Players.Single(p => p.NetId == hostPlayerId);
        var mode = replaceAncient ? rules.AncientCostMode : rules.OptionsCostMode;
        int cost = mode == RefreshCostMode.Free ? 0 : replaceAncient ? rules.AncientHpCost : rules.OptionsHpCost;
        if (cost < 0 || (mode == RefreshCostMode.Hp && cost == 0)) throw new InvalidOperationException("生命费用无效");
        if (cost != 0 && host.Creature.CurrentHp <= cost) throw new InvalidOperationException("生命不足");
        var canonical = (AncientEventModel)oldRoom.CanonicalEvent!;
        if (replaceAncient)
        {
            // This is exactly ActModel.GenerateRooms' uniform candidate enumeration,
            // including the act's already-assigned shared subset, not all shared ancients.
            var shared = (List<AncientEventModel>?)SharedSubset.GetValue(run.Act) ?? [];
            var candidates = run.Act.GetUnlockedAncients(run.UnlockState).Concat(shared)
                .Where(a => a.Id != canonical.Id && a.IsAllowed(run)).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("没有可替换的先古之民");
            canonical = CreateRng(seed, "nosuffering_ancient").NextItem(candidates)!;
        }
        var generationHp = run.Players.ToDictionary(p => p.NetId, p => p.Creature.CurrentHp);
        _busy = true;
        try
        {
            // Generate before touching the current room. Opening/redrawing the UI
            // never calls this path. Native generators keep per-character legality.
            var generated = Generate(canonical!, seed, generationHp, replaceAncient ? null : hostPlayerId);
            if (generated.Any(e => e.IsFinished || e.CurrentOptions.Count == 0 || e.CurrentOptions.All(o => o.IsProceed || o.IsLocked)))
                throw new InvalidOperationException("不存在可生成的奖励");
            if (!replaceAncient && generated[0].CurrentOptions.Count != manager.EventSynchronizer.GetEventForPlayer(host).CurrentOptions.Count)
                throw new InvalidOperationException("原生奖励生成数量发生变化");
            await PreloadManager.LoadRoomEventAssets(canonical, run);
            if (!ReferenceEquals(run.CurrentRoom, oldRoom) || (replaceAncient ? ReferenceEquals(_committedRoom, oldRoom) : PersonalUnavailableReasonIgnoringBusy(hostPlayerId)))
                throw new InvalidOperationException("先古之民已失效或奖励已确认");
            if (replaceAncient)
            {
                var nextRoom = new RerolledAncientRoom(canonical, generated);
                await oldRoom.Exit(run);
                run.PopCurrentRoom();
                run.PushRoom(nextRoom);
                await nextRoom.Enter(run, true);
                ((RoomSet)RoomsField.GetValue(run.Act)!).Ancient = canonical;
                manager.RunLocationTargetedBuffer.OnLocationChanged(run.RunLocation);
            }
            else
            {
                // Replace exactly one model. Other players retain their options,
                // callbacks, completion state, and in-flight reward choices.
                var target = (List<EventModel>)EventsField.GetValue(manager.EventSynchronizer)!;
                int slot = target.FindIndex(e => e.Owner!.NetId == hostPlayerId);
                var onChanged = (Action<EventModel>)Delegate.CreateDelegate(typeof(Action<EventModel>), oldRoom,
                    AccessTools.Method(typeof(EventRoom), "OnEventStateChanged"));
                target[slot].StateChanged -= onChanged;
                target[slot].EnsureCleanup();
                target[slot] = generated[0];
                target[slot].StateChanged += onChanged;
                if (hostPlayerId == manager.NetService.NetId)
                    NRun.Instance?.SetCurrentRoom(NEventRoom.Create(target[slot], run, false));
            }
            host.Creature.SetCurrentHpInternal(host.Creature.CurrentHp - cost);
            var prior = _state?.Context == Context ? _state : null;
            _state = new AncientRecord(Context, canonical!.Id.Entry, seed,
                (prior?.AncientRerolls ?? 0) + (replaceAncient ? 1 : 0),
                (prior?.OptionsRerolls ?? 0) + (replaceAncient ? 0 : 1), !replaceAncient && (prior?.Committed ?? false), generationHp)
            { Players = replaceAncient ? run.Players.ToDictionary(p => p.NetId, p => new AncientPlayerRecord(seed, generationHp[p.NetId], 0, false, false))
                : new(prior?.Players ?? []) };
            if (!replaceAncient) _state.Players[hostPlayerId] = new(seed, generationHp[hostPlayerId], (prior?.Players.GetValueOrDefault(hostPlayerId)?.Rerolls ?? 0) + 1, false, false);
            _committedRoom = null;
        }
        finally { _busy = false; }
    }

    private static bool PersonalUnavailableReasonIgnoringBusy(ulong id)
    {
        var model = RunManager.Instance.EventSynchronizer.GetEventForPlayer(State.Players.Single(p => p.NetId == id));
        return model.IsFinished || model.CurrentOptions.Any(o => o.WasChosen) || _state?.Players.GetValueOrDefault(id)?.Committed == true;
    }
    private static readonly JsonSerializerOptions RngJson = new() { IncludeFields = true };
#if STS2_STABLE
    private sealed record NativeRngState(uint Seed, int Counter);
    private static string WriteRng(Rng rng) => JsonSerializer.Serialize(new NativeRngState(rng.Seed, rng.Counter));
    private static Rng ReadRng(string json)
    {
        var state = JsonSerializer.Deserialize<NativeRngState>(json) ?? throw new InvalidOperationException("先古随机状态缺失");
        return new Rng(state.Seed, state.Counter);
    }
#else
    private static string WriteRng(Rng rng) => JsonSerializer.Serialize(rng.ToSerializable(), RngJson);
    private static Rng ReadRng(string json) => new(JsonSerializer.Deserialize<SerializableRng>(json, RngJson)!);
#endif

    // Capture native initial generation after entry healing, before any generator
    // consumes RNG. This lets untouched teammates reload without re-healing or
    // re-rolling them when somebody else uses a personal reroll.
    [HarmonyPatch(typeof(AncientEventModel), "SetInitialEventState")]
    private static class InitialGenerationPatch
    {
        private static void Prefix(AncientEventModel __instance, bool isPreFinished)
        {
            if (_busy || isPreFinished) return;
            if (_restorePending && _state?.Context == Context && _state.AncientId == __instance.Id.Entry) return;
            _restorePending = false;
            if (_state?.Context != Context) _state = new(Context, __instance.Id.Entry, 0, 0, 0, false, []);
            var player = __instance.Owner!;
            _state.GenerationHp[player.NetId] = player.Creature.CurrentHp;
            _state.Players[player.NetId] = new(0, player.Creature.CurrentHp, 0, false, false,
                WriteRng(__instance.Rng),
                WriteRng(player.PlayerRng.Rewards));
        }
    }

    private static Rng CreateRng(ulong seed, string name)
    {
#if STS2_STABLE
        // Stable's native RNG uses a 32-bit seed; every peer narrows the same
        // host-provided seed before the native deterministic name mixin.
        return new Rng(unchecked((uint)seed), name);
#else
        return new Rng(seed, name);
#endif
    }

    private static List<EventModel> Generate(AncientEventModel canonical, ulong seed, Dictionary<ulong, int> generationHp, ulong? onlyPlayer = null, AncientPlayerRecord? saved = null)
    {
        var result = new List<EventModel>();
        foreach (var player in State.Players.Where(p => onlyPlayer == null || p.NetId == onlyPlayer))
        {
            var mutable = (AncientEventModel)canonical.ToMutable();
            AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner)).SetValue(mutable, player);
            AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng)).SetValue(mutable, saved?.EventRng is { } eventRng ? ReadRng(eventRng) : CreateRng(seed, $"ancient_{player.NetId}"));
            // Darv's DustyTome.SetupForPlayer consumes Rewards. Isolate this narrow
            // generation scope, preserving the live Rewards stream on success/failure.
#if STS2_STABLE
            // Stable cannot reload an individual RNG backwards. Keep the original
            // instance untouched and replace only Rewards during generation.
            var streams = (Dictionary<PlayerRngType, Rng>)AccessTools.Field(typeof(PlayerRngSet), "_rngs")
                .GetValue(player.PlayerRng)!;
            var rewards = streams[PlayerRngType.Rewards];
#else
            var rewards = player.PlayerRng.Rewards.ToSerializable();
#endif
            int hp = player.Creature.CurrentHp;
            try
            {
                player.Creature.SetCurrentHpInternal(generationHp[player.NetId]);
#if STS2_STABLE
                streams[PlayerRngType.Rewards] = saved?.RewardsRng is { } rewardsRng ? ReadRng(rewardsRng) : CreateRng(seed, $"ancient_rewards_{player.NetId}");
#else
                player.PlayerRng.Rewards.LoadFromSerializable((saved?.RewardsRng is { } rewardsRng ? ReadRng(rewardsRng) : CreateRng(seed, $"ancient_rewards_{player.NetId}")).ToSerializable());
#endif
                mutable.CalculateVars();
                InitialState.Invoke(mutable, [false]);
                result.Add(mutable);
            }
            finally
            {
#if STS2_STABLE
                streams[PlayerRngType.Rewards] = rewards;
#else
                player.PlayerRng.Rewards.LoadFromSerializable(rewards);
#endif
                player.Creature.SetCurrentHpInternal(hp);
            }
        }
        return result;
    }

    private static void Install(EventModel canonical, List<EventModel> events)
    {
        var synchronizer = RunManager.Instance.EventSynchronizer;
        var target = (List<EventModel>)EventsField.GetValue(synchronizer)!;
        target.Clear(); target.AddRange(events);
        CanonicalField.SetValue(synchronizer, canonical);
    }

    private sealed class RerolledAncientRoom(AncientEventModel canonical, List<EventModel> events) : EventRoom(canonical)
    {
        public override Task EnterInternal(IRunState? runState, bool isRestoringRoomStackBase)
        {
            Install(canonical, events);
            var onChanged = (Action<EventModel>)Delegate.CreateDelegate(typeof(Action<EventModel>), this,
                AccessTools.Method(typeof(EventRoom), "OnEventStateChanged"));
            foreach (var model in events) model.StateChanged += onChanged;
            NRun.Instance?.SetCurrentRoom(NEventRoom.Create(RunManager.Instance.EventSynchronizer.GetLocalEvent(), runState, false));
            return Task.CompletedTask;
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), "ChooseOptionForEvent")]
    private static class RewardCommitPatch
    {
        private static void Prefix(EventSynchronizer __instance, Player player, int optionIndex)
        {
            if (State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel) return;
            var model = __instance.GetEventForPlayer(player);
            if (optionIndex < 0 || optionIndex >= model.CurrentOptions.Count || model.IsFinished) return;
            _committedRoom = room;
            if (_state?.Context == Context)
            {
                _state = _state with { Committed = true };
                if (_state.Players.TryGetValue(player.NetId, out var entry)) _state.Players[player.NetId] = entry with { Committed = true, ChoiceRevision = entry.ChoiceRevision + 1 };
            }
            else _state = new AncientRecord(Context, room.CanonicalEvent.Id.Entry, 0, 0, 0, true,
                State.Players.ToDictionary(p => p.NetId, p => p.Creature.CurrentHp));
        }
    }

    [HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
    private static class StaleButtonPatch
    {
        private static bool Prefix(EventOption option, int index)
        {
            if (State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel) return true;
            // Native SetOptions creates Proceed separately after the event finishes;
            // it is not a reward in EventModel.CurrentOptions. Leave its native
            // action intact, including custom event layouts supplied by other mods.
            if (option.IsProceed) return !_busy;
            var options = RunManager.Instance.EventSynchronizer.GetLocalEvent().CurrentOptions;
            return !_busy && index >= 0 && index < options.Count && ReferenceEquals(option, options[index]);
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.ChooseLocalOption))]
    private static class OrderedChoicePatch
    {
        private static bool Prefix(int index)
        {
            if (!IsAncientRoom) return true;
            if (!HostCoordinator.Busy) HostCoordinator.SubmitAncientChoice(index);
            return false;
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), "HandleEventOptionChosenMessage")]
    private static class StaleRewardPatch
    {
        private static bool Prefix(OptionIndexChosenMessage message)
        {
            if (State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel) return true;
            // Native indices carry no generation token. They cannot distinguish
            // an in-place reroll (or a later choice page) from an older choice.
            if (message.type != OptionIndexType.Event) return true;
            MegaCrit.Sts2.Core.Logging.Log.Warn("[NoSuffering] Ignored ancient choice without generation context; all peers must use the same mod version.");
            return false;
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
    private static class ReloadPatch
    {
        private static bool Prefix(EventModel canonicalEvent, bool isPrefinished)
        {
            if (!_restorePending || _state?.Context != Context || canonicalEvent is not AncientEventModel ancient || canonicalEvent.Id.Entry != _state.AncientId)
                return true;
            List<EventModel> events = [];
            if (_state.Players.Count == 0)
            {
                if (_state.Committed && !isPrefinished) throw new InvalidOperationException("无法安全恢复旧版未完成的先古奖励");
                events = Generate(ancient, _state.Seed, _state.GenerationHp);
                if (_state.Committed) foreach (var model in events) ((AncientEventModel)model).StartPreFinished();
            }
            else foreach (var player in State.Players)
            {
                var saved = _state.Players.GetValueOrDefault(player.NetId) ?? throw new InvalidOperationException("先古奖励缺少玩家生成记录");
                if (saved.Committed && !saved.Finished && !isPrefinished)
                    throw new InvalidOperationException("此玩家仍有未完成的先古奖励选择，不能安全恢复");
                if (saved.Committed || isPrefinished)
                {
                    var mutable = (AncientEventModel)ancient.ToMutable();
                    AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner)).SetValue(mutable, player);
                    AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng)).SetValue(mutable, CreateRng(saved.Seed, $"ancient_{player.NetId}"));
                    mutable.StartPreFinished();
                    events.Add(mutable);
                }
                else events.AddRange(Generate(ancient, saved.Seed, new() { [player.NetId] = saved.GenerationHp }, player.NetId, saved));
            }
            Install(ancient, events);
            _restorePending = false;
            return false; // Skip entry healing AND original reward RNG consumption.
        }
    }
}
