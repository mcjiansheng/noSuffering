using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Helpers;

namespace NoSuffering.Config;

// Optional BaseLib 3.4.7 adapter. The emitted class is only its registration
// shell: it deliberately has no static config properties for BaseLib to load
// or save. Every native control below reads/writes the existing ConfigStore.
public static class BaseLibSettings
{
    private static Type? _baseType;
    private static object? _config;
    private static Control? _captureButton;
    public static bool Registered => _config is not null;
    public enum LanguageChoice { Auto, English, 简体中文 }
    private static string T(string zh,string en)=>ConfigStore.Local.Language=="English"?en:zh;

    public static void Initialize()
    {
        if(Engine.GetMainLoop() is not SceneTree tree)return;
        void Detect() {
            tree.ProcessFrame-=Detect;
            try {
                var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="BaseLib");
                if(assembly is null)return;
                _baseType=assembly.GetType("BaseLib.Config.ModConfig",true)!;
                var module=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NoSuffering.BaseLibAdapter"),AssemblyBuilderAccess.Run).DefineDynamicModule("Adapter");
                var type=module.DefineType("NoSuffering.BaseLibPage",TypeAttributes.Public|TypeAttributes.Sealed,_baseType);
                var constructor=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,Type.EmptyTypes).GetILGenerator();
                constructor.Emit(OpCodes.Ldarg_0);constructor.Emit(OpCodes.Ldstr,"NoSuffering.BaseLibPage");
                constructor.Emit(OpCodes.Call,_baseType.GetConstructor([typeof(string)])!);constructor.Emit(OpCodes.Ret);
                var setup=type.DefineMethod("SetupConfigUI",MethodAttributes.Public|MethodAttributes.Virtual,typeof(void),[typeof(Control)]);
                var il=setup.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call,typeof(BaseLibSettings).GetMethod(nameof(BuildPage))!);il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(setup,_baseType.GetMethod("SetupConfigUI")!);
                var visible=type.DefineMethod("VisibleInModList",MethodAttributes.Public|MethodAttributes.Virtual,typeof(bool),Type.EmptyTypes);
                il=visible.GetILGenerator();il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Ret);
                type.DefineMethodOverride(visible,_baseType.GetMethod("VisibleInModList")!);
                var config=Activator.CreateInstance(type.CreateType()!)!;
                assembly.GetType("BaseLib.Config.ModConfigRegistry",true)!.GetMethod("Register")!.Invoke(null,["NoSuffering",config]);
                _config=config;
                GD.Print("[NoSuffering] BaseLib settings registered.");
            } catch(Exception e) {GD.PrintErr($"[NoSuffering] BaseLib settings unavailable: {e}");}
        }
        tree.ProcessFrame+=Detect;
    }

    private sealed record Setting(string Key,string Zh,string En,bool Local=false,int Max=0);
    private static readonly Setting[] Settings=[
        new("TogglePanelKey","面板快捷键","Panel shortcut",true),
        new("Language","语言","Language",true),
        new("EnableMapRollback","路线回滚","Rollback route"),
        new("EnableAncientReroll","刷新先古之民","Reroll ancient"),
        new("EnableAncientOptionsReroll","刷新先古之民奖励","Reroll ancient rewards"),
        new("EnableCombatRestart","重新开始战斗","Restart combat"),
        new("EnableCombatReroll","重开并刷新牌序","Restart with new deck order"),
        new("EnableShopReroll","刷新商店","Reroll shop"),
        new("EnableBossHealthIncrease","增加第三幕 Boss 生命","Increase act-three boss health"),
        new("BossHealthPercent","Boss 生命增加比例（%）","Boss health increase (%)",true,1000),
        new("AncientCostMode","先古之民刷新费用","Ancient reroll cost"),
        new("AncientHpCost","先古之民刷新生命费用","Ancient reroll HP",false,99),
        new("OptionsCostMode","奖励刷新费用","Reward reroll cost"),
        new("OptionsHpCost","奖励刷新生命费用","Reward reroll HP",false,99)
    ];

    public static void BuildPage(object config,Control container)
    {
        var rows=new List<(Setting Setting,Control Label,Control Control)>();
        var interaction=new Dictionary<Control,(Control.FocusModeEnum Focus,Control.MouseFilterEnum Mouse)>();
        var baseType=_baseType!;
        Control Raw(string method,params object[] args)=>(Control)baseType.GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(config,args)!;
        Control Label(string text)=>(Control)baseType.GetMethod("CreateRawLabelControl")!.Invoke(null,[text,28])!;
        void SetLabel(Control label,string text) {if(label is RichTextLabel rich)rich.Text=text;else if(label is Godot.Label plain)plain.Text=text;}
        foreach(var setting in Settings) {
            Control control;
            if(setting.Key=="TogglePanelKey") {
                Control? button=null;
                button=Raw("CreateRawButtonControl",KeyText(),new Action(()=> {
                    _captureButton=button;button!.GetNode<Godot.Label>("Label").Text=T("按下快捷键…","Press a shortcut…");
                }));
                control=button;
                button.TreeExiting+=()=>{if(ReferenceEquals(_captureButton,button))_captureButton=null;};
            } else {
                var property=typeof(Values).GetProperty(setting.Key)!;
                string factory=property.PropertyType==typeof(bool)?"CreateRawTickboxControl":property.PropertyType==typeof(int)?"CreateRawSliderControl":"CreateRawDropdownControl";
                control=Raw(factory,property);
                if(setting.Max>0)control.GetType().GetMethod("SetRange")!.Invoke(control,[1d,(double)setting.Max,(double?)1d]);
            }
            control.Name=setting.Key;
            var label=Label(T(setting.Zh,setting.En));
            var row=(Control)baseType.Assembly.GetType("BaseLib.Config.UI.NConfigOptionRow",true)!
                .GetConstructor([typeof(string),typeof(string),typeof(Control),typeof(Control)])!.Invoke(["NoSuffering-",setting.Key,label,control]);
            container.AddChild(row);rows.Add((setting,label,control));
        }
        void Refresh() {
            if(!GodotObject.IsInstanceValid(container))return;
            baseType.GetMethod("ConfigReloaded")!.Invoke(config,null);
            foreach(var row in rows) {
                SetLabel(row.Label,T(row.Setting.Zh,row.Setting.En));
                if(row.Setting.Key=="TogglePanelKey" && !ReferenceEquals(_captureButton,row.Control))row.Control.GetNode<Godot.Label>("Label").Text=KeyText();
                bool enabled=row.Setting.Local || ConfigStore.RulesWritable;
                row.Control.ProcessMode=enabled?Node.ProcessModeEnum.Inherit:Node.ProcessModeEnum.Disabled;
                row.Control.Modulate=new Color(1,1,1,enabled?1:0.45f);
                row.Control.TooltipText=enabled?"":T("本局规则由房主设置","The host controls this run's rules");
                ((Control)row.Control.GetParent()).TooltipText=row.Control.TooltipText;
                foreach(var child in Controls(row.Control)) {
                    if(!interaction.ContainsKey(child))interaction[child]=(child.FocusMode,child.MouseFilter);
                    var saved=interaction[child];child.FocusMode=enabled?saved.Focus:Control.FocusModeEnum.None;
                    child.MouseFilter=enabled?saved.Mouse:Control.MouseFilterEnum.Ignore;
                    if(!enabled && child.HasFocus())child.ReleaseFocus();
                }
            }
            baseType.Assembly.GetType("BaseLib.Config.SimpleModConfig",true)!.GetMethod("SetupFocusNeighbors")!.Invoke(null,[container]);
        }
        // Native controls emit ConfigChanged even when a client setter was
        // rejected; reload their display from the authoritative store as well.
        EventHandler nativeChanged=(_,_)=>Refresh();
        baseType.GetEvent("ConfigChanged")!.AddEventHandler(config,nativeChanged);
        ConfigStore.Changed+=Refresh;
        container.TreeExiting+=()=>{ConfigStore.Changed-=Refresh;baseType.GetEvent("ConfigChanged")!.RemoveEventHandler(config,nativeChanged);};
        Refresh();
    }
    private static IEnumerable<Control> Controls(Node node) {
        if(node is Control control)yield return control;
        foreach(var child in node.GetChildren())foreach(var descendant in Controls(child))yield return descendant;
    }
    private static string KeyText() {
        var key=ConfigStore.EditableLocal.TogglePanelKey;
        using var input=new InputEventKey {Keycode=key.Key,CtrlPressed=key.Ctrl,AltPressed=key.Alt,ShiftPressed=key.Shift,MetaPressed=key.Meta};
        return input.AsTextKeycode();
    }

    // Explicit isolated-lab diagnostic. Exercises native BaseLib callbacks,
    // not pointer input or the framework menu's visual layout.
    public static async Task<string> ProbeAsync()
    {
        var args=OS.GetCmdlineUserArgs();
        var labRoot=OS.GetDataDir().Replace('\\','/').TrimEnd('/')+"/NoSufferingLab/";
        if(!args.Contains("--ns-lab-probe") || !args.Contains("--ns-baselib-probe") ||
           !string.Equals(CommandLineHelper.GetValue("force-steam"),"off",StringComparison.OrdinalIgnoreCase) ||
           !OS.GetUserDataDir().Replace('\\','/').StartsWith(labRoot,StringComparison.Ordinal))
            throw new InvalidOperationException("BaseLib settings probe requires an isolated Steam-off lab.");
        if(_config is null || _baseType is null)throw new InvalidOperationException("BaseLib settings are not registered.");
        if(ConfigStore.OperationInProgress || !ConfigStore.RulesWritable)throw new InvalidOperationException("Settings probe requires idle host settings.");
        var local=ConfigStore.EditableLocal;var rules=ConfigStore.EditableRules;bool writable=ConfigStore.RulesWritable;
        var container=new VBoxContainer {Name="NoSufferingBaseLibProbe",Size=new Vector2(1200,1000)};
        var facts=new Dictionary<string,object>();
        void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
        Control Setting(string key)=>(Control)container.GetNode(key).GetType().GetProperty("SettingControl")!.GetValue(container.GetNode(key))!;
        try {
            var registry=_baseType.Assembly.GetType("BaseLib.Config.ModConfigRegistry",true)!;
            var get=registry.GetMethods().Single(m=>m.Name=="Get" && !m.IsGenericMethod);
            Require(ReferenceEquals(get.Invoke(null,["NoSuffering"]),_config),"Framework registry does not contain our page.");
            ((SceneTree)Engine.GetMainLoop()).Root.AddChild(container);
            _baseType.GetMethod("SetupConfigUI")!.Invoke(_config,[container]);
            await GameBridge.GameBridge.Frame();
            Require(container.GetChildCount()==Settings.Length,"Not all settings have framework rows.");
            facts["native_rows"]=Settings.Length;
            var toggle=Setting("EnableMapRollback");
            toggle.Call(rules.EnableMapRollback?"OnUntick":"OnTick");
            Require(ConfigStore.Rules.EnableMapRollback!=rules.EnableMapRollback,"Native toggle did not update ConfigStore.");
            var slider=Setting("BossHealthPercent");slider.Call("OnValueChanged",41d);
            Require(ConfigStore.Local.BossHealthPercent==42,"Native slider did not update ConfigStore.");
            var positioner=Setting("AncientCostMode");
            var dropdown=Controls(positioner).Single(c=>c.GetType().FullName=="BaseLib.Config.UI.NConfigDropdown");
            object ItemData(Control candidate)=>candidate.GetType().GetField("Data")!.GetValue(candidate)!;
            var item=Controls(dropdown).First(c=>c.GetType().FullName=="BaseLib.Config.UI.NConfigDropdownItem" &&
                ItemData(c).GetType().GetProperty("Value")!.GetValue(ItemData(c))!.ToString()=="Hp");
            dropdown.Call("OnDropdownItemSelected",item);
            Require(ConfigStore.Rules.AncientCostMode==RefreshCostMode.Hp,"Native dropdown did not update ConfigStore.");
            var button=Setting("TogglePanelKey");button.Call("OnReleased",button);
            Require(ReferenceEquals(_captureButton,button),"Native shortcut button did not start key capture.");
            using(var key=new InputEventKey {Pressed=true,Keycode=Key.F7,CtrlPressed=true})NGame.Instance!._Input(key);
            Require(ConfigStore.Local.TogglePanelKey is {Key:Key.F7,Ctrl:true},"Shortcut capture did not update ConfigStore.");
            using(var saved=JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("user://NoSuffering/config.json"))))
                Require(saved.RootElement.GetProperty("Local").GetProperty("BossHealthPercent").GetInt32()==42,"Framework changes were not persisted in the existing file.");
            ConfigStore.UpdateLocal(c=>c with {BossHealthPercent=67,Language="English"});
            var inner=(Godot.Range)slider.GetNode("Slider");
            Require(Math.Abs(inner.Value-66)<0.01,"Standalone store update did not synchronize native slider.");
            Require(button.GetNode<Godot.Label>("Label").Text==KeyText(),"Standalone store update did not synchronize shortcut label.");
            bool current=ConfigStore.Rules.EnableMapRollback;
            ConfigStore.SetRulesWritable(false);
            Require(toggle.ProcessMode==Node.ProcessModeEnum.Disabled && !Controls(toggle).Any(c=>c.FocusMode!=Control.FocusModeEnum.None),"Client rule control remains interactive.");
            toggle.Call(current?"OnUntick":"OnTick");
            Require(ConfigStore.Rules.EnableMapRollback==current,"Client native callback changed host rules.");
            Require((bool)toggle.GetType().GetProperty("IsTicked")!.GetValue(toggle)! == current,"Rejected client edit was not refreshed.");
            Require(slider.ProcessMode!=Node.ProcessModeEnum.Disabled,"Client local preferences were disabled.");
            facts["callbacks"]="native toggle, slider, dropdown, shortcut button and captured key";
            facts["sync"]="existing config persisted; standalone update reflected; client rule mutation rejected and display restored";
            return JsonSerializer.Serialize(facts);
        } finally {
            _captureButton=null;ConfigStore.SetRulesWritable(true);
            ConfigStore.UpdateLocal(_=>local);ConfigStore.ChangeRules(_=>rules);ConfigStore.SetRulesWritable(writable);
            container.GetParent()?.RemoveChild(container);container.QueueFree();
        }
    }
    [HarmonyPatch(typeof(NGame),nameof(NGame._Input))]
    private static class CaptureKey {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(InputEvent inputEvent) {
            if(_captureButton is not { } button || !GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree()) {_captureButton=null;return true;}
            if(inputEvent is not InputEventKey {Pressed:true,Echo:false} key)return true;
            if(key.Keycode is Key.Ctrl or Key.Alt or Key.Shift or Key.Meta) {NGame.Instance!.GetViewport().SetInputAsHandled();return false;}
            _captureButton=null;
            if(key.Keycode!=Key.Escape && key.Keycode!=Key.None)
                ConfigStore.UpdateLocal(c=>c with {TogglePanelKey=new HotkeyBinding(key.Keycode,key.CtrlPressed,key.AltPressed,key.ShiftPressed,key.MetaPressed)});
            button.GetNode<Godot.Label>("Label").Text=KeyText();
            NGame.Instance!.GetViewport().SetInputAsHandled();return false;
        }
    }

    // Reflection metadata for BaseLib native controls, separate from the
    // registration shell so BaseLib never persists a second copy of settings.
    public static class Values
    {
        public static LanguageChoice Language {get=>Enum.Parse<LanguageChoice>(ConfigStore.EditableLocal.Language);set=>ConfigStore.UpdateLocal(c=>c with {Language=value.ToString()});}
        public static int BossHealthPercent {get=>ConfigStore.EditableLocal.BossHealthPercent;set=>ConfigStore.UpdateLocal(c=>c with {BossHealthPercent=value});}
        public static bool EnableMapRollback {get=>ConfigStore.EditableRules.EnableMapRollback;set=>ConfigStore.ChangeRules(r=>r with {EnableMapRollback=value});}
        public static bool EnableAncientReroll {get=>ConfigStore.EditableRules.EnableAncientReroll;set=>ConfigStore.ChangeRules(r=>r with {EnableAncientReroll=value});}
        public static bool EnableAncientOptionsReroll {get=>ConfigStore.EditableRules.EnableAncientOptionsReroll;set=>ConfigStore.ChangeRules(r=>r with {EnableAncientOptionsReroll=value});}
        public static bool EnableCombatRestart {get=>ConfigStore.EditableRules.EnableCombatRestart;set=>ConfigStore.ChangeRules(r=>r with {EnableCombatRestart=value});}
        public static bool EnableCombatReroll {get=>ConfigStore.EditableRules.EnableCombatReroll;set=>ConfigStore.ChangeRules(r=>r with {EnableCombatReroll=value});}
        public static bool EnableShopReroll {get=>ConfigStore.EditableRules.EnableShopReroll;set=>ConfigStore.ChangeRules(r=>r with {EnableShopReroll=value});}
        public static bool EnableBossHealthIncrease {get=>ConfigStore.EditableRules.EnableBossHealthIncrease;set=>ConfigStore.ChangeRules(r=>r with {EnableBossHealthIncrease=value});}
        public static RefreshCostMode AncientCostMode {get=>ConfigStore.EditableRules.AncientCostMode;set=>ConfigStore.ChangeRules(r=>r with {AncientCostMode=value});}
        public static int AncientHpCost {get=>ConfigStore.EditableRules.AncientHpCost;set=>ConfigStore.ChangeRules(r=>r with {AncientHpCost=value});}
        public static RefreshCostMode OptionsCostMode {get=>ConfigStore.EditableRules.OptionsCostMode;set=>ConfigStore.ChangeRules(r=>r with {OptionsCostMode=value});}
        public static int OptionsHpCost {get=>ConfigStore.EditableRules.OptionsHpCost;set=>ConfigStore.ChangeRules(r=>r with {OptionsHpCost=value});}
    }
}
