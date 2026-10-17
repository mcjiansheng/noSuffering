using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Shops;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Multiplayer;

public enum CoreOperation { MapRollback,AncientReroll,AncientOptionsReroll,CombatRestart,CombatReroll,ShopReroll,BossHealthIncrease,ShopPurchaseCommit }
public enum Phase { Hello,State,Prepare,Ready,Commit,Done,Release,Abort,Request,AncientChoiceRequest,AncientChoiceCommit,RequestRejected,LoadInstalled,LoadEnter }
public struct ControlMessage : INetMessage
{
    public Phase Phase;
    public string Run,Epoch,Payload;
    public long Operation,Revision;
    public bool ShouldBroadcast=>false;
    public bool ShouldBuffer=>false;
    public NetTransferMode Mode=>NetTransferMode.Reliable;
    public LogLevel LogLevel=>LogLevel.Debug;
    public override string ToString()=>$"NoSuffering.ControlMessage phase={Phase} run={Run} epoch={Epoch} operation={Operation} revision={Revision}";
    public void Serialize(PacketWriter w){w.WriteInt((int)Phase);w.WriteString(Run??"");w.WriteString(Epoch??"");w.WriteLong(Operation);w.WriteLong(Revision);w.WriteString(Payload??"");}
    public void Deserialize(PacketReader r){Phase=(Phase)r.ReadInt();Run=r.ReadString();Epoch=r.ReadString();Operation=r.ReadLong();Revision=r.ReadLong();Payload=r.ReadString();}
}
public sealed record OperationData(CoreOperation Kind,long Checkpoint,ulong Seed,ulong Host,HostRules Rules,string? Snapshot,TimelineData Timeline,ulong Actor=0,int Percent=0);
public sealed record PersonalRequest(CoreOperation Kind,long Checkpoint,int Percent,string Context);
public sealed record AncientChoiceData(ulong Actor,int Index,string Context);
public sealed record PrepareData(CoreOperation Kind,ulong Actor,int Percent);
public sealed record ReadyData(string Error,ShopRecord? Shop);
public sealed record SessionData(HostRules Rules,TimelineData Timeline,long StartTime,string Seed,ulong[] Players);

