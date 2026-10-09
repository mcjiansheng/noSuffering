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
using NoSuffering.Shops;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Multiplayer;

public enum CoreOperation { MapRollback,AncientReroll,AncientOptionsReroll,CombatRestart,CombatReroll,ShopReroll,BossHealthIncrease,ShopPurchaseCommit }
public enum Phase { Hello,State,Prepare,Ready,Commit,Done,Release,Abort,Request,AncientChoiceRequest,AncientChoiceCommit,RequestRejected }
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
public sealed record OperationData(CoreOperation Kind,long Checkpoint,ulong Seed,ulong Host,HostRules Rules,string? Snapshot,TimelineData Timeline,ulong Actor=0,int Percent=0);
public sealed record PersonalRequest(CoreOperation Kind,long Checkpoint,int Percent,string Context);
public sealed record AncientChoiceData(ulong Actor,int Index,string Context);
public sealed record PrepareData(CoreOperation Kind,ulong Actor,int Percent);
public sealed record ReadyData(string Error,ShopRecord? Shop);
public sealed record SessionData(HostRules Rules,TimelineData Timeline);

public static class HostCoordinator
{
    private static INetGameService? _net;
    private static string _run="",_epoch="";
    private static long _sequence,_activeOperation,_appliedOperation,_completionSerial;
    private static readonly HashSet<ulong> Ready=[];
    private static readonly Dictionary<ulong,string> Done=[];
    private static string? _failure;
    private static bool _applyStarted;
    private static CancellationTokenSource? _preparation;
    public static long WorldRevision {get;private set;}
    public static bool Busy {get;private set;}
    private static bool _choicePending;
    public static bool IsHost=>RunManager.Instance.IsInProgress && RunManager.Instance.NetService is {Type:NetGameType.Singleplayer or NetGameType.Host};
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
        if(_run!=run) {_choicePending=false;_run=run;_sequence=0;_appliedOperation=0;WorldRevision=CompanionStore.LoadedRevision;_epoch=IsHost?Guid.NewGuid().ToString("N"):"";}
        ConfigStore.SetMultiplayer(manager.NetService.Type!=NetGameType.Singleplayer);
        ConfigStore.SetRulesWritable(IsHost);
        if(!IsHost && !Busy)_net!.SendMessage(Message(Phase.Hello));
    }
    public static void RulesChanged() {
        if(IsHost && !Busy && _net?.Type==NetGameType.Host)
            _net.SendMessage(Message(Phase.State,JsonSerializer.Serialize(new SessionData(ConfigStore.Rules,CheckpointService.Save(false)))));
    }
    public static void SyncTo(ulong peer) {
        if(!IsHost || Busy || _net?.Type!=NetGameType.Host)return;
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
            ConfigStore.ApplyHostRules(data.Rules);
            bool liveShop=RunManager.Instance.IsInProgress && Bridge.State.CurrentRoom is MerchantRoom &&
                data.Timeline.Shop?.Context==$"{Bridge.State.Rng.Seed}:{Bridge.State.MapLocation}";
            CheckpointService.Restore(data.Timeline,true,live:liveShop);AncientService.MarkLiveStateSynchronized();
            return;
        }
        if(m.Run!=_run || m.Epoch!=_epoch)return;
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
            case Phase.RequestRejected:
                _choicePending=false;Status=m.Payload;++_completionSerial;Changed?.Invoke();break;
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
    private static async Task PreparationBoundaryAsync(CancellationToken cancellation)
    {
        await AncientService.WaitForPendingChoicesAsync(cancellation);
        await Bridge.BoundaryAsync(cancellation);
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
            await PreparationBoundaryAsync(cancellation);
            var prepared=JsonSerializer.Deserialize<PrepareData>(request.Payload) ?? throw new InvalidOperationException("准备内容缺失");
            ValidateContext(prepared.Kind,prepared.Actor,prepared.Percent);
        } catch(OperationCanceledException){return;} catch(Exception e){error=e.Message;}
        if(cancellation.IsCancellationRequested || !Busy || _activeOperation!=request.Operation || _epoch!=request.Epoch)return;
        request.Phase=Phase.Ready;request.Payload=JsonSerializer.Serialize(new ReadyData(error,ShopService.CaptureOwnedState()));
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
        if(Busy || _choicePending || !AncientService.IsAncientRoom)return;
        OnRunReady();
        var choice=new AncientChoiceData(_net!.NetId,index,AncientService.ChoiceContext(_net.NetId));
        _choicePending=true;
        if(IsHost)AcceptChoice(choice,_net.NetId);
        else _net.SendMessage(Message(Phase.AncientChoiceRequest,JsonSerializer.Serialize(choice)));
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
            if(sender==_net!.NetId){_choicePending=false;Status=e.Message;Changed?.Invoke();}
            else _net.SendMessage(Message(Phase.RequestRejected,e.Message),sender);
        }
    }
    private static async Task Execute(CoreOperation kind,long checkpoint,int percent,ulong actor) {
        var oldTimeline=CheckpointService.Save();
        string? recovery=null;
        bool committed=false;
        try {
            OnRunReady();_activeOperation=++_sequence;Ready.Clear();Done.Clear();
            _preparation=new();
            if(_net!.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Prepare,JsonSerializer.Serialize(new PrepareData(kind,actor,percent))));
            await PreparationBoundaryAsync(_preparation.Token);ShopService.MergeOwnedState(ShopService.CaptureOwnedState(),_net.NetId);Ready.Add(_net.NetId);
            await Wait(()=>Ready.Count==Bridge.State.Players.Count,"有玩家未完成当前动作");
            if(_failure is not null)throw new InvalidOperationException(_failure);
            // Native reward/purchase tasks may have completed during preparation.
            // Recovery must pair their settled companion state with the snapshot
            // captured below, rather than the pre-boundary continuation state.
            oldTimeline=CheckpointService.Save();
            Validate(kind,actor,percent);
            recovery=CombatManager.Instance.IsInProgress ? CombatService.Baseline : Bridge.Freeze(Bridge.Capture(Bridge.State.CurrentRoom is MerchantRoom ? null : Bridge.State.CurrentRoom));
            string? snapshot=null;
            var timeline=CheckpointService.Save(false);
            if(kind==CoreOperation.MapRollback) {
                var target=CheckpointService.Require(checkpoint);
                snapshot=target.Snapshot;timeline=new([],target.Combat,target.Ancient)
                    {SettledCombat=target.CompletedCombat?new CombatSettlement(target.Coord,target.ActIndex):null,Shop=target.Shop,BossHealth=target.BossHealth};
            } else if(kind is CoreOperation.CombatRestart or CoreOperation.CombatReroll) {
                snapshot=Bridge.Freeze(CombatService.PrepareRestart(kind==CoreOperation.CombatReroll));
                if(kind==CoreOperation.CombatReroll)CombatService.SetAttempt(CombatService.Attempt+1);
                timeline=CheckpointService.Save(false);
            }
            var data=new OperationData(kind,checkpoint,Bridge.StableSeed(_run,kind.ToString(),checked(WorldRevision+1).ToString(),actor.ToString()),_net.NetId,ConfigStore.Rules,snapshot,timeline,actor,percent);
            ++WorldRevision;committed=true;_applyStarted=true;
            Status="正在同步全队";Changed?.Invoke();
            if(_net.Type==NetGameType.Host)_net.SendMessage(Message(Phase.Commit,JsonSerializer.Serialize(data)));
            await Apply(data);
            var digest=Digest();Done[_net.NetId]=digest;
            await Wait(()=>Done.Count==Bridge.State.Players.Count,"有玩家未完成加载");
            if(Done.Values.Any(d=>d!=digest))throw new InvalidOperationException("全队状态不一致，请保存后重新加入");
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
        if(data.Snapshot is null)ShopService.ApplySynchronizedState(data.Timeline.Shop);
        if(data.Snapshot is not null){CheckpointService.Restore(data.Timeline,true);await Bridge.LoadAsync(Bridge.Thaw(data.Snapshot));}
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
        foreach(var stats in s.MapPointHistory.SelectMany(act=>act).SelectMany(point=>point.PlayerStats))
        {
            // Native purchase packets convey gold lost, but not the local shop's
            // spending reason. Compare total loss without the owner-only label.
            stats.GoldLost+=stats.GoldSpent;stats.GoldSpent=0;
            // Native remote reward history omits this local history annotation;
            // the live deck, card identity and upgrades remain fully compared.
            foreach(var card in stats.CardsGained)card.FloorAddedToDeck=null;
        }
        string native=Bridge.Freeze(s),ancient=JsonSerializer.Serialize(AncientService.CaptureState()),shop=JsonSerializer.Serialize(ShopService.CaptureState()),boss=JsonSerializer.Serialize(BossHealthService.CaptureState());
        Diagnostics.MultiplayerProbe.RecordDigest(native,ancient,shop,boss);
        return Bridge.Hash(CanonicalJson(native)+CanonicalJson(JsonSerializer.Serialize(CombatService.CurrentOrderDigests))+CanonicalJson(ancient)+CanonicalJson(shop)+CanonicalJson(boss));
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
    private static void Release(){++_completionSerial;_preparation?.Cancel();_preparation?.Dispose();_preparation=null;Busy=false;ConfigStore.OperationInProgress=false;Bridge.Unlock();Changed?.Invoke();}
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
    [HarmonyPatch(typeof(RunManager),nameof(RunManager.SetUpSavedMultiplayer))]
    private static class SavedSessionReady
    {
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
    [HarmonyPatch(typeof(EventSynchronizer),nameof(EventSynchronizer.ChooseLocalOption))]
    private static class EventBoundary {static bool Prefix()=>!Busy;}
    [HarmonyPatch(typeof(LoadRunLobby),"HandleClientLoadJoinRequestMessage")]
    private static class LoadJoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
    [HarmonyPatch(typeof(RunLobby),"HandleClientRejoinRequestMessage")]
    private static class RejoinData {static void Prefix(ulong senderId)=>SyncTo(senderId);}
}
