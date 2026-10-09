using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using NoSuffering.Ancients;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using Bridge=NoSuffering.GameBridge.GameBridge;
[ModInitializer(nameof(Init))]
public static class Probe {
static SceneTree Tree=null!;
static readonly Dictionary<string,string> Results=new();
static readonly Dictionary<string,object> Facts=new();
static string Stage="startup";
public static void Init(){
 if(!OS.GetCmdlineUserArgs().Contains("--ns-native-interop") || !OS.GetCmdlineUserArgs().Contains("--ns-lab-probe") || CommandLineHelper.GetValue("force-steam")!="off" || OS.GetUserDataDir().Replace('\\','/').TrimEnd('/')!=OS.GetDataDir().Replace('\\','/').TrimEnd('/')+"/NoSufferingLab/windows-public-beta")return;
 Tree=(SceneTree)Engine.GetMainLoop(); void Start(){Tree.ProcessFrame-=Start;_=Run();} Tree.ProcessFrame+=Start;
}
static Type U(string name)=>AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="UndoAndRestart").GetType("UndoAndRestartCode."+name,true)!;
static object? Call(string type,string method)=>U(type).GetMethod(method,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,null);
static T Prop<T>(string name)=>(T)U("UndoRedoManager").GetProperty(name,BindingFlags.Public|BindingFlags.Static)!.GetValue(null)!;
static int Generation()=>(int)U("UndoRedoManager").GetField("_timelineGeneration",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
static void Require(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
static void Pass(string detail){Results[Stage]="PASS: "+detail;}
static async Task Wait(Func<bool> done,string detail){var end=Time.GetTicksMsec()+60000;while(!done()){if(Time.GetTicksMsec()>end)throw new TimeoutException(detail);await Bridge.Frame();}}
static async Task Frames(int count=60){for(int i=0;i<count;i++)await Bridge.Frame();}
static async Task Timed(Task task){await Wait(()=>task.IsCompleted,"native task");await task;}
static async Task Operation(CoreOperation op){var rev=HostCoordinator.WorldRevision;HostCoordinator.Submit(op);await Wait(()=>!HostCoordinator.Busy,"NoS op");Require(HostCoordinator.WorldRevision==rev+1 && HostCoordinator.Status=="操作完成",HostCoordinator.Status);await Frames(5);}
static Task Opening()=>Wait(()=>CombatManager.Instance.IsInProgress&&!CombatManager.Instance.IsStarting&&RunManager.Instance.ActionQueueSynchronizer.CombatState==ActionSynchronizerCombatState.PlayPhase&&CombatService.CurrentOrderDigests.Count==1,"opening");
static string Digests()=>string.Join("/",CombatService.CurrentOrderDigests.OrderBy(p=>p.Key).Select(p=>p.Value));
static async Task Claim(){
 await Wait(()=>NEventRoom.Instance?.Layout?.OptionButtons.Any()==true,"buttons");
 NEventOptionButton? reward=null;
 for(int i=0;reward==null&&i<8;i++){
 reward=NEventRoom.Instance!.Layout!.OptionButtons.FirstOrDefault(b=>b.Option.Relic is {} r && (r.GetType().GetMethod(nameof(RelicModel.AfterObtained))!.DeclaringType==typeof(RelicModel) || r.Id.Entry is "NEOWS_TALISMAN" or "GOLDEN_PEARL" or "NUTRITIOUS_OYSTER" or "CURSED_PEARL" or "SILKEN_TRESS" or "LEAFY_POULTICE")&&!b.Option.IsLocked);
 if(reward==null)await Operation(CoreOperation.AncientOptionsReroll);
 }
 Require(reward!=null,"prompt-free reward absent");var local=RunManager.Instance.EventSynchronizer.GetLocalEvent();reward!.Call(NEventOptionButton.MethodName.OnRelease);
 await Wait(()=>local.IsFinished&&NEventRoom.Instance!.Layout!.OptionButtons.Any(b=>b.Option.IsProceed),"claim");await Timed(RunManager.Instance.EventSynchronizer.AwaitPendingOptionTasks());
 NEventRoom.Instance!.Layout!.OptionButtons.Single(b=>b.Option.IsProceed).Call(NEventOptionButton.MethodName.OnRelease);await Frames(3);Require(NMapScreen.Instance is {IsOpen:true,IsTravelEnabled:true},"Proceed blocked");
}
static async Task RestartThird(){
 var old=NRun.Instance;Require((bool)Call("FloorRestartService","HandleQuickRestartKey")!,"third-party restart rejected");
 await Wait(()=>NRun.Instance!=old&&!NGame.Instance!.Transition.InTransition,"third party restart reconstruction");await Frames(30);
}
static async Task Run(){
 foreach(var k in new[]{"ancient_f03_claim_proceed","ancient_sl_load_f03_claim_proceed","undo_native_card","nos_restart_invalidates_undo","f05_undo_retains_order","third_restart_retains_f05","native_potion_after_restart","instrumented_busy_potion_use","instrumented_busy_potion_discard"})Results[k]="NOT_RUN";
 try{
 await Wait(()=>NGame.Instance?.GameStartupComplete.IsCompleted==true,"startup");await NGame.Instance!.GameStartupComplete;Require(!RunManager.Instance.IsInProgress,"active run");
 Facts["loaded_assemblies"]=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name is "NoSuffering" or "AncientSL" or "UndoAndRestart").Select(a=>new{a.FullName,a.Location}).ToArray();
 ConfigStore.ChangeRules(r=>r with{EnableAncientOptionsReroll=true,OptionsCostMode=RefreshCostMode.Free,EnableCombatRestart=true,EnableCombatReroll=true});
 var m=RunManager.Instance;var g=NGame.Instance!;var run=RunState.CreateForNewRun([Player.CreateForNewRun(ModelDb.Character<Ironclad>(),UnlockState.all,1UL)],ActModel.GetDefaultList().Select(a=>a.ToMutable()).ToList(),[],GameMode.Standard,0,"NATIVEINTEROP");
 m.SetUpNewSingleplayer(run,true);await Timed(PreloadManager.LoadRunAssets(run.Players.Select(p=>p.Character)));await Timed(m.FinalizeStartingRelics());m.Launch();g.RootSceneContainer.SetCurrentScene(NRun.Create(run));g.ReactionContainer.InitializeNetworking(m.NetService);
 foreach(var ftue in new[]{"map_select_ftue","combat_rules_ftue","combat_reward_ftue"})SaveManager.Instance.MarkFtueAsComplete(ftue);
 await Timed(m.EnterAct(0,false));HostCoordinator.OnRunReady();await Timed(m.EnterMapCoord(Bridge.State.Map.StartingMapPoint.coord));
 Stage="ancient_f03_claim_proceed";await Operation(CoreOperation.AncientOptionsReroll);await Claim();Pass("AncientSL patch loaded; F03 native reward and Proceed callbacks completed");
 Stage="ancient_sl_load_f03_claim_proceed";await RestartThird();Require(Bridge.State.CurrentRoom is EventRoom && !m.EventSynchronizer.GetLocalEvent().IsFinished,"opening ancient SL did not reopen rewards");await Operation(CoreOperation.AncientOptionsReroll);await Claim();Pass("actual UndoAndRestart floor restart reloaded AncientSL-preserved pre-claim opening save; F03 and native claim/Proceed completed");
 var monster=Bridge.State.Map.GetAllMapPoints().Where(p=>p.PointType==MapPointType.Monster).OrderBy(p=>p.coord.row).ThenBy(p=>p.coord.col).First();await Timed(m.EnterMapCoord(monster.coord));await Opening();await Frames();
 Stage="undo_native_card";await PlayUndo();Pass("native queued PlayCardAction completed; third-party HandleUndoKey restored card and energy");
 Stage="nos_restart_invalidates_undo";int gen=Generation();await Operation(CoreOperation.CombatRestart);await Opening();await Frames();Require(Generation()>gen,"stale undo generation retained");Facts["history_after_nos_restart"]=Prop<int>("SnapshotCount");var initial=Digests();if((bool)Call("UndoRedoManager","HandleUndoKey")!){await Wait(()=>!Prop<bool>("IsNavigationInProgress"),"new opening undo");await Frames(10);}Require(Digests()==initial&&CombatService.Attempt==0,"Undo after F04 changed current attempt/order");Pass("NoS F04 rebuilt native combat and invalidated old undo generation/history");
 Stage="f05_undo_retains_order";await Operation(CoreOperation.CombatReroll);await Opening();var digest=Digests();Require(CombatService.Attempt==1,"attempt missing");await Frames();await PlayUndo();await Operation(CoreOperation.CombatRestart);await Opening();Require(CombatService.Attempt==1&&Digests()==digest,"F04 after undo lost F05 order");Pass("F05, native card play, third-party Undo, NoS F04 retained active attempt/order");
 Stage="third_restart_retains_f05";await Frames();await RestartThird();await Opening();Require(CombatService.Attempt==1&&Digests()==digest,"third-party restart lost F05 attempt/order");Pass("actual third-party quick restart retained NoS F05 active attempt/order");
 Stage="native_potion_after_restart";await Operation(CoreOperation.CombatRestart);await Opening();await Frames();var player=Bridge.State.Players.Single();var potion=ModelDb.Potion<EnergyPotion>().ToMutable();await Timed(PotionCmd.TryToProcure(potion,player));Require(player.Potions.Contains(potion),"potion procurement failed");int energy=player.PlayerCombatState!.Energy;potion.EnqueueManualUse(player.Creature);await Wait(()=>!player.Potions.Contains(potion)&&!m.ActionExecutor.IsRunning&&m.ActionQueueSet.IsEmpty,"native potion action");Require(player.PlayerCombatState!.Energy>energy,"energy potion did not grant energy");Facts["potion_energy_before"]=energy;Facts["potion_energy_after"]=player.PlayerCombatState.Energy;Pass("after NoS F04, PotionCmd.TryToProcure EnergyPotion and PotionModel.EnqueueManualUse(self) consumed potion and increased energy");
 await BusyPotionChecks();
 }catch(Exception e){Results[Stage]="FAIL: "+e;}
 var report=JsonSerializer.Serialize(new{scope="isolated Windows beta headless native callbacks; direct room fixture; no pointer/full-run/multiplayer claim",results=Results,facts=Facts});File.WriteAllText(Path.Combine(OS.GetUserDataDir(),"native-interop.json"),report);Console.WriteLine("NATIVE_INTEROP_RESULTS "+report);Tree.Quit(Results.Values.All(v=>v.StartsWith("PASS"))?0:1);
}
static async Task PlayUndo(){
 var m=RunManager.Instance;var p=Bridge.State.Players.Single();var state=p.PlayerCombatState!;var card=state.Hand.Cards.First(c=>c.Id.Entry.StartsWith("DEFEND")||c.Id.Entry.StartsWith("STRIKE"));int count=state.Hand.Cards.Count,energy=state.Energy;
 m.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card,card.TargetType==TargetType.AnyEnemy?((CombatRoom)Bridge.State.CurrentRoom!).Enemies.First(e=>e.IsAlive):null));
 await Wait(()=>state.Hand.Cards.Count<count&&!m.ActionExecutor.IsRunning&&m.ActionQueueSet.IsEmpty,"native play");await Frames();Require(Prop<int>("SnapshotCount")>=2,"Undo snapshots not captured");Require((bool)Call("UndoRedoManager","HandleUndoKey")!,"Undo rejected");await Wait(()=>!Prop<bool>("IsNavigationInProgress"),"Undo restoring");await Frames(10);
 p=Bridge.State.Players.Single();Require(p.PlayerCombatState!.Hand.Cards.Count==count&&p.PlayerCombatState.Energy==energy,"Undo card/energy not restored");
}
// Artificially holds the real coordinator flag to make the blocked-enqueue
// window deterministic; this is instrumentation, not a timing or UI test.
static void SetBusy(bool busy)=>typeof(HostCoordinator).GetField("<Busy>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,busy);
static bool HolderDisabled(NPotionHolder holder)=>(bool)typeof(NPotionHolder).GetField("_disabledUntilPotionRemoved",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(holder)!;
static async Task BusyPotionChecks(){
 bool baseline=OS.GetCmdlineUserArgs().Contains("--ns-busy-baseline");
 var m=RunManager.Instance;var player=Bridge.State.Players.Single();
 Stage="instrumented_busy_potion_use";var potion=ModelDb.Potion<EnergyPotion>().ToMutable();await Timed(PotionCmd.TryToProcure(potion,player));Require(player.Potions.Contains(potion),"busy fixture procure failed");await Frames(5);
 int beforeUse=0;void BeforeUse(){beforeUse++;}potion.BeforeUse+=BeforeUse;
 int energy=player.PlayerCombatState!.Energy;
 try{SetBusy(true);potion.EnqueueManualUse(player.Creature);await Frames(3);
 Facts["busy_use_beforeuse_calls"]=beforeUse;Facts["busy_use_isqueued"]=potion.IsQueued;
 Require(player.Potions.Contains(potion)&&player.PlayerCombatState.Energy==energy&&m.ActionQueueSet.IsEmpty&&!m.ActionExecutor.IsRunning,"busy potion unexpectedly executed");
 Require(baseline?beforeUse==1&&potion.IsQueued:beforeUse==0&&!potion.IsQueued,baseline?"old poison not reproduced":"busy call mutated local potion state");
 }finally{SetBusy(false);potion.BeforeUse-=BeforeUse;}
 if(baseline)Pass("PRE-FIX REPRODUCED: artificial Busy dropped native enqueue but BeforeUse fired and IsQueued became true; potion remained in belt, energy unchanged");
 else {potion.EnqueueManualUse(player.Creature);await Wait(()=>!player.Potions.Contains(potion)&&m.ActionQueueSet.IsEmpty&&!m.ActionExecutor.IsRunning,"use retry after artificial release");Require(player.PlayerCombatState.Energy>energy,"released potion unusable");Pass("artificial Busy rejected native use without BeforeUse/IsQueued mutation; exact same potion consumed and granted energy after flag release");}
 Stage="instrumented_busy_potion_discard";var discard=ModelDb.Potion<EnergyPotion>().ToMutable();await Timed(PotionCmd.TryToProcure(discard,player));Require(player.Potions.Contains(discard),"discard fixture procure failed");await Frames(5);
 var holder=NRun.Instance!.GlobalUi.TopBar.PotionContainer.FindChildren("*","",true,false).OfType<NPotionHolder>().Single(h=>h.Potion?.Model==discard);
 var popup=NPotionPopup.Create(holder);NGame.Instance!.GetTree().Root.AddChild(popup);await Frames(2);
 var callback=typeof(NPotionPopup).GetMethod("OnDiscardButtonPressed",BindingFlags.Instance|BindingFlags.NonPublic)!;
 try{SetBusy(true);callback.Invoke(popup,[null]);await Frames(3);Facts["busy_discard_holder_disabled"]=HolderDisabled(holder);
 Require(player.Potions.Contains(discard)&&m.ActionQueueSet.IsEmpty&&!m.ActionExecutor.IsRunning,"busy discard unexpectedly executed");Require(HolderDisabled(holder)==baseline,baseline?"old disabled-holder defect not reproduced":"busy discard disabled holder");
 }finally{SetBusy(false);}
 if(baseline)Pass("PRE-FIX REPRODUCED: actual popup discard callback disabled holder while Busy dropped enqueue; potion remained in belt");
 else{callback.Invoke(popup,[null]);await Wait(()=>!player.Potions.Contains(discard)&&m.ActionQueueSet.IsEmpty&&!m.ActionExecutor.IsRunning,"discard retry after artificial release");Pass("artificial Busy rejected actual native popup discard before holder disable; same callback discarded exact same potion after flag release");}
 popup.QueueFree();
}

}
