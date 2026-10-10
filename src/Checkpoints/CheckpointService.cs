using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using NoSuffering.Ancients;
using NoSuffering.Combat;
using NoSuffering.Shops;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Checkpoints;

// Native MapLocation has unannotated fields; persist an explicit JSON value.
public readonly record struct CombatSettlement(MapCoord? Coord,int ActIndex)
{
    public static CombatSettlement At(MapLocation location)=>new(location.coord,location.actIndex);
}
public sealed record CombatRewardReplay(MapCoord? Coord,int ActIndex,string Snapshot);
public sealed record TimelineData(List<MapCheckpoint> Checkpoints,CombatRecord? Combat,AncientRecord? Ancient)
{
    public CombatSettlement? SettledCombat {get;init;}
    public CombatRewardReplay? PendingCombatRewards {get;init;}
    public ShopRecord? Shop {get;init;}
    public BossHealthRecord? BossHealth {get;init;}
}
public sealed record MapCheckpoint(long Id,string RunId,string Position,string Label,long CreatedAt,string LastRoom,Dictionary<ulong,int> PlayerHp,string Snapshot,CombatRecord? Combat,AncientRecord? Ancient)
{
    public ShopRecord? Shop {get;init;}
    public BossHealthRecord? BossHealth {get;init;}
    public int ActIndex {get;init;}=-1;
    public MapCoord? Coord {get;init;}
    public bool BeforeEntry {get;init;}
    public bool CompletedCombat {get;init;}
    public bool BeforeRewards {get;init;}
    public bool IsNodeTarget=>Coord.HasValue && (CompletedCombat || BeforeEntry && LastRoom is not ("Monster" or "Elite" or "Boss" or ""));
}
public static class CheckpointService
{
    private static List<MapCheckpoint> _history=[];
    private static long _nextId;
    private static MapLocation? _settledCombat;
    public static CombatRewardReplay? PendingCombatRewards {get;private set;}
    private static CombatRoom? _rewardRoom;
    private static readonly HashSet<ulong> RewardsStarted=[];
    public static IReadOnlyList<MapCheckpoint> History=>_history;
    public static TimelineData Save(bool includeHistory=true)=>new(includeHistory?new(_history):[],CombatService.Save(),AncientService.CaptureState()){SettledCombat=_settledCombat is { } settled?CombatSettlement.At(settled):null,PendingCombatRewards=PendingCombatRewards,Shop=ShopService.CaptureState(),BossHealth=BossHealthService.CaptureState()};
    public static void Restore(TimelineData data,bool keepHistory=false,bool live=false) {
        if(!keepHistory)_history=new(data.Checkpoints);
        _nextId=Math.Max(_nextId,_history.Select(p=>p.Id).DefaultIfEmpty().Max());
        CombatService.Restore(data.Combat);AncientService.RestoreState(data.Ancient);ShopService.RestoreState(data.Shop,live);BossHealthService.RestoreState(data.BossHealth);
        _settledCombat=data.SettledCombat is { } settled?new MapLocation(settled.Coord,settled.ActIndex):null;PendingCombatRewards=data.PendingCombatRewards;_rewardRoom=null;RewardsStarted.Clear();
    }
    public static void Reset(){_history=[];_settledCombat=null;PendingCombatRewards=null;_rewardRoom=null;RewardsStarted.Clear();CombatService.Restore(null);AncientService.RestoreState(null);ShopService.RestoreState(null);BossHealthService.RestoreState(null);}
    // Native entry saves happen after the coordinate is selected and the old room
    // exits, but before rolling/generating the new room or applying its effects.
    private static void CaptureEntry(SerializableRun save) {
        var manager=RunManager.Instance;
        // Saved-run setup writes its reload count before InitializeShared enables
        // ShouldSave and networking. That write is not a new node entry.
        if(!manager.IsInProgress || !manager.ShouldSave || Bridge.IsRestoring || Multiplayer.HostCoordinator.Busy || manager.NetService.Type==NetGameType.Client ||
           Bridge.State.CurrentRoom is not null || save.PreFinishedRoom is not null || save.VisitedMapCoords.Count==0)return;
        var coord=save.VisitedMapCoords[^1];var act=save.CurrentActIndex;
        if(_history.Any(c=>c.ActIndex==act && c.Coord==coord))return;
        _settledCombat=null;PendingCombatRewards=null;
        var position=new MapLocation(coord,act).ToString();
        _history.Add(new(++_nextId,Bridge.Key(save),position,"",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"",Bridge.State.Players.ToDictionary(p=>p.NetId,p=>p.Creature.CurrentHp),Bridge.Freeze(save),null,null)
            {ActIndex=act,Coord=coord,BeforeEntry=true});
    }
    private static void IdentifyEnteredRoom() {
        var manager=RunManager.Instance;
        if(!manager.IsInProgress || Bridge.IsRestoring || manager.NetService.Type==NetGameType.Client || Bridge.State.CurrentRoomCount!=1)return;
        var state=Bridge.State;
        var index=_history.FindIndex(c=>c.ActIndex==state.CurrentActIndex && c.Coord==state.CurrentMapCoord && c.BeforeEntry);
        if(index<0)return;
        _history[index]=_history[index] with {LastRoom=state.CurrentRoom!.RoomType.ToString()};
        CompanionStore.PairWithLastNativeSave();
    }
    private static void CaptureVictoryRewards(CombatRoom room) {
        var manager=RunManager.Instance;
        if(!manager.IsInProgress || Bridge.IsRestoring || manager.NetService.Type==NetGameType.Client ||
           !ReferenceEquals(room,Bridge.State.CurrentRoom) || Bridge.State.CurrentRoomCount!=1 || CompletedCombatAt(Bridge.State.MapLocation))return;
        var state=Bridge.State;
        if(PendingCombatRewards is { } pending && pending.Coord==state.CurrentMapCoord && pending.ActIndex==state.CurrentActIndex)return;
        var existing=_history.FindIndex(c=>c.RunId==Bridge.RunKey && c.Position==state.MapLocation.ToString());
        // Capture once, after victory hooks but before native reward population
        // consumes Rewards RNG or card rarity odds. Claims never replace it.
        var snapshot=Bridge.Freeze(Bridge.Capture(room));
        PendingCombatRewards=new(state.CurrentMapCoord,state.CurrentActIndex,snapshot);
        if(existing>=0 && _history[existing].BeforeRewards)return;
        var record=new MapCheckpoint(existing>=0?_history[existing].Id:++_nextId,Bridge.RunKey,state.MapLocation.ToString(),"",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),room.RoomType.ToString(),state.Players.ToDictionary(p=>p.NetId,p=>p.Creature.CurrentHp),snapshot,null,AncientService.CaptureState())
            {ActIndex=state.CurrentActIndex,Coord=state.CurrentMapCoord,CompletedCombat=true,BeforeRewards=true,Shop=ShopService.CaptureState(),BossHealth=BossHealthService.CaptureState()};
        if(existing>=0)_history[existing]=record;else _history.Add(record);
        CompanionStore.PairWithLastNativeSave();
    }
    public static void CaptureDecision(bool sealing=false) {
        var manager=RunManager.Instance;
        if(!manager.IsInProgress || Bridge.IsRestoring || Multiplayer.HostCoordinator.Busy ||
           manager.NetService.Type==NetGameType.Client || (!sealing && NMapScreen.Instance is not {IsTravelEnabled:true,IsTraveling:false}))return;
        var state=Bridge.State;
        if(MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress || !AllRewardsFinished(manager))return;
        var position=state.MapLocation.ToString();var key=Bridge.RunKey;
        var existing=_history.FindIndex(c=>c.RunId==key && c.Position==position);
        // Noncombat targets keep their immutable entry snapshot, even after a
        // purchase, event choice, rest or chest reward has completed.
        if(existing>=0 && _history[existing].BeforeEntry && _history[existing].LastRoom is not ("Monster" or "Elite" or "Boss"))return;
        bool finishedCombat=state.CurrentRoom is CombatRoom && state.CurrentRoomCount==1;
        if(finishedCombat && _settledCombat!=state.MapLocation &&
           (!ReferenceEquals(_rewardRoom,state.CurrentRoom) || state.Players.Any(p=>!p.Creature.IsDead && !RewardsStarted.Contains(p.NetId))))return;
        var save=Bridge.Capture(finishedCombat?state.CurrentRoom:new MapRoom());
        if(finishedCombat)save.PreFinishedRoom!.IsPreFinished=true;
        if(finishedCombat) {
            _settledCombat=state.MapLocation;PendingCombatRewards=null;
            if(existing>=0 && _history[existing].BeforeRewards){CompanionStore.PairWithLastNativeSave();return;}
        }
        var snapshot=Bridge.Freeze(save);
        var record=new MapCheckpoint(existing>=0?_history[existing].Id:++_nextId,key,position,"",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),state.CurrentRoom?.RoomType.ToString()??"Map",state.Players.ToDictionary(p=>p.NetId,p=>p.Creature.CurrentHp),snapshot,finishedCombat?null:CombatService.Save(),AncientService.CaptureState())
            {ActIndex=state.CurrentActIndex,Coord=state.CurrentMapCoord,CompletedCombat=finishedCombat,Shop=ShopService.CaptureState(),BossHealth=BossHealthService.CaptureState()};
        if(existing>=0)_history[existing]=record;else _history.Add(record);
        CompanionStore.PairWithLastNativeSave();
    }
    private static bool AllRewardsFinished(RunManager manager) {
        var states=(System.Collections.IEnumerable)AccessTools.Field(manager.RewardsSetSynchronizer.GetType(),"_rewardStates").GetValue(manager.RewardsSetSynchronizer)!;
        foreach(var s in states) {
            var stack=(System.Collections.ICollection)AccessTools.Field(s.GetType(),"rewardsStack").GetValue(s)!;
            if(stack.Count!=0)return false;
        }
        return true;
    }
    public static MapCheckpoint Require(long id)=>_history.Single(c=>c.Id==id && c.RunId==Bridge.RunKey);
    public static bool CompletedCombatAt(MapLocation location)=>_settledCombat==location;
    public static void CommittedRollback(long id){var index=_history.FindIndex(c=>c.Id==id);if(index<0)throw new InvalidOperationException("回滚目标已失效");_history.RemoveRange(index+1,_history.Count-index-1);}
    public static void ImportLegacyHistory() {
        // Old versions saved only post-room decisions. Recover genuine completed
        // combat targets; pre-entry resources cannot be reconstructed for events.
        for(int i=0;i<_history.Count;i++) {
            var cp=_history[i];if(cp.ActIndex>=0 || cp.LastRoom is not ("Monster" or "Elite" or "Boss"))continue;
            var save=Bridge.Thaw(cp.Snapshot);if(save.VisitedMapCoords.Count==0)continue;
            _history[i]=cp with {ActIndex=save.CurrentActIndex,Coord=save.VisitedMapCoords[^1],CompletedCombat=true};
        }
    }
    [HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.SaveRun),[typeof(SerializableRun),typeof(bool)])]
    private static class EntrySaved {static void Prefix(SerializableRun save)=>CaptureEntry(save);}
    [HarmonyPatch(typeof(RewardsSetSynchronizer),nameof(RewardsSetSynchronizer.BeginRewardsSet))]
    private static class VictoryRewards {
        static void Postfix(RewardsSet set,ref Task __result) {
            if(!RunManager.Instance.IsInProgress || (Bridge.IsRestoring && PendingCombatRewards is null) || set.Room is not CombatRoom room ||
               !ReferenceEquals(room,Bridge.State.CurrentRoom) || CompletedCombatAt(Bridge.State.MapLocation))return;
            if(!ReferenceEquals(_rewardRoom,room)){_rewardRoom=room;RewardsStarted.Clear();}
            RewardsStarted.Add(set.Player.NetId);
            __result=Finish(__result,room);
        }
        static async Task Finish(Task task,CombatRoom room){await task;await SealVictory(room);}
    }
    [HarmonyPatch(typeof(CombatRoom),nameof(CombatRoom.OfferRoomEndRewards))]
    private static class VictoryRewardBoundary {static void Prefix(CombatRoom __instance)=>CaptureVictoryRewards(__instance);}
    private static async Task SealVictory(CombatRoom room) {
        if(SaveManager.Instance.CurrentRunSaveTask is { } pending)await pending;
        var manager=RunManager.Instance;
        if(!manager.IsInProgress || Bridge.IsRestoring || Multiplayer.HostCoordinator.Busy ||
           manager.NetService.Type==NetGameType.Client || !ReferenceEquals(room,Bridge.State.CurrentRoom))return;
        CaptureDecision(true);
        if(_settledCombat==Bridge.State.MapLocation)await SaveManager.Instance.SaveRun(room,false);
    }
    [HarmonyPatch(typeof(NRewardsScreen),"OnProceedButtonPressed")]
    private static class VictoryProceed {
        static void Prefix(NRewardsScreen __instance) {
            var manager=RunManager.Instance;
            if(!manager.IsInProgress || Bridge.State.CurrentRoom is not CombatRoom || Bridge.State.CurrentRoomCount!=1 ||
               !(bool)AccessTools.Field(typeof(NRewardsScreen),"_isTerminal").GetValue(__instance)!)return;
            var set=(RewardsSet)AccessTools.Field(typeof(NRewardsScreen),"_rewardsSet").GetValue(__instance)!;
            // Native travel otherwise defers this intended skip until room exit,
            // after its map coordinate can already have moved to the next node.
            if(!manager.RewardsSetSynchronizer.IsRewardsSetCompleted(set))manager.RewardsSetSynchronizer.SkipLocalRewardsSet();
        }
    }
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.ProceedFromTerminalRewardsScreen))]
    private static class RewardlessVictory {
        static void Postfix(ref Task __result)=>__result=Finish(__result);
        static async Task Finish(Task task) {
            await task;
            // A native terminal screen normally remains under the map until
            // travel. Its set is now sealed, so remove that stale reward UI.
            if(RunManager.Instance.IsInProgress && Bridge.State.CurrentRoom is CombatRoom &&
               NMapScreen.Instance is {IsOpen:true} && NOverlayStack.Instance?.Peek() is NRewardsScreen screen &&
               (bool)AccessTools.Field(typeof(NRewardsScreen),"_isTerminal").GetValue(screen)! &&
               RunManager.Instance.RewardsSetSynchronizer.IsRewardsSetCompleted((RewardsSet)AccessTools.Field(typeof(NRewardsScreen),"_rewardsSet").GetValue(screen)!))
                NOverlayStack.Instance.Remove(screen);
            if(!RunManager.Instance.IsInProgress || Bridge.State.CurrentRoom is not CombatRoom room || room.Encounter.ShouldGiveRewards)return;
            _rewardRoom=room;RewardsStarted.UnionWith(Bridge.State.Players.Select(p=>p.NetId));await SealVictory(room);
        }
    }
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.EnterRoom))]
    private static class RoomIdentified {
        static void Postfix(ref Task __result)=>__result=Finish(__result);
        static async Task Finish(Task task){await task;IdentifyEnteredRoom();}
    }
    [HarmonyPatch(typeof(NMapScreen),nameof(NMapScreen.Open))]
    private static class DecisionOpened {static void Postfix(){if(!UI.MapRollbackSelection.Active)CaptureDecision();}}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.EnterMapCoord))]
    private static class DecisionSealed {static void Prefix()=>CaptureDecision(true);}
}
