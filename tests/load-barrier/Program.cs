using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using NoSuffering.Multiplayer;

var flags=BindingFlags.Static|BindingFlags.NonPublic;
var owner=typeof(HostCoordinator);
var barrierType=owner.GetNestedType("LoadBarrier",BindingFlags.NonPublic)!;
var net=DispatchProxy.Create<INetGameService,Transport>();
var transport=(Transport)(object)net;
var barrier=Activator.CreateInstance(barrierType,[net,"run","epoch",9L,5L,new ulong[]{1,2,3},"operation"])!;
var field=owner.GetField("_loadBarrier",flags)!;
field.SetValue(null,barrier);owner.GetField("_net",flags)!.SetValue(null,net);
var receive=owner.GetMethod("OnMessage",flags)!;
var enter=owner.GetMethod("TryEnterLoad",flags)!;
var installed=(HashSet<ulong>)barrierType.GetField("Installed")!.GetValue(barrier)!;
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
ControlMessage Message()=>new(){Phase=Phase.LoadInstalled,Run="run",Epoch="epoch",Operation=9,Revision=5,Payload="operation"};
void Receive(ControlMessage message,ulong sender)=>receive.Invoke(null,[message,sender]);
try {
    var stale=Message();stale.Operation=8;Receive(stale,2);
    stale=Message();stale.Epoch="old";Receive(stale,2);
    stale=Message();stale.Revision=4;Receive(stale,2);
    stale=Message();stale.Payload="probe-shop-disk-continue";Receive(stale,2);
    Receive(Message(),4);Receive(Message(),1);
    Require(installed.Count==0,"Stale/foreign/self installed packet entered the barrier.");
    // Native RunManager is absent in this process: early packets must use only
    // the captured transport/roster and remain pending until local setup ends.
    Receive(Message(),2);Receive(Message(),2);Receive(Message(),3);
    Require(installed.SetEquals(new ulong[]{2,3}) && transport.Sent.Count==0,"Early/deduplicated peer installation released the host too soon.");
    installed.Add(1);enter.Invoke(null,[barrier]);
    Require(transport.Sent.Count==1 && transport.Sent[0].Phase==Phase.LoadEnter,"All installations did not release exactly once.");
    var sent=transport.Sent[0];
    Require(sent.Run=="run" && sent.Epoch=="epoch" && sent.Operation==9 && sent.Revision==5 && sent.Payload=="operation","Enter lost captured operation identity.");
    Receive(Message(),2);enter.Invoke(null,[barrier]);
    Require(transport.Sent.Count==1,"Duplicate installation resent Enter.");
    field.SetValue(null,null);Receive(Message(),2);
    Require(transport.Sent.Count==1,"Released barrier accepted a late packet.");
    field.SetValue(null,barrier);
    var failed=Message();failed.Phase=Phase.Done;failed.Payload="ERROR:setup failed";
    Receive(failed,2);
    Require((string?)owner.GetField("_failure",flags)!.GetValue(null)==failed.Payload,"Setup failure did not stop the host's captured barrier.");
    owner.GetField("_failure",flags)!.SetValue(null,null);
    transport.GameType=NetGameType.Client;transport.LocalId=2;
    var clientBarrier=Activator.CreateInstance(barrierType,[net,"run","epoch",9L,5L,new ulong[]{1,2,3},"operation"])!;
    // DispatchProxy is not the native concrete client; supply the captured host
    // that a NetClientGameService exposes through HostNetId.
    barrierType.GetField("Host")!.SetValue(clientBarrier,1UL);
    field.SetValue(null,clientBarrier);
    var release=Message();release.Phase=Phase.LoadEnter;
    Receive(release,3);
    Require(!(bool)barrierType.GetField("Entered")!.GetValue(clientBarrier)!,"Non-host peer released the client.");
    Receive(release,1);
    Require((bool)barrierType.GetField("Entered")!.GetValue(clientBarrier)!,"Captured host did not release the client.");
    Require((int)Phase.RequestRejected==11 && (int)Phase.LoadInstalled==12 && (int)Phase.LoadEnter==13,"Existing wire phase values changed.");
    Console.WriteLine("PASS: early installations wait for local setup; roster authentication, identity/context rejection, deduplication, single Enter, released packets, setup failure, client host authentication and phase values.");
} finally {field.SetValue(null,null);owner.GetField("_net",flags)!.SetValue(null,null);owner.GetField("_failure",flags)!.SetValue(null,null);}

public class Transport:DispatchProxy
{
    public readonly List<ControlMessage> Sent=[];
    public NetGameType GameType=NetGameType.Host;
    public ulong LocalId=1;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="get_Type")return GameType;
        if(method.Name=="get_NetId")return LocalId;
        if(method.Name=="SendMessage"){Sent.Add((ControlMessage)args![0]!);return null;}
        throw new InvalidOperationException("Unexpected live transport access: "+method.Name);
    }
}
