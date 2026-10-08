using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace NoSuffering.Combat;

// Persist the native RNG's immutable value data for the selected game branch.
public sealed record ParentEventPlayer(ulong PlayerId, int Counter,
#if STS2_STABLE
    uint Seed,
#else
    ulong State0, ulong State1, ulong State2, ulong State3,
#endif
    bool IsFinished, bool CleanupCalled, bool StartedFight,
    string? DescriptionTable, string? DescriptionKey);

public sealed record ParentEventRecord(string ModelId, bool IsPreFinished,
    List<ParentEventPlayer> Players);

public static class ParentEventState
{
    public static ParentEventRecord? Capture(CombatRoom combat)
    {
        if (combat.ParentEventId is null) return null;
        if (UnavailableReason(combat) is not null) return null;
        var run = combat.CombatState.RunState;
        if (run.BaseRoom is not EventRoom parent || parent.ModelId != combat.ParentEventId)
            throw new InvalidOperationException("Event combat has no matching parent event room.");
        ValidateSupported(parent.CanonicalEvent, combat.ShouldResumeParentEventAfterCombat);
        var events = RunManager.Instance.EventSynchronizer.Events;
        if (events.Count != run.Players.Count || events.Any(item => item.Owner is null || item.Id != parent.ModelId))
            throw new InvalidOperationException("Parent event player models do not match the combat roster.");
        return new ParentEventRecord(parent.ModelId.ToString(), parent.IsPreFinished,
            events.Select(item =>
            {
#if STS2_STABLE
                var rng = item.Rng;
                return new ParentEventPlayer(item.Owner!.NetId, rng.Counter, rng.Seed, item.IsFinished,
#else
                var rng = item.Rng.ToSerializable();
                return new ParentEventPlayer(item.Owner!.NetId, rng.counter, rng.state0, rng.state1,
                    rng.state2, rng.state3, item.IsFinished,
#endif
                    (bool)AccessTools.Field(typeof(EventModel), "_cleanupCalled").GetValue(item)!,
                    item is FakeMerchant merchant && merchant.StartedFight,
                    item.Description?.LocTable, item.Description?.LocEntryKey);
            }).ToList());
    }

    public static string? UnavailableReason(CombatRoom combat)
    {
        if (combat.ParentEventId is null) return null;
        if (combat.CombatState.RunState.BaseRoom is not EventRoom parent || parent.ModelId != combat.ParentEventId)
            return "当前战斗缺少可恢复的父事件";
        return IsSupported(parent.CanonicalEvent, combat.ShouldResumeParentEventAfterCombat)
            ? null : "当前事件战斗暂不支持重开";
    }

    // Use after RunManager has initialized the saved run and before it enters any room.
    // The native internal entry method preserves room IDs and lifecycle notifications,
    // without appending another history room or rerunning parent event entry effects.
    public static async Task RestoreCombatAsync(CombatRoom combat, CombatRecord record)
    {
        var data = record.ParentEvent ?? throw new InvalidOperationException("Saved parent event state is missing.");
        var parentId = combat.ParentEventId ?? throw new InvalidOperationException("Combat has no parent event ID.");
        if (parentId.ToString() != data.ModelId)
            throw new InvalidOperationException("Saved parent event does not match the loaded combat.");
        var canonical = SaveUtil.EventOrDeprecated(parentId);
        ValidateSupported(canonical, combat.ShouldResumeParentEventAfterCombat);
        var run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("No native run is initialized.");
        if (run.CurrentRoomCount != 0)
            throw new InvalidOperationException("Parent combat restoration requires an empty native room stack.");
        if (!data.Players.Select(player => player.PlayerId).Order().SequenceEqual(run.Players.Select(player => player.NetId).Order()))
            throw new InvalidOperationException("Saved parent event roster differs from the loaded run.");
        var parent = new RestoredParentEventRoom(canonical, data);
        var enter = AccessTools.Method(typeof(RunManager), "EnterRoomInternal",
            new[] { typeof(AbstractRoom), typeof(bool) });
        await (Task)enter.Invoke(RunManager.Instance, new object[] { parent, true })!;
        await (Task)enter.Invoke(RunManager.Instance, new object[] { combat, false })!;
    }

    private static void ValidateSupported(EventModel canonical, bool resume)
    {
        if (IsSupported(canonical, resume)) return;
        throw new InvalidOperationException($"Parent event {canonical.Id} has no verified combat resume serializer.");
    }

    private static bool IsSupported(EventModel canonical, bool resume) => resume
        ? canonical is BattlewornDummy
        : canonical is DenseVegetation or PunchOff or TheLanternKey or FakeMerchant;

    private sealed class RestoredParentEventRoom : EventRoom
    {
        private readonly ParentEventRecord _data;

        public RestoredParentEventRoom(EventModel canonical, ParentEventRecord data) : base(canonical)
        {
            _data = data;
            if (data.IsPreFinished) MarkPreFinished();
        }

        public override Task EnterInternal(IRunState? runState, bool isRestoringRoomStackBase)
        {
            if (!isRestoringRoomStackBase || runState is null)
                throw new InvalidOperationException("Parent event adapter only supports native stack reconstruction.");
            var sync = RunManager.Instance.EventSynchronizer;
            var events = (List<EventModel>)AccessTools.Field(typeof(EventSynchronizer), "_events").GetValue(sync)!;
            if (events.Count != 0)
                throw new InvalidOperationException("Parent event restoration requires a fresh native event synchronizer.");
            AccessTools.Field(typeof(EventSynchronizer), "_canonicalEvent").SetValue(sync, CanonicalEvent);
            var combatSync = AccessTools.Field(typeof(EventSynchronizer), "_combatSynchronizer").GetValue(sync);
            var changed = (Action<EventModel>)AccessTools.Method(typeof(EventRoom), "OnEventStateChanged")
                .CreateDelegate(typeof(Action<EventModel>), this);
            foreach (var player in runState.Players)
            {
                var saved = _data.Players.Single(item => item.PlayerId == player.NetId);
                var model = CanonicalEvent.ToMutable();
                AccessTools.Property(typeof(EventModel), nameof(EventModel.Owner)).SetValue(model, player);
                AccessTools.Property(typeof(EventModel), nameof(EventModel.Rng)).SetValue(model,
#if STS2_STABLE
                    new Rng(saved.Seed, saved.Counter));
#else
                    new Rng(new SerializableRng { counter = saved.Counter, state0 = saved.State0,
                        state1 = saved.State1, state2 = saved.State2, state3 = saved.State3 }));
#endif
                AccessTools.Field(typeof(EventModel), "_combatSynchronizer").SetValue(model, combatSync);
                AccessTools.Field(typeof(EventModel), "_isFinished").SetValue(model, saved.IsFinished);
                AccessTools.Field(typeof(EventModel), "_cleanupCalled").SetValue(model, saved.CleanupCalled);
                AccessTools.Field(typeof(EventModel), "_currentOptions").SetValue(model, new List<EventOption>());
                if (saved.DescriptionTable is not null && saved.DescriptionKey is not null)
                    AccessTools.Property(typeof(EventModel), nameof(EventModel.Description)).SetValue(model,
                        new LocString(saved.DescriptionTable, saved.DescriptionKey));
                if (model is FakeMerchant)
                    AccessTools.Field(typeof(FakeMerchant), "_startedFight").SetValue(model, saved.StartedFight);
                model.StateChanged += changed;
                events.Add(model);
            }
            // No BeginEvent, CalculateVars, layout generation, room hooks, healing or UI.
            // Native EventRoom.Resume invokes every restored player's Resume after combat.
            return Task.CompletedTask;
        }
    }
}
