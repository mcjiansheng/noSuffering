using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Checkpoints;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using Bridge=NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.UI;

public static class MapRollbackSelection
{
    private static NMapScreen? _screen;
    private static HBoxContainer? _controls;
    private static RunState? _original;
    private static bool _wasOpen;
    private static bool _preview;
    private static int _act;
    private static readonly System.Reflection.FieldInfo ScreenState=AccessTools.Field(typeof(NMapScreen),"_runState");
    private static readonly System.Reflection.FieldInfo Points=AccessTools.Field(typeof(NMapScreen),"_mapPointDictionary");
    public static bool Active {get;private set;}
    public static IReadOnlyList<MapCheckpoint> Targets=>CheckpointService.History.Where(c=>c.IsNodeTarget).ToList();
    private static string T(string zh,string en)=>ConfigStore.Local.Language=="English"?en:zh;

    public static void Begin() {
        if(!HostCoordinator.IsHost || HostCoordinator.Busy || !ConfigStore.Rules.EnableMapRollback)return;
        if(CompanionStore.Error is { } error){Overlay.Notify(error);return;}
        if(Targets.Count==0){Overlay.Notify(T("还没有可返回的节点","No visited nodes to return to yet"));return;}
        if(Active)Cancel();
        Overlay.Close();
        _original=Bridge.State;_screen=NMapScreen.Instance!;_wasOpen=_screen.IsOpen;
        _act=_original.CurrentActIndex;Active=true;
        _screen.Open(true);
        _controls=new HBoxContainer {Theme=Overlay.MakeTheme(),MouseFilter=Control.MouseFilterEnum.Pass};
        _screen.AddChild(_controls);
        _controls.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _controls.Position=new Vector2(_screen.Size.X/2-260,100);_controls.CustomMinimumSize=new Vector2(520,44);
        _controls.AddChild(new Label {Text=T("选择要返回的节点","Choose a node to return to"),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
        foreach(int act in Targets.Select(c=>c.ActIndex).Distinct().Order()) {
            if(Targets.Select(c=>c.ActIndex).Distinct().Count()==1)break;
            int selected=act;
            var button=new Button {Text=T($"第 {act+1} 幕",$"Act {act+1}"),FocusMode=Control.FocusModeEnum.All};
            button.Pressed+=()=>ShowAct(selected);_controls.AddChild(button);
        }
        var cancel=new Button {Text=T("取消","Cancel"),FocusMode=Control.FocusModeEnum.All};cancel.Pressed+=()=>Cancel();_controls.AddChild(cancel);
        if(!Targets.Any(c=>c.ActIndex==_act))ShowAct(Targets.Last().ActIndex);
        RefreshPoints();
    }
    private static void ShowAct(int act) {
        if(!Active || _screen is null || _original is null)return;
        _act=act;
        try {
        if(act==_original.CurrentActIndex) {
            _preview=false;ScreenState.SetValue(_screen,_original);_screen.SetMap(_original.Map,_original.Rng.Seed,false);
        } else {
            // This state belongs only to the map view. The actual run, network
            // location, RNG and resources remain untouched until the host commits.
            var save=Bridge.Thaw(Targets.Last(c=>c.ActIndex==act).Snapshot);
            var preview=RunState.FromSerializable(save);
            preview.Map=new SavedActMap(save.Acts[act].SavedMap!);
            // SetMap normally queries live map votes using Player identities.
            // A past-act view owns snapshot players and must not join that path.
            _preview=true;ScreenState.SetValue(_screen,preview);_screen.SetMap(preview.Map,preview.Rng.Seed,false);
        }
        RefreshPoints();
        } catch {Cancel(false);throw;}
    }
    public static void Cancel(bool closeMap=true) {
        if(!Active)return;
        Active=false;
        try {
        if(_controls is not null && GodotObject.IsInstanceValid(_controls)) {
            _controls.GetViewport().GuiReleaseFocus();_controls.GetParent()?.RemoveChild(_controls);_controls.QueueFree();
        }
        if(_screen is not null && GodotObject.IsInstanceValid(_screen) && _original is not null) {
            if(_preview){ScreenState.SetValue(_screen,_original);_screen.SetMap(_original.Map,_original.Rng.Seed,false);}
            _screen.Call(NMapScreen.MethodName.RecalculateTravelability);_screen.RefreshAllPointVisuals();
            if(closeMap && !_wasOpen)_screen.Close(false);
        }
        } finally {_controls=null;_screen=null;_original=null;_preview=false;}
    }
    private static void RefreshPoints() {
        if(!Active || _screen is null)return;
        foreach(var point in ((Dictionary<MapCoord,NMapPoint>)Points.GetValue(_screen)!).Values)
            point.State=Targets.Any(c=>c.ActIndex==_act && c.Coord==point.Point.coord)?MapPointState.Traveled:MapPointState.Untravelable;
        _screen.RefreshAllPointVisuals();
    }
    private static bool CanSelect(NMapPoint point)=>Active && HostCoordinator.IsHost && !HostCoordinator.Busy &&
        ReferenceEquals(RunManager.Instance.DebugOnlyGetState(),_original) && _screen is {IsTraveling:false} &&
        Targets.Any(c=>c.ActIndex==_act && c.Coord==point.Point.coord);
    [HarmonyPatch(typeof(NMapPoint),"get_IsTravelable")]
    private static class VisitedNodeInput {
        static bool Prefix(NMapPoint __instance,ref bool __result){if(!Active)return true;__result=CanSelect(__instance);return false;}
    }
    [HarmonyPatch(typeof(NMapScreen),nameof(NMapScreen.OnMapPointSelectedLocally))]
    private static class NodeSelected {
        static bool Prefix(NMapPoint point) {
            if(!Active)return true;
            if(!CanSelect(point))return false;
            var target=Targets.Single(c=>c.ActIndex==_act && c.Coord==point.Point.coord);
            Cancel();HostCoordinator.Submit(CoreOperation.MapRollback,target.Id);return false;
        }
    }
    [HarmonyPatch(typeof(NMapScreen),"RecalculateTravelability")]
    private static class SelectionVisuals {static void Postfix()=>RefreshPoints();}
    [HarmonyPatch]
    private static class PreviewVotes {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>new[] {"InitMapVotes","RefreshAllMapPointVotes","OnPlayerVoteChanged","OnPlayerVoteCancelled"}
            .Select(name=>AccessTools.Method(typeof(NMapScreen),name));
        static bool Prefix()=>!Active || !_preview;
    }
    [HarmonyPatch]
    private static class LiveTravel {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>[
            AccessTools.Method(typeof(NMapScreen),nameof(NMapScreen.TravelToMapCoord)),
            AccessTools.Method(typeof(RunManager),nameof(RunManager.EnterMapCoord)),
            AccessTools.Method(typeof(RunManager),nameof(RunManager.EnterAct))];
        // Other players can complete native travel while the host previews a
        // previous act. Restore the live view before native map/act logic runs.
        static void Prefix()=>Cancel(false);
    }
    [HarmonyPatch(typeof(NMapScreen),nameof(NMapScreen.Close))]
    private static class CloseSelection {static void Prefix()=>Cancel(false);}
    [HarmonyPatch(typeof(NMapScreen),nameof(NMapScreen.CleanUp))]
    private static class CleanupSelection {static void Prefix()=>Cancel(false);}
    [HarmonyPatch(typeof(NGame),nameof(NGame._Input))]
    private static class CancelKey {
        [HarmonyPriority(Priority.First)]
        static void Prefix(InputEvent inputEvent) {
            if(Active && inputEvent is InputEventKey {Pressed:true,Echo:false,Keycode:Key.Escape}) {
                Cancel();NGame.Instance!.GetViewport().SetInputAsHandled();
            }
        }
    }
}
