using System.Reflection;
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
    int AncientRerolls, int OptionsRerolls, bool Committed, Dictionary<ulong, int> GenerationHp);

public static class AncientService
{
    private static readonly FieldInfo EventsField = AccessTools.Field(typeof(EventSynchronizer), "_events");
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

    public static string? GetUnavailableReason(bool replaceAncient, ulong hostPlayerId, HostRules rules)
    {
        if (UnavailableReason is { } reason) return reason;
        if (replaceAncient ? !rules.EnableAncientReroll : !rules.EnableAncientOptionsReroll) return "此功能已关闭";
        var host = State.Players.SingleOrDefault(p => p.NetId == hostPlayerId);
        if (host == null) return "房主不在本局中";
        var mode = replaceAncient ? rules.AncientCostMode : rules.OptionsCostMode;
        int cost = mode == RefreshCostMode.Free ? 0 : replaceAncient ? rules.AncientHpCost : rules.OptionsHpCost;
        if (cost != 0 && host.Creature.CurrentHp <= cost) return "房主生命不足";
        if (replaceAncient)
        {
            var current = ((EventRoom)State.CurrentRoom!).CanonicalEvent!;
            var shared = (List<AncientEventModel>?)SharedSubset.GetValue(State.Act) ?? [];
            if (!State.Act.GetUnlockedAncients(State.UnlockState).Concat(shared).Any(a => a.Id != current.Id && a.IsAllowed(State)))
                return "没有可替换的先古之民";
        }
        return null;
    }

    private static RunState State => RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("没有活动运行状态");
    private static string Context => $"{State.Rng.Seed}:{State.MapLocation}";
    public static AncientRecord? CaptureState() => _state is null ? null : _state with { GenerationHp = new(_state.GenerationHp) };
    public static void RestoreState(AncientRecord? state)
    {
        _state = state is null ? null : state with { GenerationHp = new(state.GenerationHp) };
        _restorePending = state != null;
        _committedRoom = null;
    }

    // Called on every peer ONLY by the host-authorized, ordered coordinator. The
    // coordinator owns operation IDs, deduplication, input lock and native saves.
    public static async Task ExecuteAsync(bool replaceAncient, ulong seed, ulong hostPlayerId, HostRules rules)
    {
        if (!Available) throw new InvalidOperationException(UnavailableReason);
        if (replaceAncient ? !rules.EnableAncientReroll : !rules.EnableAncientOptionsReroll)
            throw new InvalidOperationException("此功能已关闭");
        var manager = RunManager.Instance;
        var run = State;
        var oldRoom = (EventRoom)run.CurrentRoom!;
        var host = run.Players.Single(p => p.NetId == hostPlayerId);
        var mode = replaceAncient ? rules.AncientCostMode : rules.OptionsCostMode;
        int cost = mode == RefreshCostMode.Free ? 0 : replaceAncient ? rules.AncientHpCost : rules.OptionsHpCost;
        if (cost < 0 || (mode == RefreshCostMode.Hp && cost == 0)) throw new InvalidOperationException("生命费用无效");
        if (cost != 0 && host.Creature.CurrentHp <= cost) throw new InvalidOperationException("房主生命不足");
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
            var generated = Generate(canonical!, seed, generationHp);
            if (generated.Any(e => e.IsFinished || e.CurrentOptions.Count == 0 || e.CurrentOptions.All(o => o.IsProceed || o.IsLocked)))
                throw new InvalidOperationException("不存在可生成的奖励");
            if (!replaceAncient && generated.Where((e, i) => e.CurrentOptions.Count != manager.EventSynchronizer.Events[i].CurrentOptions.Count).Any())
                throw new InvalidOperationException("原生奖励生成数量发生变化");
            await PreloadManager.LoadRoomEventAssets(canonical, run);
            if (!ReferenceEquals(run.CurrentRoom, oldRoom) || ReferenceEquals(_committedRoom, oldRoom))
                throw new InvalidOperationException("先古之民已失效或奖励已确认");
            var nextRoom = new RerolledAncientRoom(canonical, generated);
            await oldRoom.Exit(run);
            run.PopCurrentRoom();
            run.PushRoom(nextRoom);
            await nextRoom.Enter(run, true);
            // Persist the rolled ancient in the native act and native current room.
            var rooms = (RoomSet)RoomsField.GetValue(run.Act)!;
            rooms.Ancient = canonical;
            manager.RunLocationTargetedBuffer.OnLocationChanged(run.RunLocation);
            host.Creature.SetCurrentHpInternal(host.Creature.CurrentHp - cost);
            var prior = _state?.Context == Context ? _state : null;
            _state = new AncientRecord(Context, canonical.Id.Entry, seed,
                (prior?.AncientRerolls ?? 0) + (replaceAncient ? 1 : 0),
                (prior?.OptionsRerolls ?? 0) + (replaceAncient ? 0 : 1), false, generationHp);
            _committedRoom = null;
        }
        finally { _busy = false; }
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

    private static List<EventModel> Generate(AncientEventModel canonical, ulong seed, Dictionary<ulong, int> generationHp)
    {
        var result = new List<EventModel>();
        foreach (var player in State.Players)
        {
            var mutable = (AncientEventModel)canonical.ToMutable();
            AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner)).SetValue(mutable, player);
            AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng)).SetValue(mutable, CreateRng(seed, $"ancient_{player.NetId}"));
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
                streams[PlayerRngType.Rewards] = CreateRng(seed, $"ancient_rewards_{player.NetId}");
#else
                player.PlayerRng.Rewards.LoadFromSerializable(new Rng(seed, $"ancient_rewards_{player.NetId}").ToSerializable());
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
            if (_state?.Context == Context) _state = _state with { Committed = true };
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

    [HarmonyPatch(typeof(EventSynchronizer), "HandleEventOptionChosenMessage")]
    private static class StaleRewardPatch
    {
        private static bool Prefix(OptionIndexChosenMessage message)
        {
            if (State.CurrentRoom is not EventRoom room || room.CanonicalEvent is not AncientEventModel) return true;
            // The native buffer accepts ANY previously visited location. Ancient
            // rerolls must require the CURRENT new room ID to reject old indices.
            return message.Location.Equals(RunManager.Instance.RunLocationTargetedBuffer.CurrentLocation);
        }
    }

    [HarmonyPatch(typeof(EventSynchronizer), nameof(EventSynchronizer.BeginEvent))]
    private static class ReloadPatch
    {
        private static bool Prefix(EventModel canonicalEvent, bool isPrefinished)
        {
            if (!_restorePending || _state?.Context != Context || canonicalEvent is not AncientEventModel ancient || canonicalEvent.Id.Entry != _state.AncientId)
                return true;
            if (_state.Committed && !isPrefinished)
                throw new InvalidOperationException("原生存档没有保存已提交但未完成的先古奖励流程，不能安全恢复此提交");
            List<EventModel> events;
            if (!_state.Committed) events = Generate(ancient, _state.Seed, _state.GenerationHp);
            else
            {
                events = [];
                foreach (var player in State.Players)
                {
                    var mutable = (AncientEventModel)ancient.ToMutable();
                    AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner)).SetValue(mutable, player);
                    AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng)).SetValue(mutable, CreateRng(_state.Seed, $"ancient_{player.NetId}"));
                    mutable.StartPreFinished();
                    events.Add(mutable);
                }
            }
            Install(ancient, events);
            _restorePending = false;
            return false; // Skip entry healing AND original reward RNG consumption.
        }
    }
}
