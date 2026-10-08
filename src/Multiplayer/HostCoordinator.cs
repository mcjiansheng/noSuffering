using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Multiplayer;

public enum CoreOperation { MapRollback,AncientReroll,AncientOptionsReroll,CombatRestart,CombatReroll }
public enum Phase { Hello,State,Prepare,Ready,Commit,Done,Release,Abort }
public struct ControlMessage : INetMessage
{
    public Phase Phase;
    public string Run,Epoch,Payload;
    public long Operation,Revision;
    public bool ShouldBroadcast=>false;
    public bool ShouldBuffer=>false;
    public NetTransferMode Mode=>NetTransferMode.Reliable;
    public LogLevel LogLevel=>LogLevel.Debug;
    public void Serialize(PacketWriter w){w.WriteInt((int)Phase);w.WriteString(Run??"");w.WriteString(Epoch??"");w.WriteLong(Operation);w.WriteLong(Revision);w.WriteString(Payload??"");}
    public void Deserialize(PacketReader r){Phase=(Phase)r.ReadInt();Run=r.ReadString();Epoch=r.ReadString();Operation=r.ReadLong();Revision=r.ReadLong();Payload=r.ReadString();}
}
public sealed record OperationData(CoreOperation Kind,long Checkpoint,ulong Seed,ulong Host,HostRules Rules,string? Snapshot,TimelineData Timeline);
public sealed record SessionData(HostRules Rules,TimelineData Timeline);

