using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using NoSuffering.Ancients;
using NoSuffering.Combat;
using NoSuffering.Config;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Checkpoints;

public sealed record TimelineData(List<MapCheckpoint> Checkpoints,CombatRecord? Combat,AncientRecord? Ancient);
public sealed record MapCheckpoint(long Id,string RunId,string Position,string Label,long CreatedAt,string LastRoom,Dictionary<ulong,int> PlayerHp,string Snapshot,CombatRecord? Combat,AncientRecord? Ancient);
public static class CheckpointService
{
    private static List<MapCheckpoint> _history=[];
    private static long _nextId;
    public static IReadOnlyList<MapCheckpoint> History=>_history;
    public static TimelineData Save(bool includeHistory=true)=>new(includeHistory?new(_history):[],CombatService.Save(),AncientService.CaptureState());
    public static void Restore(TimelineData data,bool keepHistory=false) {
        if(!keepHistory)_history=new(data.Checkpoints);
        _nextId=Math.Max(_nextId,_history.Select(p=>p.Id).DefaultIfEmpty().Max());
        CombatService.Restore(data.Combat);AncientService.RestoreState(data.Ancient);
    }
    public static void Reset(){_history=[];CombatService.Restore(null);AncientService.RestoreState(null);}
    public static void CaptureDecision(bool sealing=false) {
        var manager=MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        if(!manager.IsInProgress || Bridge.IsRestoring || Multiplayer.HostCoordinator.Busy ||
           manager.NetService.Type==NetGameType.Client || (!sealing && NMapScreen.Instance is not {IsTravelEnabled:true,IsTraveling:false}))return;
        var state=Bridge.State;
        if(CombatManagerActive() || !AllRewardsFinished(manager))return;
        var position=state.MapLocation.ToString();
        var key=Bridge.RunKey;
        var snapshot=Bridge.Freeze(Bridge.Capture(new MapRoom()));
        var label=$"第 {state.CurrentActIndex+1} 幕 · 第 {state.ActFloor} 层结算后 · 待选下一节点";
        var existing=_history.FindIndex(c=>c.RunId==key && c.Position==position);
        if(existing>=0) {_history[existing]=_history[existing] with {Snapshot=snapshot,Combat=CombatService.Save(),Ancient=AncientService.CaptureState(),PlayerHp=state.Players.ToDictionary(p=>p.NetId,p=>p.Creature.CurrentHp)};CompanionStore.PairWithLastNativeSave();return;}
        _history.Add(new(++_nextId,key,position,label,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),state.CurrentRoom?.RoomType.ToString() ?? "Map",state.Players.ToDictionary(p=>p.NetId,p=>p.Creature.CurrentHp),snapshot,CombatService.Save(),AncientService.CaptureState()));
        Trim();
        CompanionStore.PairWithLastNativeSave();
    }
    private static bool CombatManagerActive()=>MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress;
    private static bool AllRewardsFinished(MegaCrit.Sts2.Core.Runs.RunManager manager) {
        // Native travel can be locally enabled while a teammate is still choosing.
        // Snapshot only the all-player decision boundary, never partial rewards.
        var states=(System.Collections.IEnumerable)AccessTools.Field(manager.RewardsSetSynchronizer.GetType(),"_rewardStates").GetValue(manager.RewardsSetSynchronizer)!;
        foreach(var s in states) {
            var stack=(System.Collections.ICollection)AccessTools.Field(s.GetType(),"rewardsStack").GetValue(s)!;
            if(stack.Count!=0)return false;
        }
        return true;
    }
    public static void Trim(){while(_history.Count>ConfigStore.Rules.CheckpointLimit)_history.RemoveAt(0);}
    public static MapCheckpoint Require(long id)=>_history.Single(c=>c.Id==id && c.RunId==Bridge.RunKey);
    public static void CommittedRollback(long id){var index=_history.FindIndex(c=>c.Id==id);if(index<0)throw new InvalidOperationException("检查点已失效");_history.RemoveRange(index+1,_history.Count-index-1);}
    [HarmonyPatch(typeof(NMapScreen),nameof(NMapScreen.Open))]
    private static class DecisionOpened {static void Postfix()=>CaptureDecision();}
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Runs.RunManager),nameof(MegaCrit.Sts2.Core.Runs.RunManager.EnterMapCoord))]
    private static class DecisionSealed {static void Prefix()=>CaptureDecision(true);}
}
