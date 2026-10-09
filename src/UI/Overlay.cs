using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using NoSuffering.Ancients;
using NoSuffering.Checkpoints;
using NoSuffering.Combat;
using NoSuffering.Config;
using NoSuffering.Multiplayer;
using NoSuffering.Shops;

namespace NoSuffering.UI;

public static class Overlay
{
    private static CanvasLayer? _layer;
    private static Control? _backdrop;
    private static VBoxContainer? _content;
    private static Label? _toast;
    private static ulong _toastUntil;
    private static bool _settings;
    private static Button? _keyCapture;
    private static string _lastStatus = "";
    private static bool English => ConfigStore.Local.Language == "English";
    private static string T(string zh, string en) => English ? en : zh;

    public static void Attach()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        _layer = new CanvasLayer { Name = "NoSufferingOverlay", Layer = 80 };
        tree.Root.AddChild(_layer);
        _backdrop = new Control { MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _layer.AddChild(_backdrop);
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var shade = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _backdrop.AddChild(shade);
        shade.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _backdrop.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(460, 0), Theme = MakeTheme() };
        center.AddChild(panel);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 22);
        panel.AddChild(margin);
        _content = new VBoxContainer();
        _content.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_content);
        _toast = new Label { Position = new Vector2(24, 90), MouseFilter = Control.MouseFilterEnum.Ignore, Theme = panel.Theme };
        _layer.AddChild(_toast);
        HostCoordinator.Changed += () =>
        {
            if (_backdrop.Visible && !_settings) Refresh();
            var status = HostCoordinator.Status;
            if (status != _lastStatus && !HostCoordinator.Busy && status is not "" and not "操作完成" and not "等待当前动作完成" and not "正在同步全队") Notify(status);
            _lastStatus = status;
        };
        ConfigStore.Changed += () => { if (_backdrop.Visible && !_settings) Refresh(); };
        tree.ProcessFrame += () => { if (_toast is not null && Time.GetTicksMsec() > _toastUntil) _toast.Text = ""; };
    }

    public static void Close()
    {
        _keyCapture = null;
        if (_backdrop is null) return;
        var focus = _backdrop.GetViewport().GuiGetFocusOwner();
        if (focus != null && _backdrop.IsAncestorOf(focus)) focus.ReleaseFocus();
        _backdrop.Visible = false;
        _settings = false;
    }

    public static void Notify(string text)
    {
        if (_toast is null || text == "") return;
        _toast.Text = text;
        _toastUntil = Time.GetTicksMsec() + 4000;
    }

    private static void Input(InputEvent input)
    {
        if (_backdrop is null) return;
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (_backdrop.Visible && key.Keycode == Key.Escape)
        {
            if (_keyCapture != null) { _keyCapture = null; Refresh(); } else Close();
            NGame.Instance!.GetViewport().SetInputAsHandled();
            return;
        }
        if (_keyCapture != null)
        {
            if (key.Keycode is not Key.None and not Key.Ctrl and not Key.Alt and not Key.Shift and not Key.Meta)
            {
                ConfigStore.UpdateLocal(c => c with { TogglePanelKey = new HotkeyBinding(key.Keycode, key.CtrlPressed, key.AltPressed, key.ShiftPressed, key.MetaPressed) });
                _keyCapture = null;
                Refresh();
            }
            NGame.Instance!.GetViewport().SetInputAsHandled();
            return;
        }
        var focus = NGame.Instance!.GetViewport().GuiGetFocusOwner();
        if (focus is not LineEdit and not TextEdit && ConfigStore.Local.TogglePanelKey.Matches(key))
        {
            if (_backdrop.Visible) Close();
            else { _backdrop.Visible = true; _settings = !RunManager.Instance.IsInProgress; Refresh(); }
            NGame.Instance.GetViewport().SetInputAsHandled();
            return;
        }
        if (_backdrop.Visible && focus is not LineEdit and not TextEdit && key.Keycode is not Key.Tab and not Key.Enter and not Key.KpEnter and not Key.Space and not Key.Up and not Key.Down and not Key.Left and not Key.Right)
            NGame.Instance!.GetViewport().SetInputAsHandled();
    }

    private static void Refresh()
    {
        if (_content is null) return;
        ((Control)_content.GetParent().GetParent()).Theme = MakeTheme();
        foreach (var child in _content.GetChildren()) { _content.RemoveChild(child); child.QueueFree(); }
        var heading = new HBoxContainer();
        _content.AddChild(heading);
        var title = new Label { Text = _settings ? T("不吃苦 · 设置", "NoSuffering · Settings") : T("不吃苦", "NoSuffering"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 25);
        heading.AddChild(title);
        AddButton(heading, _settings ? T("返回", "Back") : T("设置", "Settings"), () => { if (!RunManager.Instance.IsInProgress) { Close(); return; } _settings = !_settings; _keyCapture = null; Refresh(); });
        AddButton(heading, "×", Close);
        _content.AddChild(new HSeparator());
        if (_settings) { Settings(); BindFocus(); return; }
        var r = ConfigStore.Rules;
        var personalDisabled = HostCoordinator.Busy || CompanionStore.Error != null;
        var disabled = !HostCoordinator.IsHost || personalDisabled;
        if (CombatManager.Instance.IsInProgress && CombatService.Baseline is not null)
        {
            ActionButton(CoreOperation.CombatRestart, T("重新开始战斗", "Restart combat"), CombatService.UnavailableReason ?? T("保留本次牌序。", "Keep the current deck order."), disabled || !r.EnableCombatRestart || CombatService.UnavailableReason != null);
            ActionButton(CoreOperation.CombatReroll, T("重开并刷新牌序", "Restart with new deck order"), CombatService.RerollUnavailableReason ?? CombatService.UnavailableReason ?? T("重新随机牌序。", "Shuffle the deck order."), disabled || !r.EnableCombatReroll || CombatService.UnavailableReason != null || CombatService.RerollUnavailableReason != null);
        }
        if (RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.EventRoom room && room.CanonicalEvent is MegaCrit.Sts2.Core.Models.AncientEventModel)
        {
            var ancientReason = AncientService.GetUnavailableReason(true, RunManager.Instance.NetService.NetId, r);
            var optionsReason = AncientService.GetUnavailableReason(false, RunManager.Instance.NetService.NetId, r);
            ActionButton(CoreOperation.AncientReroll, T("刷新先古之民", "Reroll ancient") + Cost(r.AncientCostMode, r.AncientHpCost), ancientReason ?? T("更换先古之民。", "Choose a different ancient."), disabled || ancientReason != null);
            ActionButton(CoreOperation.AncientOptionsReroll, T("刷新先古之民奖励", "Reroll ancient rewards") + Cost(r.OptionsCostMode, r.OptionsHpCost), optionsReason ?? T("刷新当前奖励。", "Refresh the rewards."), personalDisabled || optionsReason != null);
        }
        if (RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.MerchantRoom)
        {
            var reason = ShopService.GetUnavailableReason(RunManager.Instance.NetService.NetId, r);
            ActionButton(CoreOperation.ShopReroll, T("刷新商店", "Reroll shop"), reason ?? T("重新生成全部商品。", "Generate new stock."), personalDisabled || reason != null);
        }
        if (CombatManager.Instance.IsInProgress && RunManager.Instance.DebugOnlyGetState()?.CurrentActIndex == 2 &&
            RunManager.Instance.DebugOnlyGetState()?.CurrentRoom is MegaCrit.Sts2.Core.Rooms.CombatRoom { RoomType: MegaCrit.Sts2.Core.Rooms.RoomType.Boss })
        {
            var reason = BossHealthService.GetUnavailableReason(r);
            var row = new HBoxContainer(); _content!.AddChild(row);
            var percent = new SpinBox { MinValue = 1, MaxValue = 1000, Step = 1, Value = ConfigStore.Local.BossHealthPercent, Suffix = "%", CustomMinimumSize = new Vector2(112, 0), Editable = !disabled && reason == null };
            row.AddChild(percent);
            percent.ValueChanged += value => ConfigStore.UpdateLocal(c => c with { BossHealthPercent = (int)value });
            var button = AddButton(row, T("增加 Boss 生命", "Increase boss health"), () => HostCoordinator.Submit(CoreOperation.BossHealthIncrease, percent: (int)percent.Value), disabled || reason != null, reason ?? T("增加上限并补充相同生命，保留已受伤害。", "Increase maximum and current HP equally, preserving damage taken."));
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        AddButton(_content, T("路线回滚", "Rollback route"), () => { Close(); MapRollbackSelection.Begin(); }, disabled || !r.EnableMapRollback || MapRollbackSelection.Targets.Count == 0, T("在地图上选择走过的节点。", "Choose a visited node on the map."));
        if (CompanionStore.Error != null) AddText(CompanionStore.Error);
        else if (HostCoordinator.Busy) AddText(T("处理中…", "Working…"));
        BindFocus();
    }

    private static void Settings()
    {
        var local = ConfigStore.EditableLocal;
        var r = ConfigStore.EditableRules;
        var key = AddButton(Row(T("快捷键", "Shortcut")), KeyLabel(local.TogglePanelKey), () => { });
        key.Pressed += () => { _keyCapture = key; key.Text = T("按下快捷键…", "Press a shortcut…"); };
        var language = new OptionButton();
        foreach (var label in new[] { T("跟随游戏", "Game language"), "English", "简体中文" }) language.AddItem(label);
        language.Selected = local.Language switch { "English" => 1, "简体中文" => 2, _ => 0 };
        language.ItemSelected += index => { ConfigStore.UpdateLocal(c => c with { Language = index == 1 ? "English" : index == 2 ? "简体中文" : "Auto" }); Refresh(); };
        Row(T("语言", "Language")).AddChild(language);
        _content!.AddChild(new HSeparator());
        Toggle(T("路线回滚", "Rollback route"), r.EnableMapRollback, (rules, value) => rules with { EnableMapRollback = value });
        Toggle(T("重新开始战斗", "Restart combat"), r.EnableCombatRestart, (rules, value) => rules with { EnableCombatRestart = value });
        Toggle(T("重开并刷新牌序", "Restart with new deck order"), r.EnableCombatReroll, (rules, value) => rules with { EnableCombatReroll = value });
        Toggle(T("刷新商店", "Reroll shop"), r.EnableShopReroll, (rules, value) => rules with { EnableShopReroll = value });
        Toggle(T("增加第三幕 Boss 生命", "Increase act-three boss health"), r.EnableBossHealthIncrease, (rules, value) => rules with { EnableBossHealthIncrease = value });
        Toggle(T("刷新先古之民", "Reroll ancient"), r.EnableAncientReroll, (rules, value) => rules with { EnableAncientReroll = value });
        CostSetting(T("费用", "Cost"), r.AncientCostMode, r.AncientHpCost, true);
        Toggle(T("刷新先古之民奖励", "Reroll ancient rewards"), r.EnableAncientOptionsReroll, (rules, value) => rules with { EnableAncientOptionsReroll = value });
        CostSetting(T("费用", "Cost"), r.OptionsCostMode, r.OptionsHpCost, false);
    }

    private static void Toggle(string text, bool enabled, Func<HostRules, bool, HostRules> update)
    {
        var toggle = new CheckButton { ButtonPressed = enabled, Disabled = !ConfigStore.RulesWritable };
        Row(text).AddChild(toggle);
        toggle.Toggled += value => ConfigStore.ChangeRules(r => update(r, value));
    }

    private static void CostSetting(string label, RefreshCostMode mode, int amount, bool ancient)
    {
        var row = Row(label);
        var select = new OptionButton { Disabled = !ConfigStore.RulesWritable };
        select.AddItem(T("免费", "Free")); select.AddItem("HP"); select.Selected = (int)mode; row.AddChild(select);
        var hp = new SpinBox { MinValue = 1, MaxValue = 99, Step = 1, Value = amount, Editable = ConfigStore.RulesWritable && mode == RefreshCostMode.Hp, CustomMinimumSize = new Vector2(92, 0) };
        row.AddChild(hp);
        select.ItemSelected += index =>
        {
            ConfigStore.ChangeRules(r => ancient ? r with { AncientCostMode = (RefreshCostMode)index } : r with { OptionsCostMode = (RefreshCostMode)index });
            hp.Editable = ConfigStore.RulesWritable && index == 1;
        };
        hp.ValueChanged += value => ConfigStore.ChangeRules(r => ancient ? r with { AncientHpCost = (int)value } : r with { OptionsHpCost = (int)value });
    }

    private static HBoxContainer Row(string label)
    {
        var row = new HBoxContainer(); _content!.AddChild(row);
        row.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        return row;
    }
    private static string KeyLabel(HotkeyBinding key) => (key.Ctrl ? "Ctrl + " : "") + (key.Alt ? "Alt + " : "") + (key.Shift ? "Shift + " : "") + (key.Meta ? "Meta + " : "") + OS.GetKeycodeString(key.Key);
    private static string Cost(RefreshCostMode mode, int amount) => mode == RefreshCostMode.Hp ? $" · {amount} HP" : "";
    private static void AddText(string text) => _content!.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(400, 0) });
    private static void ActionButton(CoreOperation op, string text, string help, bool disabled) => AddButton(_content!, text, () => HostCoordinator.Submit(op), disabled, help);
    private static Button AddButton(Node parent, string text, Action action, bool disabled = false, string help = "")
    {
        var button = new Button { Text = text, Disabled = disabled, TooltipText = help, CustomMinimumSize = new Vector2(0, 38), FocusMode = Control.FocusModeEnum.All };
        button.Pressed += action; parent.AddChild(button); return button;
    }

    private static void BindFocus()
    {
        var controls = new List<Control>();
        Collect(_content!);
        if (controls.Count == 0) return;
        for (var i = 0; i < controls.Count; i++)
        {
            controls[i].FocusPrevious = controls[(i + controls.Count - 1) % controls.Count].GetPath();
            controls[i].FocusNext = controls[(i + 1) % controls.Count].GetPath();
        }
        controls[0].GrabFocus();
        void Collect(Node node)
        {
            if (node is Control control && control.FocusMode == Control.FocusModeEnum.All && (control is not BaseButton button || !button.Disabled)) controls.Add(control);
            foreach (var child in node.GetChildren()) Collect(child);
        }
    }

    internal static Theme MakeTheme()
    {
        var theme = new Theme { DefaultFontSize = 19 };
        // Reference installed game fonts; no game assets are bundled with the mod.
        theme.DefaultFont = ResourceLoader.Load<Font>(English ? "res://fonts/kreon_regular.ttf" : "res://themes/fonts/zhs/noto_sans_mono_cjksc_regular_shared.tres");
        var cream = new Color("efe4cf");
        var muted = new Color("948e82");
        var panel = Box("1c252b", "887852", 1, 8);
        panel.ShadowColor = new Color(0, 0, 0, 0.45f);
        panel.ShadowSize = 12;
        theme.SetStylebox("panel", "PanelContainer", panel);
        theme.SetColor("font_color", "Label", cream);
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            theme.SetColor("font_color", type, cream);
            theme.SetColor("font_hover_color", type, Colors.White);
            theme.SetColor("font_pressed_color", type, cream);
            theme.SetColor("font_disabled_color", type, muted);
            theme.SetStylebox("normal", type, Box("29353c", "6e654e", 1, 4));
            theme.SetStylebox("hover", type, Box("38464a", "b5a06e", 1, 4));
            theme.SetStylebox("pressed", type, Box("202b31", "b5a06e", 1, 4));
            theme.SetStylebox("disabled", type, Box("232c31", "484c47", 1, 4));
            theme.SetStylebox("focus", type, Box("00000000", "ebd8a0", 2, 4));
        }
        theme.SetStylebox("normal", "LineEdit", Box("151e24", "6e654e", 1, 4));
        theme.SetStylebox("read_only", "LineEdit", Box("20292f", "484c47", 1, 4));
        theme.SetStylebox("focus", "LineEdit", Box("00000000", "ebd8a0", 2, 4));
        theme.SetColor("font_color", "LineEdit", cream);
        theme.SetColor("font_uneditable_color", "LineEdit", muted);
        theme.SetColor("caret_color", "LineEdit", cream);
        theme.SetColor("selection_color", "LineEdit", new Color("52605d"));
        theme.SetStylebox("panel", "PopupMenu", Box("1c252b", "887852", 1, 5));
        theme.SetStylebox("hover", "PopupMenu", Box("38464a", "b5a06e", 1, 3));
        theme.SetColor("font_color", "PopupMenu", cream);
        theme.SetColor("font_hover_color", "PopupMenu", Colors.White);
        theme.SetColor("font_disabled_color", "PopupMenu", muted);
        theme.SetColor("font_color", "CheckButton", cream);
        theme.SetColor("font_disabled_color", "CheckButton", muted);
        theme.SetStylebox("separator", "HSeparator", new StyleBoxLine { Color = new Color("655d49"), Thickness = 1 });
        theme.SetColor("font_color", "TooltipLabel", cream);
        theme.SetStylebox("panel", "TooltipPanel", Box("1c252b", "887852", 1, 4));
        return theme;
    }
    private static StyleBoxFlat Box(string fill, string border, int width, int radius) => new()
    {
        BgColor = new Color(fill), BorderColor = new Color(border), BorderWidthLeft = width, BorderWidthRight = width, BorderWidthTop = width, BorderWidthBottom = width,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6
    };
    // Open after GUI/key-capture handlers (including optional config frameworks).
    // Once open, its own capture/Esc handling stays in _Input.
    [HarmonyPatch(typeof(NHotkeyManager), nameof(NHotkeyManager._UnhandledInput))]
    private static class ModalHotkeys {
        static bool Prefix(InputEvent inputEvent) {
            if (_backdrop?.Visible == true) return false;
            Input(inputEvent);
            return _backdrop?.Visible != true;
        }
    }
    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    private static class PanelInput { static void Prefix(InputEvent inputEvent) { if (_backdrop?.Visible == true) Input(inputEvent); } }
}