public static class HostCoordinator
{
    private static INetGameService? _net;
    private static string _run="",_epoch="";
    private static long _sequence,_activeOperation,_appliedOperation;
    private static readonly HashSet<ulong> Ready=[];
    private static readonly Dictionary<ulong,string> Done=[];
    private static string? _failure;
    private static bool _applyStarted;
    private static CancellationTokenSource? _preparation;
    public static long WorldRevision {get;private set;}
    public static bool Busy {get;private set;}
    public static bool IsHost=>RunManager.Instance.IsInProgress && RunManager.Instance.NetService.Type is NetGameType.Singleplayer or NetGameType.Host;
    public static string Status {get;private set;}="";
    public static event Action? Changed;
    public static void Fail(string reason){_failure=reason;Status=reason;Changed?.Invoke();}
    public static void Attach(INetGameService net) {
        if(ReferenceEquals(net,_net))return;
        if(_net is not null){_net.UnregisterMessageHandler<ControlMessage>(OnMessage);_net.Disconnected-=OnDisconnected;}
        _net=net;net.RegisterMessageHandler<ControlMessage>(OnMessage);net.Disconnected+=OnDisconnected;
    }
    private static void OnDisconnected(NetErrorInfo info){Fail("连接已断开");Release();}
    public static void OnRunReady() {
        var manager=RunManager.Instance;Attach(manager.NetService);
        var run=Bridge.RunKey;
        if(_run!=run) {_run=run;_sequence=0;_appliedOperation=0;WorldRevision=CompanionStore.LoadedRevision;_epoch=IsHost?Guid.NewGuid().ToString("N"):"";}
        ConfigStore.SetMultiplayer(manager.NetService.Type!=NetGameType.Singleplayer);
        ConfigStore.SetRulesWritable(IsHost);
        if(!IsHost)_net!.SendMessage(Message(Phase.Hello));
    }
    public static void RulesChanged() {
        if(IsHost && !Busy && _net?.Type==NetGameType.Host)
            _net.SendMessage(Message(Phase.State,JsonSerializer.Serialize(new SessionData(ConfigStore.Rules,CheckpointService.Save(false)))));
    }
    public static void SyncTo(ulong peer) {
        if(!IsHost || _net?.Type!=NetGameType.Host)return;
        _net.SendMessage(Message(Phase.State,JsonSerializer.Serialize(new SessionData(ConfigStore.Rules,CheckpointService.Save(false)))),peer);
    }
    private static ControlMessage Message(Phase phase,string payload="")=>new(){Phase=phase,Run=_run,Epoch=_epoch,Operation=_activeOperation,Revision=WorldRevision,Payload=payload};
    private static bool FromHost(ulong sender)=>_net is NetClientGameService client && sender==client.HostNetId;
    private static void OnMessage(ControlMessage m,ulong sender) {
        if(m.Phase==Phase.Hello){if(IsHost)SyncTo(sender);return;}
        if(m.Phase==Phase.State) {
            if(!FromHost(sender) || (Busy && m.Epoch!=_epoch))return;
            var data=JsonSerializer.Deserialize<SessionData>(m.Payload) ?? throw new InvalidOperationException("同步状态缺失");
            _run=m.Run;_epoch=m.Epoch;WorldRevision=m.Revision;
            ConfigStore.ApplyHostRules(data.Rules);CheckpointService.Restore(data.Timeline,true);
            return;
        }
        if(m.Run!=_run || m.Epoch!=_epoch)return;
        if(IsHost) {
            if(m.Operation!=_activeOperation || !Bridge.State.Players.Any(p=>p.NetId==sender))return;
            if(m.Phase==Phase.Ready){if(m.Payload!="")Fail(m.Payload);Ready.Add(sender);}
            if(m.Phase==Phase.Done){Done[sender]=m.Payload;}
            return;
        }
        if(!FromHost(sender))return;
        switch(m.Phase) {
            case Phase.Prepare:
                if(Busy || m.Operation<=_appliedOperation || m.Revision!=WorldRevision)return;
                Busy=true;ConfigStore.OperationInProgress=true;_activeOperation=m.Operation;_applyStarted=false;
                Status="等待当前动作完成";Changed?.Invoke();_preparation=new();TaskHelper.RunSafely(ClientPrepare(m,_preparation.Token));break;
            case Phase.Commit:
                if(!Busy || m.Operation!=_activeOperation || _applyStarted || m.Revision!=WorldRevision+1)return;
                _applyStarted=true;WorldRevision=m.Revision;
                TaskHelper.RunSafely(ClientApply(m));break;
            case Phase.Release:
                if(m.Operation==_activeOperation){_appliedOperation=m.Operation;Release();}break;
            case Phase.Abort:
                if(m.Operation==_activeOperation){Status=m.Payload;Release();}break;
        }
    }
    private static async Task ClientPrepare(ControlMessage request,CancellationToken cancellation) {
        string error="";
        try {
            await Bridge.BoundaryAsync(cancellation);
            var kind=Enum.Parse<CoreOperation>(request.Payload);
            if(kind is CoreOperation.AncientReroll or CoreOperation.AncientOptionsReroll && !AncientService.Available)
                error=AncientService.UnavailableReason ?? "先古选择已经提交";
        } catch(OperationCanceledException){return;} catch(Exception e){error=e.Message;}
        if(cancellation.IsCancellationRequested || !Busy || _activeOperation!=request.Operation || _epoch!=request.Epoch)return;
        request.Phase=Phase.Ready;request.Payload=error;
        _net!.SendMessage(request);
    }
    private static async Task ClientApply(ControlMessage message) {
        try {
            var data=JsonSerializer.Deserialize<OperationData>(message.Payload) ?? throw new InvalidOperationException("操作内容缺失");
            await Apply(data);
            _appliedOperation=message.Operation;
            _net!.SendMessage(Message(Phase.Done,Digest()));
        } catch(Exception e) {Fail(e.Message);_net!.SendMessage(Message(Phase.Done,"ERROR:"+e.Message));}
    }
    public static void Submit(CoreOperation kind,long checkpoint=0) {
        if(Busy || !IsHost)return; // Permission is enforced beyond the UI.
        Busy=true;ConfigStore.OperationInProgress=true;_failure=null;_applyStarted=false;
        Status="等待当前动作完成";Changed?.Invoke();TaskHelper.RunSafely(Execute(kind,checkpoint));
    }
    private static async Task Execute(CoreOperation kind,long checkpoint) {
        var oldTimeline=CheckpointService.Save();
        string? recovery=null;
        bool committed=false;
        try {
            OnRunReady();_activeOperation=++_sequence;Ready.Clear();Done.Clear();
            _preparation=new();
            if(_net!.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Prepare,kind.ToString()));
            await Bridge.BoundaryAsync(_preparation.Token);Ready.Add(_net.NetId);
            await Wait(()=>Ready.Count==Bridge.State.Players.Count,"有玩家未完成当前动作");
            if(_failure is not null)throw new InvalidOperationException(_failure);
            Validate(kind);
            recovery=CombatManager.Instance.IsInProgress ? CombatService.Baseline : Bridge.Freeze(Bridge.Capture(Bridge.State.CurrentRoom));
            string? snapshot=null;
            var timeline=CheckpointService.Save(false);
            if(kind==CoreOperation.MapRollback) {
                var target=CheckpointService.Require(checkpoint);
                snapshot=target.Snapshot;timeline=new([],target.Combat,target.Ancient);
            } else if(kind is CoreOperation.CombatRestart or CoreOperation.CombatReroll) {
                snapshot=Bridge.Freeze(CombatService.PrepareRestart(kind==CoreOperation.CombatReroll));
                if(kind==CoreOperation.CombatReroll)CombatService.SetAttempt(CombatService.Attempt+1);
                timeline=CheckpointService.Save(false);
            }
            var data=new OperationData(kind,checkpoint,Bridge.StableSeed(_run,kind.ToString(),_activeOperation.ToString()),_net.NetId,ConfigStore.Rules,snapshot,timeline);
            ++WorldRevision;committed=true;
            Status="正在同步全队";Changed?.Invoke();
            if(_net.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Commit,JsonSerializer.Serialize(data)));
            await Apply(data);
            var digest=Digest();Done[_net.NetId]=digest;
            await Wait(()=>Done.Count==Bridge.State.Players.Count,"有玩家未完成加载");
            if(Done.Values.Any(d=>d!=digest))throw new InvalidOperationException("全队状态不一致，请保存后重新加入");
            if(kind==CoreOperation.MapRollback)CheckpointService.CommittedRollback(checkpoint);
            await CompanionStore.SaveNativeCurrent();
            _appliedOperation=_activeOperation;Status="操作完成";
            if(_net.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Release));
        } catch(Exception e) {
            Status=e.Message;Log.Error("[NoSuffering] "+e);
            if(!committed){CheckpointService.Restore(oldTimeline);if(_net?.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Abort,e.Message));}
            else {
                // Never let only some peers continue the abandoned world.
                // The preserved native save is the recovery point for native rejoin.
                CheckpointService.Restore(oldTimeline);
                if(recovery is not null) {
                    try{await Bridge.LoadAsync(Bridge.Thaw(recovery));}catch(Exception load){Log.Error("[NoSuffering] Recovery failed: "+load);}
                }
                if(_net?.Type==NetGameType.Host)_net.Disconnect(NetError.StateDivergence);
            }
        } finally {Release();}
    }
    private static async Task Apply(OperationData data) {
        ConfigStore.ApplyHostRules(data.Rules);
        if(data.Snapshot is not null){CheckpointService.Restore(data.Timeline,true);await Bridge.LoadAsync(Bridge.Thaw(data.Snapshot));}
        else {await AncientService.ExecuteAsync(data.Kind==CoreOperation.AncientReroll,data.Seed,data.Host,data.Rules);}
        // LoadRun returns before the opening draw turn task completes.
        var deadline=Time.GetTicksMsec()+20000;
        while(CombatManager.Instance.IsStarting || (CombatManager.Instance.IsInProgress && RunManager.Instance.ActionQueueSynchronizer.CombatState!=ActionSynchronizerCombatState.PlayPhase)) {
            if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException("战斗开场尚未完成，请检查选择界面");
            await Bridge.Frame();
        }
    }
    private static void Validate(CoreOperation kind) {
        if(!IsHost)throw new InvalidOperationException("本局由房主控制");
        if(CompanionStore.Error is not null)throw new InvalidOperationException(CompanionStore.Error);
        var r=ConfigStore.Rules;
        if(kind==CoreOperation.MapRollback && !r.EnableMapRollback || kind==CoreOperation.CombatRestart && !r.EnableCombatRestart || kind==CoreOperation.CombatReroll && !r.EnableCombatReroll)throw new InvalidOperationException("此功能已关闭");
        if(kind is CoreOperation.CombatRestart or CoreOperation.CombatReroll && !CombatManager.Instance.IsInProgress)throw new InvalidOperationException("当前没有活动战斗");
        if(kind is CoreOperation.AncientReroll or CoreOperation.AncientOptionsReroll) {
            var reason=AncientService.GetUnavailableReason(kind==CoreOperation.AncientReroll,_net!.NetId,r);
            if(reason is not null)throw new InvalidOperationException(reason);
        }
    }
    private static string Digest() {
        var s=Bridge.Capture(null);
        s.NumReloads=0;s.SaveTime=0;s.RunTime=0;s.PlatformType=default;
        return Bridge.Hash(Bridge.Freeze(s)+JsonSerializer.Serialize(CombatService.CurrentOrderDigests)+JsonSerializer.Serialize(AncientService.CaptureState()));
    }
    private static async Task Wait(Func<bool> condition,string timeout) {
        var deadline=Time.GetTicksMsec()+20000;
        while(!condition()){if(_failure is not null)throw new InvalidOperationException(_failure);if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException(timeout);await Bridge.Frame();}
    }
    private static void Release(){_preparation?.Cancel();_preparation?.Dispose();_preparation=null;Busy=false;ConfigStore.OperationInProgress=false;Bridge.Unlock();Changed?.Invoke();}
    [HarmonyPatch(typeof(NetHostGameService),MethodType.Constructor,[typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo)])]
    private static class AttachHost{static void Postfix(NetHostGameService __instance)=>Attach(__instance);}
    [HarmonyPatch(typeof(NetClientGameService),MethodType.Constructor,[typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo)])]
    private static class AttachClient{static void Postfix(NetClientGameService __instance)=>Attach(__instance);}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.Launch))]
    private static class RunReady{static void Postfix()=>OnRunReady();}
    [HarmonyPatch(typeof(ActionQueueSynchronizer),nameof(ActionQueueSynchronizer.RequestEnqueue))]
    private static class InputBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(EventSynchronizer),nameof(EventSynchronizer.ChooseLocalOption))]
    private static class EventBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(LoadRunLobby),"HandleClientLoadJoinRequestMessage")]
    private static class LoadJoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
    [HarmonyPatch(typeof(RunLobby),"HandleClientRejoinRequestMessage")]
    private static class RejoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
}