public static class HostCoordinator
{
    private static INetGameService? _net;
    private static readonly System.Reflection.FieldInfo StartTimeField=AccessTools.Field(typeof(RunManager),"_startTime");
    private static bool _freshRunIdentityPending;
    private static string _run="",_epoch="";
    private static long _sequence,_activeOperation,_appliedOperation,_completionSerial;
    private static readonly HashSet<ulong> Ready=[];
    private static readonly Dictionary<ulong,string> Done=[];
    private static string? _failure;
    private static bool _applyStarted;
    private static CancellationTokenSource? _preparation;
    // Captured before teardown: RunManager temporarily has no live state while
    // peers install replacement native synchronizers on the retained transport.
    private sealed class LoadBarrier(INetGameService net,string run,string epoch,long operation,long revision,ulong[] players,string context)
    {
        public readonly INetGameService Net=net;
        public readonly string Run=run,Epoch=epoch,Context=context;
        public readonly long Operation=operation,Revision=revision;
        public readonly HashSet<ulong> Players=players.ToHashSet(),Installed=[];
        public readonly ulong Host=net is NetClientGameService client?client.HostNetId:net.NetId;
        public bool Entered;
        public ControlMessage Message(Phase phase)=>new(){Phase=phase,Run=Run,Epoch=Epoch,Operation=Operation,Revision=Revision,Payload=Context};
        public bool Matches(ControlMessage message)=>message.Run==Run && message.Epoch==Epoch && message.Operation==Operation && message.Revision==Revision;
    }
    private static LoadBarrier? _loadBarrier;
    public static Func<Task> PrepareSnapshotLoad(string context="operation")
    {
        if(_net?.Type==NetGameType.Singleplayer)return ()=>Task.CompletedTask;
        if(_loadBarrier is not null)throw new InvalidOperationException("上次快照加载尚未结束");
        var barrier=new LoadBarrier(_net!,_run,_epoch,_activeOperation,WorldRevision,Bridge.State.Players.Select(p=>p.NetId).ToArray(),context);
        _loadBarrier=barrier;
        return ()=>AwaitLoadInstallation(barrier);
    }
    private static void TryEnterLoad(LoadBarrier barrier)
    {
        if(barrier.Net.Type!=NetGameType.Host || barrier.Entered || !barrier.Installed.SetEquals(barrier.Players))return;
        barrier.Net.SendMessage(barrier.Message(Phase.LoadEnter));
        barrier.Entered=true;
    }
    private static async Task AwaitLoadInstallation(LoadBarrier barrier)
    {
        try {
            RequireLoadBarrier(barrier);
            barrier.Installed.Add(barrier.Net.NetId);
            if(barrier.Net.Type==NetGameType.Host)TryEnterLoad(barrier);
            else barrier.Net.SendMessage(barrier.Message(Phase.LoadInstalled));
            var deadline=Time.GetTicksMsec()+20000;
            while(!barrier.Entered) {
                RequireLoadBarrier(barrier);
                if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException("有玩家未完成原生加载准备");
                await Bridge.Frame();
            }
            RequireLoadBarrier(barrier);
        } finally {if(ReferenceEquals(_loadBarrier,barrier))_loadBarrier=null;}
    }
    private static void RequireLoadBarrier(LoadBarrier barrier)
    {
        if(_failure is not null)throw new InvalidOperationException(_failure);
        if(!ReferenceEquals(_loadBarrier,barrier) || !ReferenceEquals(_net,barrier.Net) ||
            _run!=barrier.Run || _epoch!=barrier.Epoch || _activeOperation!=barrier.Operation || WorldRevision!=barrier.Revision)
            throw new InvalidOperationException("快照加载会话已结束");
    }
    public static long WorldRevision {get;private set;}
    public static bool Busy {get;private set;}
    private static bool _choicePending;
    private static long _choiceRequest;
    public static bool IsHost=>RunManager.Instance.IsInProgress && RunManager.Instance.NetService is {Type:NetGameType.Singleplayer or NetGameType.Host};
    public static string Status {get;private set;}="";
    public static event Action? Changed;
    public static void Fail(string reason){_failure=reason;Status=reason;Changed?.Invoke();}
    public static void Attach(INetGameService net) {
        if(ReferenceEquals(net,_net))return;
        if(_net is not null){_net.UnregisterMessageHandler<ControlMessage>(OnMessage);_net.Disconnected-=OnDisconnected;}
        _loadBarrier=null;
        _net=net;_freshRunIdentityPending=false;
        // Failures belong to the replaced transport; a retained transport keeps
        // its failure so active preparation still stops on real disconnects.
        _failure=null;
        // A new client transport must receive its own authenticated State before
        // saved-room factories consume companion data. Snapshot loads reuse net.
        if(net.Type==NetGameType.Client){_epoch="";_choicePending=false;++_choiceRequest;}
        net.RegisterMessageHandler<ControlMessage>(OnMessage);net.Disconnected+=OnDisconnected;
    }
    private static void OnDisconnected(NetErrorInfo info){_freshRunIdentityPending=false;Fail("连接已断开");Release();}
    public static void OnRunReady() {
        var manager=RunManager.Instance;Attach(manager.NetService);
        var run=Bridge.RunKey;
        if(_run!=run) {_choicePending=false;++_choiceRequest;_run=run;_sequence=0;_appliedOperation=0;WorldRevision=CompanionStore.LoadedRevision;_epoch=IsHost?Guid.NewGuid().ToString("N"):"";}
        ConfigStore.SetMultiplayer(manager.NetService.Type!=NetGameType.Singleplayer);
        ConfigStore.SetRulesWritable(IsHost);
        if(!IsHost && !Busy && _epoch.Length==0)_net!.SendMessage(Message(Phase.Hello));
    }
    public static void RulesChanged() {
        if(IsHost && !Busy && _net?.Type==NetGameType.Host)
            _net.SendMessage(Message(Phase.State,JsonSerializer.Serialize(Session())));
    }
    public static void SyncTo(ulong peer) {
        if(!IsHost || Busy || _net?.Type!=NetGameType.Host)return;
        _net.SendMessage(Message(Phase.State,JsonSerializer.Serialize(Session())),peer);
    }
    private static SessionData Session() {
        // Launch releases native buffered messages before its postfix. A Hello
        // handled during that release still needs this run's initialized key.
        OnRunReady();
        return new(ConfigStore.Rules,CheckpointService.Save(false),RunManager.Instance.ToSave(null).StartTime,
            Bridge.State.Rng.Seed.ToString(),Bridge.State.Players.Select(p=>p.NetId).Order().ToArray());
    }
    private static ControlMessage Message(Phase phase,string payload="")=>new(){Phase=phase,Run=_run,Epoch=_epoch,Operation=_activeOperation,Revision=WorldRevision,Payload=payload};
    private static bool FromHost(ulong sender)=>_net is NetClientGameService client && sender==client.HostNetId;
    private static void OnMessage(ControlMessage m,ulong sender) {
        // These messages must not consult Bridge.State/IsHost during native cleanup.
        if(m.Phase is Phase.LoadInstalled or Phase.LoadEnter) {
            var barrier=_loadBarrier;
            if(barrier is null || !ReferenceEquals(_net,barrier.Net) || !barrier.Matches(m) || m.Payload!=barrier.Context)return;
            if(m.Phase==Phase.LoadInstalled && barrier.Net.Type==NetGameType.Host && sender!=barrier.Host && barrier.Players.Contains(sender)) {
                barrier.Installed.Add(sender);TryEnterLoad(barrier);
            } else if(m.Phase==Phase.LoadEnter && barrier.Net.Type==NetGameType.Client && sender==barrier.Host)barrier.Entered=true;
            return;
        }
        if(m.Phase==Phase.Done && _loadBarrier is {} loading && loading.Matches(m) &&
            loading.Net.Type==NetGameType.Host && sender!=loading.Host && loading.Players.Contains(sender) &&
            m.Payload.StartsWith("ERROR:",StringComparison.Ordinal)) {
            Fail(m.Payload);return;
        }
        if(m.Phase==Phase.Abort && _loadBarrier is {} active && active.Matches(m) && sender==active.Host) {
            Fail(m.Payload);Release();return;
        }
        if(m.Phase==Phase.Hello){if(IsHost)SyncTo(sender);return;}
        if(m.Phase==Phase.State) {
            if(!FromHost(sender) || (Busy && m.Epoch!=_epoch))return;
            var data=JsonSerializer.Deserialize<SessionData>(m.Payload) ?? throw new InvalidOperationException("同步状态缺失");
            bool initialIdentityAdopted=false;
            if(RunManager.Instance.IsInProgress && m.Run!=Bridge.RunKey) {
                // New multiplayer setup timestamps each peer with its local
                // clock. Adopt the authenticated host's timestamp once, while
                // preserving the durable native-save/checkpoint key algorithm.
                // Saved runs already carry their canonical timestamp unchanged.
                var save=RunManager.Instance.ToSave(null);
                var players=save.Players.Select(p=>p.NetId).Order().ToArray();
                if(!_freshRunIdentityPending || Busy || _epoch.Length>0 || data.Seed!=Bridge.State.Rng.Seed.ToString() ||
                    data.Players is null || !players.SequenceEqual(data.Players.Order())) {
                    Log.Warn($"[NoSuffering] State identity rejected hostRun={m.Run} localRun={Bridge.RunKey} fresh={_freshRunIdentityPending} busy={Busy} epoch={_epoch} hostSeed={data.Seed} localSeed={Bridge.State.Rng.Seed} hostPlayers={string.Join(",",data.Players??[])} localPlayers={string.Join(",",players)}");
                    return;
                }
                long localStart=save.StartTime;save.StartTime=data.StartTime;
                if(Bridge.Key(save)!=m.Run){Log.Warn("[NoSuffering] State identity rejected: host timestamp does not match run key");return;}
                StartTimeField.SetValue(RunManager.Instance,data.StartTime);initialIdentityAdopted=true;
                Log.Info($"[NoSuffering] New multiplayer identity synchronized localStart={localStart} hostStart={data.StartTime} run={m.Run}");
            }
            _freshRunIdentityPending=false;
            // Operation sequence numbers belong to one host lifetime. A native
            // reconnect can retain this run while its restarted host starts at 1.
            if(m.Run!=_run || m.Epoch!=_epoch){_appliedOperation=0;_activeOperation=0;}
            // Initial State may arrive while a cleared native choice UI awaits
            // its first authenticated epoch. Keep that pending selection owned.
            bool replacedSession=m.Run!=_run && !initialIdentityAdopted || _epoch.Length>0 && m.Epoch!=_epoch;
            bool restoreChoice=_choicePending && replacedSession;
            if(replacedSession){_choicePending=false;++_choiceRequest;}
            _run=m.Run;_epoch=m.Epoch;WorldRevision=m.Revision;
            ConfigStore.ApplyHostRules(data.Rules);
            bool liveShop=RunManager.Instance.IsInProgress && Bridge.State.CurrentRoom is MerchantRoom &&
                data.Timeline.Shop?.Context==$"{Bridge.State.Rng.Seed}:{Bridge.State.MapLocation}";
            CheckpointService.Restore(data.Timeline,true,live:liveShop);CompanionStore.AcceptHostState(m.Revision);AncientService.MarkLiveStateSynchronized();
            if(restoreChoice)AncientService.RestoreRejectedLocalChoice();
            return;
        }
        // An authenticated host must be able to reject an obsolete session's
        // request even when the client's local epoch is stale. Re-establish the
        // timeline through State; never replay that old selected option.
        if(m.Phase==Phase.RequestRejected && FromHost(sender) && RunManager.Instance.IsInProgress && m.Run==Bridge.RunKey) {
            bool rejectedChoice=_choicePending;_choicePending=false;++_choiceRequest;
            if(m.Epoch!=_epoch)_epoch="";
            if(rejectedChoice)AncientService.RestoreRejectedLocalChoice();
            Status=m.Payload;++_completionSerial;Changed?.Invoke();
            if(_epoch.Length==0 && !Busy)_net!.SendMessage(Message(Phase.Hello));
            return;
        }
        if(m.Run!=_run || m.Epoch!=_epoch) {
            Log.Warn($"[NoSuffering] Ignored session mismatch from {sender}: {m}; expected run={_run} epoch={_epoch}");
            if(IsHost && (m.Phase is Phase.AncientChoiceRequest or Phase.Request) && Bridge.State.Players.Any(p=>p.NetId==sender))
                _net!.SendMessage(Message(Phase.RequestRejected,"会话已更新，请重新操作"),sender);
            return;
        }
        if(IsHost) {
            if(!Bridge.State.Players.Any(p=>p.NetId==sender))return;
            if(m.Phase==Phase.Request) {
                var request=JsonSerializer.Deserialize<PersonalRequest>(m.Payload);
                if(request is null || !Personal(request.Kind) || Busy || m.Revision!=WorldRevision || request.Context!=RequestContext(request.Kind,sender)) {
                    _net!.SendMessage(Message(Phase.RequestRejected,"请求已失效，请重新操作"),sender);return;
                }
                Start(request.Kind,request.Checkpoint,request.Percent,sender);return;
            }
            if(m.Phase==Phase.AncientChoiceRequest) {
                var choice=JsonSerializer.Deserialize<AncientChoiceData>(m.Payload);
                if(choice is not null && choice.Actor==sender && m.Revision==WorldRevision)AcceptChoice(choice,sender);
                else _net!.SendMessage(Message(Phase.RequestRejected,"选择已失效，请重新操作"),sender);
                return;
            }
            if(m.Operation!=_activeOperation || !Bridge.State.Players.Any(p=>p.NetId==sender))return;
            if(m.Phase==Phase.Ready && !_applyStarted && m.Revision==WorldRevision && !Ready.Contains(sender)){
                try {
                    var prepared=JsonSerializer.Deserialize<ReadyData>(m.Payload) ?? throw new InvalidOperationException("准备状态缺失");
                    if(prepared.Error!="")Fail(prepared.Error);
                    ShopService.MergeOwnedState(prepared.Shop,sender);Ready.Add(sender);
                } catch(Exception e){Fail(e.Message);}
            }
            if(m.Phase==Phase.Done && _applyStarted && m.Revision==WorldRevision){Done[sender]=m.Payload;}
            return;
        }
        if(!FromHost(sender))return;
        switch(m.Phase) {
            case Phase.AncientChoiceCommit:
                if(m.Revision!=WorldRevision || Busy)throw new InvalidOperationException("先古选择顺序与房主不同，请重新加入");
                var choice=JsonSerializer.Deserialize<AncientChoiceData>(m.Payload) ?? throw new InvalidOperationException("选择内容缺失");
                AncientService.Choose(choice.Actor,choice.Index,choice.Context);
                if(choice.Actor==_net!.NetId)_choicePending=false;
                break;
            case Phase.Prepare:
                if(Busy || m.Operation<=_appliedOperation || m.Revision!=WorldRevision)return;
                Busy=true;ConfigStore.OperationInProgress=true;_activeOperation=m.Operation;_applyStarted=false;
                Status="等待当前动作完成";Changed?.Invoke();_preparation=new();TaskHelper.RunSafely(ClientPrepare(m,_preparation.Token));break;
            case Phase.Commit:
                if(!Busy || m.Operation!=_activeOperation || _applyStarted || m.Revision!=WorldRevision+1)return;
                _applyStarted=true;WorldRevision=m.Revision;
                TaskHelper.RunSafely(ClientApply(m));break;
            case Phase.Release:
                if(m.Operation==_activeOperation){_appliedOperation=m.Operation;Status="操作完成";Release();}break;
            case Phase.Abort:
                if(m.Operation==_activeOperation){Status=m.Payload;Release();}break;
        }
    }
    private static async Task PreparationBoundaryAsync(CoreOperation kind,CancellationToken cancellation)
    {
        await AncientService.WaitForPendingChoicesAsync(cancellation);
        await Bridge.BoundaryAsync(cancellation,kind is CoreOperation.MapRollback or CoreOperation.CombatRestart or CoreOperation.CombatReroll);
        if (AncientService.IsAncientRoom)
        {
            // Native reward acquisition updates owner-only discovery/player data.
            // Reconcile through the same native full-player synchronization used
            // by room exit before comparing or saving a personal refresh.
            var sync=RunManager.Instance.CombatStateSynchronizer;
            sync.StartSync();
            await sync.WaitForSync().WaitAsync(TimeSpan.FromSeconds(20),cancellation);
        }
    }
    private static async Task ClientPrepare(ControlMessage request,CancellationToken cancellation) {
        string error="";
        try {
            var prepared=JsonSerializer.Deserialize<PrepareData>(request.Payload) ?? throw new InvalidOperationException("准备内容缺失");
            await PreparationBoundaryAsync(prepared.Kind,cancellation);
            ValidateContext(prepared.Kind,prepared.Actor,prepared.Percent);
        } catch(OperationCanceledException){return;} catch(Exception e){error=e.Message;}
        if(cancellation.IsCancellationRequested || !Busy || _activeOperation!=request.Operation || _epoch!=request.Epoch)return;
        request.Phase=Phase.Ready;request.Payload=JsonSerializer.Serialize(new ReadyData(error,ShopService.CaptureOwnedState()));
        _net!.SendMessage(request);
    }
    private static async Task ClientApply(ControlMessage message) {
        try {
            var data=JsonSerializer.Deserialize<OperationData>(message.Payload) ?? throw new InvalidOperationException("操作内容缺失");
            var installed=data.Snapshot is not null && _net!.Type!=NetGameType.Singleplayer?PrepareSnapshotLoad():null;
            await Apply(data,installed);
            if(!Busy || _activeOperation!=message.Operation || _epoch!=message.Epoch || WorldRevision!=message.Revision)return;
            _appliedOperation=message.Operation;
            _net!.SendMessage(Message(Phase.Done,Digest()));
        } catch(Exception e) {
            Fail(e.Message);
            if(Busy && _activeOperation==message.Operation && _epoch==message.Epoch)_net!.SendMessage(Message(Phase.Done,"ERROR:"+e.Message));
        }
    }
    private static bool Personal(CoreOperation kind)=>kind is CoreOperation.AncientOptionsReroll or CoreOperation.ShopReroll or CoreOperation.ShopPurchaseCommit;
    private static string RequestContext(CoreOperation kind,ulong actor)=>kind==CoreOperation.AncientOptionsReroll ? AncientService.ChoiceContext(actor) : $"{Bridge.State.MapLocation}:{Bridge.State.RunLocation}";
    // Native merchant purchase messages do not save the run or transmit owner
    // stock/Courier restocks. Use the existing authenticated all-peer boundary
    // after native purchase tasks release their counters, then save that state.
    public static async Task PersistShopPurchaseAsync()
    {
        if(!RunManager.Instance.IsInProgress || Bridge.State.CurrentRoom is not MerchantRoom)return;
        string context=RequestContext(CoreOperation.ShopPurchaseCommit,RunManager.Instance.NetService.NetId);
        var deadline=Time.GetTicksMsec()+60000;
        while(Busy) {if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException("商店购买存档同步超时");await Bridge.Frame();}
        if(!RunManager.Instance.IsInProgress || Bridge.State.CurrentRoom is not MerchantRoom ||
            context!=RequestContext(CoreOperation.ShopPurchaseCommit,RunManager.Instance.NetService.NetId))
            throw new InvalidOperationException("商店购买尚未同步，位置已经改变");
        long revision=WorldRevision,completion=_completionSerial;Submit(CoreOperation.ShopPurchaseCommit);
        while(Busy || WorldRevision==revision && _completionSerial==completion)
        {
            if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException("商店购买存档同步超时："+Status);
            await Bridge.Frame();
        }
        if(Status!="操作完成")throw new InvalidOperationException("商店购买存档失败："+Status);
    }
    public static void Submit(CoreOperation kind,long checkpoint=0,int percent=0) {
        if(Busy || !RunManager.Instance.IsInProgress)return;
        OnRunReady();
        if(!IsHost) {
            if(Personal(kind))_net!.SendMessage(Message(Phase.Request,JsonSerializer.Serialize(new PersonalRequest(kind,checkpoint,percent,RequestContext(kind,_net.NetId)))));
            return;
        }
        Start(kind,checkpoint,percent,_net!.NetId);
    }
    private static void Start(CoreOperation kind,long checkpoint,int percent,ulong actor) {
        if(Busy)return;
        Busy=true;ConfigStore.OperationInProgress=true;_failure=null;_applyStarted=false;
        Status="等待当前动作完成";Changed?.Invoke();TaskHelper.RunSafely(Execute(kind,checkpoint,percent,actor));
    }
    public static void SubmitAncientChoice(int index) {
        if(Busy || _choicePending || !AncientService.IsAncientRoom) {
            Log.Warn($"[NoSuffering] Ancient choice blocked index={index} busy={Busy} pending={_choicePending} ancient={AncientService.IsAncientRoom}");
            AncientService.RestoreRejectedLocalChoice();return;
        }
        OnRunReady();
        var model=RunManager.Instance.EventSynchronizer.GetLocalEvent();
        if(index<0 || index>=model.CurrentOptions.Count){AncientService.RestoreRejectedLocalChoice();return;}
        var option=model.CurrentOptions[index];
        _choicePending=true;
        TaskHelper.RunSafely(SendAncientChoice(index,model,option,_net!,++_choiceRequest));
    }
    private static async Task SendAncientChoice(int index,EventModel model,MegaCrit.Sts2.Core.Events.EventOption option,INetGameService net,long request) {
        try {
            // A first click can precede the launch Hello/State roundtrip. Sending
            // with an empty epoch would be dropped before request validation.
            if(!IsHost && _epoch.Length==0)await Wait(()=>_epoch.Length>0 || request!=_choiceRequest,"房主会话尚未同步，请重新选择");
            if(request!=_choiceRequest)return;
            if(!ReferenceEquals(net,_net) || !AncientService.IsAncientRoom || Busy ||
                !ReferenceEquals(model,RunManager.Instance.EventSynchronizer.GetLocalEvent()) ||
                index>=model.CurrentOptions.Count || !ReferenceEquals(option,model.CurrentOptions[index]))
                throw new InvalidOperationException("先古奖励已刷新，请重新选择");
            var choice=new AncientChoiceData(net.NetId,index,AncientService.ChoiceContext(net.NetId));
            Log.Info($"[NoSuffering] Ancient choice submit actor={choice.Actor} index={index} context={choice.Context} epoch={_epoch} revision={WorldRevision}");
            if(IsHost)AcceptChoice(choice,net.NetId);
            else net.SendMessage(Message(Phase.AncientChoiceRequest,JsonSerializer.Serialize(choice)));
        } catch(Exception e) {
            if(request!=_choiceRequest)return;
            _choicePending=false;AncientService.RestoreRejectedLocalChoice();Status=e.Message;Changed?.Invoke();
            Log.Warn("[NoSuffering] Ancient choice send failed: "+e);
        }
    }
    private static void AcceptChoice(AncientChoiceData choice,ulong sender) {
        try {
            if(Busy || choice.Actor!=sender || !AncientService.IsAncientRoom || choice.Context!=AncientService.ChoiceContext(sender))
                throw new InvalidOperationException("先古奖励已刷新，请重新选择");
            // Check before broadcasting: only native execution below commits the
            // choice, and the next reroll sees its committed marker immediately.
            var player=Bridge.State.Players.Single(p=>p.NetId==sender);
            var model=RunManager.Instance.EventSynchronizer.GetEventForPlayer(player);
            if(model.IsFinished || choice.Index<0 || choice.Index>=model.CurrentOptions.Count || model.CurrentOptions[choice.Index].WasChosen)
                throw new InvalidOperationException("先古奖励选项已失效");
            if(_net!.Type==NetGameType.Host)_net.SendMessage(Message(Phase.AncientChoiceCommit,JsonSerializer.Serialize(choice)));
            AncientService.Choose(sender,choice.Index,choice.Context);
            if(sender==_net.NetId)_choicePending=false;
        } catch(Exception e) {
            Log.Warn($"[NoSuffering] Ancient choice rejected actor={choice.Actor} sender={sender} index={choice.Index} requested={choice.Context} current={(AncientService.IsAncientRoom?AncientService.ChoiceContext(sender):"outside ancient room")} busy={Busy}: {e.Message}");
            if(sender==_net!.NetId){_choicePending=false;AncientService.RestoreRejectedLocalChoice();Status=e.Message;Changed?.Invoke();}
            else _net.SendMessage(Message(Phase.RequestRejected,e.Message),sender);
        }
    }
    private static async Task Execute(CoreOperation kind,long checkpoint,int percent,ulong actor) {
        TimelineData? oldTimeline=null;
        string? recovery=null;
        bool committed=false;
        bool attemptStaged=false;
        try {
            OnRunReady();_activeOperation=++_sequence;Ready.Clear();Done.Clear();
            _preparation=new();
            if(_net!.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Prepare,JsonSerializer.Serialize(new PrepareData(kind,actor,percent))));
            await PreparationBoundaryAsync(kind,_preparation.Token);ShopService.MergeOwnedState(ShopService.CaptureOwnedState(),_net.NetId);Ready.Add(_net.NetId);
            await Wait(()=>Ready.Count==Bridge.State.Players.Count,"有玩家未完成当前动作");
            if(_failure is not null)throw new InvalidOperationException(_failure);
            // Native reward/purchase tasks may have completed during preparation.
            // Recovery must pair their settled companion state with the snapshot
            // captured below, rather than the pre-boundary continuation state.
            oldTimeline=CheckpointService.Save();
            Validate(kind,actor,percent);
            recovery=CombatManager.Instance.IsInProgress ? CombatService.Baseline : CheckpointService.PendingCombatRewards?.Snapshot ?? Bridge.Freeze(Bridge.Capture(Bridge.State.CurrentRoom is MerchantRoom ? null : Bridge.State.CurrentRoom));
            string? snapshot=null;
            var timeline=CheckpointService.Save(false);
            if(kind==CoreOperation.MapRollback) {
                var target=CheckpointService.Require(checkpoint);
                snapshot=target.Snapshot;timeline=new([],target.Combat,target.Ancient)
                    {SettledCombat=target.CompletedCombat && !target.BeforeRewards?new CombatSettlement(target.Coord,target.ActIndex):null,
                     PendingCombatRewards=target.BeforeRewards?new CombatRewardReplay(target.Coord,target.ActIndex,target.Snapshot):null,Shop=target.Shop,BossHealth=target.BossHealth};
            } else if(kind is CoreOperation.CombatRestart or CoreOperation.CombatReroll) {
                snapshot=Bridge.Freeze(CombatService.PrepareRestart(kind==CoreOperation.CombatReroll));
                if(kind==CoreOperation.CombatReroll){attemptStaged=true;CombatService.SetAttempt(CombatService.Attempt+1);}
                timeline=CheckpointService.Save(false);
            }
            var data=new OperationData(kind,checkpoint,Bridge.StableSeed(_run,kind.ToString(),checked(WorldRevision+1).ToString(),actor.ToString()),_net.NetId,ConfigStore.Rules,snapshot,timeline,actor,percent);
            ++WorldRevision;committed=true;_applyStarted=true;
            Status="正在同步全队";Changed?.Invoke();
            var installed=data.Snapshot is not null && _net.Type!=NetGameType.Singleplayer?PrepareSnapshotLoad():null;
            if(_net.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Commit,JsonSerializer.Serialize(data)));
            long operation=_activeOperation;string epoch=_epoch;
            await Apply(data,installed);
            if(!Busy || _activeOperation!=operation || _epoch!=epoch)throw new InvalidOperationException("操作会话已结束");
            var digest=Digest();Done[_net.NetId]=digest;
            await Wait(()=>Done.Count==Bridge.State.Players.Count,"有玩家未完成加载");
            if(Done.Values.Any(d=>d!=digest)) {
                foreach(var peer in Done.Where(p=>p.Value!=digest))Log.Error($"[NoSuffering] Digest mismatch operation={_activeOperation} kind={kind} peer={peer.Key} local={digest} remote={peer.Value}");
                throw new InvalidOperationException("全队状态不一致，请保存后重新加入");
            }
            if(kind==CoreOperation.MapRollback)CheckpointService.CommittedRollback(checkpoint);
            // A native multi-page reward can be committed without being finished.
            // Native saves cannot restore that continuation. Retain the paid live
            // refresh and let the eventual native all-finished event save persist
            // it, rather than replacing the last safe save with a broken one.
            if (!AncientService.HasUnfinishedCommittedChoices) await CompanionStore.SaveNativeCurrent();
            else Log.Info("[NoSuffering] Deferred ancient save until committed reward choices finish.");
            _appliedOperation=_activeOperation;Status="操作完成";
            if(_net.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Release));
        } catch(Exception e) {
            Status=e.Message;Log.Error("[NoSuffering] "+e);
            if(!committed){
                // Preparation can await a live enemy turn or native reward UI.
                // Those completed facts must survive an aborted request. Only
                // the explicitly staged attempt is speculative before commit.
                if(attemptStaged)CombatService.Restore(oldTimeline!.Combat);
                if(_net?.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Abort,e.Message));
            }
            else {
                // Native multiplayer loading waits for all peers. A host-only
                // recovery reload cannot finish; disconnect through native menu
                // recovery and retain the last paired save for rejoin instead.
                CheckpointService.Restore(oldTimeline!);
                if(_net?.Type==NetGameType.Singleplayer && recovery is not null) {
                    try{await Bridge.LoadAsync(Bridge.Thaw(recovery));}catch(Exception load){Log.Error("[NoSuffering] Recovery failed: "+load);}
                }
                if(_net?.Type==NetGameType.Host)_net.Disconnect(NetError.StateDivergence);
            }
        } finally {Release();}
    }
    private static async Task Apply(OperationData data,Func<Task>? installed=null) {
        // The host may edit pending rules while this operation uses its captured
        // rules. Only clients adopt the operation snapshot as host rules.
        if(!IsHost)ConfigStore.ApplyHostRules(data.Rules);
        if(data.Snapshot is null)ShopService.ApplySynchronizedState(data.Timeline.Shop);
        if(data.Snapshot is not null){CheckpointService.Restore(data.Timeline,true);await Bridge.LoadAsync(Bridge.Thaw(data.Snapshot),installed);}
        else switch(data.Kind) {
            case CoreOperation.AncientReroll:
            case CoreOperation.AncientOptionsReroll:
                await AncientService.ExecuteAsync(data.Kind==CoreOperation.AncientReroll,data.Seed,data.Actor,data.Rules);break;
            case CoreOperation.ShopReroll: await ShopService.ExecuteAsync(data.Seed,data.Actor,data.Rules);break;
            case CoreOperation.ShopPurchaseCommit: break; // Owner stock was applied above; native player sync settled in preparation.
            case CoreOperation.BossHealthIncrease: await BossHealthService.ExecuteAsync(data.Percent);break;
            default: throw new InvalidOperationException("操作缺少原生快照");
        }
        // LoadRun returns before the opening draw turn task completes.
        var deadline=Time.GetTicksMsec()+20000;
        while(CombatManager.Instance.IsStarting || (CombatManager.Instance.IsInProgress && RunManager.Instance.ActionQueueSynchronizer.CombatState!=ActionSynchronizerCombatState.PlayPhase)) {
            if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException("战斗开场尚未完成，请检查选择界面");
            await Bridge.Frame();
        }
    }
    private static void Validate(CoreOperation kind,ulong actor,int percent) {
        if(!IsHost)throw new InvalidOperationException("本局由房主控制");
        if(CompanionStore.Error is not null)throw new InvalidOperationException(CompanionStore.Error);
        if(!Bridge.State.Players.Any(p=>p.NetId==actor) || !Personal(kind) && actor!=_net!.NetId)
            throw new InvalidOperationException("此操作需要房主执行");
        ValidateContext(kind,actor,percent);
    }
    private static void ValidateContext(CoreOperation kind,ulong actor,int percent) {
        var r=ConfigStore.Rules;
        if(kind==CoreOperation.MapRollback && !r.EnableMapRollback || kind==CoreOperation.CombatRestart && !r.EnableCombatRestart || kind==CoreOperation.CombatReroll && !r.EnableCombatReroll)throw new InvalidOperationException("此功能已关闭");
        if(kind is CoreOperation.CombatRestart or CoreOperation.CombatReroll && !CombatManager.Instance.IsInProgress)throw new InvalidOperationException("当前没有活动战斗");
        string? reason=kind switch {
            CoreOperation.AncientReroll or CoreOperation.AncientOptionsReroll=>AncientService.GetUnavailableReason(kind==CoreOperation.AncientReroll,actor,r),
            CoreOperation.ShopReroll=>ShopService.GetUnavailableReason(actor,r),
            CoreOperation.ShopPurchaseCommit=>Bridge.State.CurrentRoom is MerchantRoom ? null : "当前不是商店",
            CoreOperation.BossHealthIncrease=>BossHealthService.GetUnavailableReason(r),
            _=>null
        };
        if(reason is not null)throw new InvalidOperationException(reason);
        if(kind==CoreOperation.BossHealthIncrease && percent is < 1 or > 1000)throw new InvalidOperationException("首领生命增幅无效");
    }
    private static string Digest() {
        var s=Bridge.Capture(null);
        PrepareNativeDigest(s);
        string native=Bridge.Freeze(s),ancient=JsonSerializer.Serialize(AncientService.CaptureState()),shop=JsonSerializer.Serialize(ShopService.CaptureState()),boss=JsonSerializer.Serialize(BossHealthService.CaptureState());
        Diagnostics.MultiplayerProbe.RecordDigest(native,ancient,shop,boss);
        RecordNativeDigestDiagnostic(native);
        // Component hashes identify the affected state without logging saves or
        // weakening any compared fields. Peers compare the complete manifest.
        return JsonSerializer.Serialize(new {
            Native=Bridge.Hash(CanonicalJson(native)),
            Combat=Bridge.Hash(CanonicalJson(JsonSerializer.Serialize(CombatService.CurrentOrderDigests))),
            Ancient=Bridge.Hash(CanonicalJson(ancient)),Shop=Bridge.Hash(CanonicalJson(shop)),Boss=Bridge.Hash(CanonicalJson(boss))
        });
    }
    private static void PrepareNativeDigest(SerializableRun s) {
        s.NumReloads=0;s.SaveTime=0;s.RunTime=0;s.PlatformType=default;
        s.MapDrawings=null; // Local map annotations are presentation, not synchronized gameplay.
        foreach(var player in s.Players)
        {
            // Native preview/hover UI records discoveries only for its local
            // owner. They are profile progress, and may change as refreshed shop
            // previews render; native saves retain the unmodified lists.
            player.DiscoveredCards=[];player.DiscoveredEnemies=[];player.DiscoveredEpochs=[];
            player.DiscoveredPotions=[];player.DiscoveredRelics=[];
        }
        foreach(var point in s.MapPointHistory.SelectMany(act=>act))
        foreach(var stats in point.PlayerStats)
        {
            // Native purchase packets convey gold lost, but not the local shop's
            // spending reason. Compare total loss without the owner-only label.
            stats.GoldLost+=stats.GoldSpent;stats.GoldSpent=0;
            // Native merchant wrappers record purchase provenance only on the
            // buyer; remote reward packets do not transmit these history lists.
            // Normalize this deep-cloned comparison only, retaining saved history
            // and the full live deck, relics, gold, RNG and stock comparison.
            stats.BoughtRelics=[];stats.BoughtColorless=[];stats.BoughtPotions=[];
            // Merchant exit derives skipped offers from each peer's local stock;
            // remote stock does not clear/restock with native purchase packets.
            // These history annotations are not a shared gameplay state.
            if(point.HasRoomOfType(RoomType.Shop)) {
                // Remote RewardObtained(Card) also appends a picked choice for
                // a purchase; the buyer's merchant wrapper does not. Actual
                // acquisitions remain compared in CardsGained and the deck.
                stats.CardChoices.Clear();
                stats.RelicChoices.RemoveAll(choice=>!choice.wasPicked);
                stats.PotionChoices.RemoveAll(choice=>!choice.wasPicked);
            }
            // Native remote reward history omits this local history annotation;
            // the live deck, card identity and upgrades remain fully compared.
            foreach(var card in stats.CardsGained)card.FloorAddedToDeck=null;
        }
    }
    private static void RecordNativeDigestDiagnostic(string native)
    {
        if(!OS.GetCmdlineUserArgs().Contains("--ns-shop-diagnostics"))return;
        try {
            string directory=Path.Combine(OS.GetUserDataDir(),"NoSuffering","shop-diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,$"revision-{WorldRevision}-player-{_net!.NetId}-native-digest.json"),native);
        } catch(Exception error) {
            Log.Warn($"[NoSuffering] Native digest diagnostic write failed: {error.Message}");
        }
    }
    private static string CanonicalJson(string json)
    {
        // Native packet/disk deserializers may insert dictionary keys in different
        // orders. Compare every value and array position, independent of object
        // property insertion order; never change the native saved representation.
        using var document=JsonDocument.Parse(json);
        using var bytes=new MemoryStream();
        using(var writer=new Utf8JsonWriter(bytes))Write(document.RootElement,writer);
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        static void Write(JsonElement value,Utf8JsonWriter writer)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach(var property in value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal))
                {writer.WritePropertyName(property.Name);Write(property.Value,writer);}
                writer.WriteEndObject();
            }
            else if(value.ValueKind==JsonValueKind.Array)
            {writer.WriteStartArray();foreach(var item in value.EnumerateArray())Write(item,writer);writer.WriteEndArray();}
            else value.WriteTo(writer);
        }
    }
    private static async Task Wait(Func<bool> condition,string timeout) {
        var deadline=Time.GetTicksMsec()+20000;
        while(!condition()){if(_failure is not null)throw new InvalidOperationException(_failure);if(Time.GetTicksMsec()>deadline)throw new InvalidOperationException(timeout);await Bridge.Frame();}
    }
    private static void Release(){_loadBarrier=null;++_completionSerial;_preparation?.Cancel();_preparation?.Dispose();_preparation=null;Busy=false;ConfigStore.OperationInProgress=false;Bridge.Unlock();Changed?.Invoke();}
