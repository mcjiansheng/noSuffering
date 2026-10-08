using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;

namespace NoSuffering.UI;

public static class Overlay
{
    private static CanvasLayer? _layer;
    private static Control? _backdrop;
    private static VBoxContainer? _content;
    private static ConfirmationDialog? _confirm;
    private static Label? _toast;
    private static ulong _toastUntil;
    private static long _selectedCheckpoint;
    private static bool English=>ConfigStore.Local.Language=="English";
    private static string T(string zh,string en)=>English?en:zh;
    public static void Attach() {
        var tree=(SceneTree)Engine.GetMainLoop();
        _layer=new CanvasLayer {Name="NoSufferingOverlay",Layer=80};
        tree.Root.AddChild(_layer);
        _backdrop=new Control {MouseFilter=Control.MouseFilterEnum.Stop,Visible=false};
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);_layer.AddChild(_backdrop);
        var shade=new ColorRect {Color=new Color(0,0,0,0.55f),MouseFilter=Control.MouseFilterEnum.Ignore};
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);_backdrop.AddChild(shade);
        var center=new CenterContainer();center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);_backdrop.AddChild(center);
        var panel=new PanelContainer {CustomMinimumSize=new Vector2(570,0)};center.AddChild(panel);
        var margin=new MarginContainer();foreach(var side in new[]{"left","right","top","bottom"})margin.AddThemeConstantOverride("margin_"+side,24);panel.AddChild(margin);
        _content=new VBoxContainer();_content.AddThemeConstantOverride("separation",12);margin.AddChild(_content);
        _confirm=new ConfirmationDialog {Title=T("路线回滚","Rollback route"),DialogText="",MinSize=new Vector2I(460,180)};
        _layer.AddChild(_confirm);_confirm.Confirmed+=()=>HostCoordinator.Submit(CoreOperation.MapRollback,_selectedCheckpoint);
        _toast=new Label {Position=new Vector2(24,90),MouseFilter=Control.MouseFilterEnum.Ignore};_layer.AddChild(_toast);
        HostCoordinator.Changed+=()=>{if(_backdrop!.Visible)Refresh();Notify(HostCoordinator.Status);};
        ConfigStore.Changed+=()=>{if(_backdrop!.Visible)Refresh();};
        tree.ProcessFrame+=()=>{if(_toast is not null && Time.GetTicksMsec()>_toastUntil)_toast.Text="";};
    }
    public static void Close(){if(_backdrop is not null)_backdrop.Visible=false;_confirm?.Hide();}
    public static void Notify(string text){if(_toast is null || text=="")return;_toast.Text=text;_toastUntil=Time.GetTicksMsec()+4000;}
    private static void Input(InputEvent input) {
        if(input is not InputEventKey {Pressed:true,Echo:false} key || _backdrop is null)return;
        if(key.Keycode==Key.Escape && _backdrop.Visible){Close();NGame.Instance!.GetViewport().SetInputAsHandled();return;}
        var focus=NGame.Instance!.GetViewport().GuiGetFocusOwner();
        if(focus is LineEdit or TextEdit || focus?.GetType().Name=="KeyCaptureNode")return;
        if(!ConfigStore.Local.TogglePanelKey.Matches(key) || !RunManager.Instance.IsInProgress)return;
        _backdrop.Visible=!_backdrop.Visible;if(_backdrop!.Visible)Refresh();
        NGame.Instance!.GetViewport().SetInputAsHandled();
    }
    private static void Refresh() {
        if(_content is null)return;
        foreach(var child in _content.GetChildren()){_content.RemoveChild(child);child.QueueFree();}
        var heading=new HBoxContainer();_content.AddChild(heading);
        heading.AddChild(new Label {Text="不吃苦 · NoSuffering",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
        AddButton(heading,T("设置","Settings"),OpenSettings,false);AddButton(heading,T("关闭","Close"),Close,false);
        AddText(HostCoordinator.IsHost?T("房主控制","Host controls this run"):T("本局由房主控制","Only the host can act"));
        if(CompanionStore.Error is not null){AddText(CompanionStore.Error);return;}
        if(HostCoordinator.Busy){AddText(T("正在同步全队，请稍候","Synchronizing the party…"));return;}
        var r=ConfigStore.Rules;bool client=!HostCoordinator.IsHost;
        if(CombatManager.Instance.IsInProgress && CombatService.Baseline is not null) {
            ActionButton(CoreOperation.CombatRestart,T("重新开始战斗 · 免费","Restart combat · Free"),T("恢复全队战前状态，保留本次牌序。","Restore pre-combat state and keep the current deck order."),client||!r.EnableCombatRestart||CombatService.UnavailableReason is not null);
            ActionButton(CoreOperation.CombatReroll,T("重开并刷新牌序 · 免费","Restart with new deck order · Free"),T("恢复全队战前状态，重新随机每人的牌序。","Restore pre-combat state with a new deck order for everyone."),client||!r.EnableCombatReroll||CombatService.UnavailableReason is not null||CombatService.RerollUnavailableReason is not null);
        }
        if(CombatService.UnavailableReason is { } combatReason)AddText(combatReason);
        else if(CombatService.RerollUnavailableReason is { } orderReason && CombatManager.Instance.IsInProgress)AddText(orderReason);
        if(RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.EventRoom room && room.CanonicalEvent is MegaCrit.Sts2.Core.Models.AncientEventModel) {
            ActionButton(CoreOperation.AncientReroll,T("刷新先古之民","Reroll ancient")+Cost(r.AncientCostMode,r.AncientHpCost),"",client||!r.EnableAncientReroll||!AncientService.Available);
            ActionButton(CoreOperation.AncientOptionsReroll,T("刷新先古之民奖励","Reroll ancient rewards")+Cost(r.OptionsCostMode,r.OptionsHpCost),"",client||!r.EnableAncientOptionsReroll||!AncientService.Available);
            if(AncientService.UnavailableReason is { } reason)AddText(reason);
        }
        AddText(T("路线回滚 · 免费","Rollback route · Free"));
        var history=new VBoxContainer();
        var scroll=new ScrollContainer {CustomMinimumSize=new Vector2(0,180)};scroll.AddChild(history);_content.AddChild(scroll);history.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        foreach(var cp in CheckpointService.History.Reverse()) {
            var summary=cp.Label+" · "+RoomLabel(cp.LastRoom)+" · HP "+string.Join(" / ",cp.PlayerHp.OrderBy(p=>p.Key).Select(p=>p.Value))+" · "+DateTimeOffset.FromUnixTimeSeconds(cp.CreatedAt).ToLocalTime().ToString("HH:mm");
            AddButton(history,summary,()=>Rollback(cp),client||!r.EnableMapRollback);
        }
        if(CheckpointService.History.Count==0)history.AddChild(new Label {Text=T("尚无已完成的路线检查点","No completed route checkpoints yet")});
    }
    private static string RoomLabel(string room)=>room switch {
        "Monster"=>T("普通战斗","Combat"),"Elite"=>T("精英","Elite"),"Boss"=>T("首领","Boss"),
        "Event"=>T("事件","Event"),"Shop"=>T("商店","Shop"),"RestSite"=>T("休息点","Rest site"),
        "Treasure"=>T("宝箱","Treasure"),_=>T("地图","Map")
    };
    private static string Cost(RefreshCostMode mode,int amount)=>mode==RefreshCostMode.Free?T(" · 免费"," · Free"):T($" · 房主支付 {amount} 点生命",$" · Host pays {amount} HP");
    private static void AddText(string text)=>_content!.AddChild(new Label {Text=text,AutowrapMode=TextServer.AutowrapMode.WordSmart,CustomMinimumSize=new Vector2(520,0)});
    private static void ActionButton(CoreOperation op,string text,string help,bool disabled) {AddButton(_content!,text,()=>HostCoordinator.Submit(op),disabled);if(help!="")AddText(help);}
    private static void AddButton(Node parent,string text,Action action,bool disabled) {
        var button=new Button {Text=text,Disabled=disabled,CustomMinimumSize=new Vector2(0,42),FocusMode=Control.FocusModeEnum.All};button.Pressed+=action;parent.AddChild(button);
    }
    private static void Rollback(MapCheckpoint checkpoint) {
        if(ConfigStore.Local.ConfirmMapRollback){_selectedCheckpoint=checkpoint.Id;_confirm!.DialogText=T("返回：","Return to: ")+checkpoint.Label+T("\n这之后的进度将被撤销。","\nProgress after this point will be undone.");_confirm.PopupCentered();}
        else HostCoordinator.Submit(CoreOperation.MapRollback,checkpoint.Id);
    }
    private static void OpenSettings() {
        Close();
        if(!ConfigStore.OpenSettings()){Notify(T("安装 ModConfig 后可在游戏内调整设置","Install ModConfig for in-game settings"));return;}
        // The same native settings screen used by the pause menu.
        var stack=NRun.Instance!.GlobalUi.SubmenuStack;
        stack.ShowScreen(MegaCrit.Sts2.Core.Nodes.Screens.CapstoneSubmenuType.Settings);
    }
    [HarmonyPatch(typeof(NGame),nameof(NGame._Input))]
    private static class PanelInput {static void Prefix(InputEvent inputEvent)=>Input(inputEvent);}
}