#if STS2_STABLE
    [HarmonyPatch(typeof(NetHostGameService),MethodType.Constructor,new Type[0])]
#else
    [HarmonyPatch(typeof(NetHostGameService),MethodType.Constructor,[typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo)])]
#endif
    private static class AttachHost{static void Postfix(NetHostGameService __instance)=>Attach(__instance);}
#if STS2_STABLE
    [HarmonyPatch(typeof(NetClientGameService),MethodType.Constructor,new Type[0])]
#else
    [HarmonyPatch(typeof(NetClientGameService),MethodType.Constructor,[typeof(MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo)])]
#endif
    private static class AttachClient{static void Postfix(NetClientGameService __instance)=>Attach(__instance);}
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpNewMultiplayer))]
    private static class NewSessionIdentity
    {
        static void Postfix()=>_freshRunIdentityPending=RunManager.Instance.NetService.Type==NetGameType.Client;
    }
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpSavedMultiplayer))]
    private static class SavedSessionReady
    {
        static void Prefix()=>_freshRunIdentityPending=false;
        static void Postfix(ref Task __result)=>__result=BeforeRoomFactory(__result);
        static async Task BeforeRoomFactory(Task setup)
        {
            await setup;
            OnRunReady();
            // Native saved clients have no local companion file. Establish the
            // authenticated host metadata before native room factories consume
            // RNG or create merchant stock from the saved player state.
            if(IsHost)RulesChanged();
            else await Wait(()=>_epoch.Length>0 && _run==Bridge.RunKey,"房主存档记录尚未同步");
        }
    }
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.Launch))]
    private static class RunReady{static void Postfix()=>OnRunReady();}
    [HarmonyPatch(typeof(ActionQueueSynchronizer),nameof(ActionQueueSynchronizer.RequestEnqueue))]
    private static class InputBoundary {static bool Prefix()=>!Busy;}
    // Native potion entry points mutate local queue/holder state before
    // RequestEnqueue. Reject here so a busy operation leaves them retryable.
    [HarmonyPatch(typeof(PotionModel),nameof(PotionModel.EnqueueManualUse))]
    private static class PotionUseBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(NPotionPopup),"OnDiscardButtonPressed")]
    private static class PotionDiscardBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(EventSynchronizer),nameof(EventSynchronizer.ChooseLocalOption))]
    private static class EventBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(LoadRunLobby),"HandleClientLoadJoinRequestMessage")]
    private static class LoadJoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
    [HarmonyPatch(typeof(RunLobby),"HandleClientRejoinRequestMessage")]
    private static class RejoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
}
