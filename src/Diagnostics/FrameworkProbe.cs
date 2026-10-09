using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using NoSuffering.Config;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Optional actual-framework fixture, invoked only through the gated lab probe.
// All integration calls reflect the loaded official source-built assembly.
public static class FrameworkProbe
{
    private static Assembly? Framework => AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "ModConfig");
    public static bool IsPresent => Framework != null;

    public static async Task<object> Run(SceneTree tree)
    {
        var assembly = Framework ?? throw new InvalidOperationException("Actual ModConfig assembly missing.");
        var api = assembly.GetType("ModConfig.ModConfigApi", true)!;
        var manager = assembly.GetType("ModConfig.ModConfigManager", true)!;
        var registrations = (IDictionary)manager.GetField("_registrations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Require(registrations.Contains("NoSuffering"), "NoSuffering was not registered with actual ModConfig.");
        var registration = registrations["NoSuffering"]!;
        var entries = (Array)registration.GetType().GetProperty("Entries")!.GetValue(registration)!;
        string[] keys = ["TogglePanelKey", "Language", "EnableMapRollback", "EnableAncientReroll",
            "EnableAncientOptionsReroll", "EnableCombatRestart", "EnableCombatReroll", "EnableShopReroll",
            "EnableBossHealthIncrease", "BossHealthPercent", "AncientCostMode", "AncientHpCost", "OptionsCostMode", "OptionsHpCost"];
        var actualKeys = entries.Cast<object>().Select(e => (string)e.GetType().GetProperty("Key")!.GetValue(e)!).ToArray();
        Require(actualKeys.Order().SequenceEqual(keys.Order()), "Actual framework registration does not contain the declared 14 keys.");
        T Get<T>(string key) => (T)api.GetMethod("GetValue")!.MakeGenericMethod(typeof(T)).Invoke(null, ["NoSuffering", key])!;
        void Set(string key, object value) => api.GetMethod("SetValue")!.Invoke(null, ["NoSuffering", key, value]);
        var local = ConfigStore.EditableLocal;
        var rules = ConfigStore.Rules;
        bool writable = ConfigStore.RulesWritable;
        var ownFile = Path.Combine(OS.GetUserDataDir(), "NoSuffering", "config.json");
        var frameworkFile = Path.Combine(OS.GetUserDataDir(), "ModConfig", "NoSuffering.json");
        var ownBytes = File.Exists(ownFile) ? File.ReadAllBytes(ownFile) : null;
        var frameworkBytes = File.Exists(frameworkFile) ? File.ReadAllBytes(frameworkFile) : null;
        try
        {
            ConfigStore.SetRulesWritable(true);
            ConfigStore.ChangeRules(r => r with { EnableShopReroll = false });
            var outboundKey = new HotkeyBinding(Key.F8, ctrl: true);
            ConfigStore.UpdateLocal(c => c with { BossHealthPercent = 63, Language = "English", TogglePanelKey = outboundKey });
            Require(!Get<bool>("EnableShopReroll") && Get<int>("BossHealthPercent") == 63 &&
                Get<string>("Language") == "English" && Get<long>("TogglePanelKey") == outboundKey.ToEncodedKey(),
                "NoSuffering-to-framework values diverged.");

            var inboundKey = new HotkeyBinding(Key.F9, shift: true);
            Set("EnableShopReroll", true);
            Set("BossHealthPercent", 71f);
            Set("Language", "简体中文");
            Set("TogglePanelKey", inboundKey.ToEncodedKey());
            Require(ConfigStore.Rules.EnableShopReroll && ConfigStore.Local.BossHealthPercent == 71 &&
                ConfigStore.EditableLocal.Language == "简体中文" && ConfigStore.Local.TogglePanelKey == inboundKey,
                "Framework callbacks failed to update NoSuffering.");
            await Bridge.Frame();
            await Bridge.Frame();
            using (var saved = JsonDocument.Parse(File.ReadAllText(ownFile)))
            {
                var savedLocal = saved.RootElement.GetProperty("Local");
                Require(savedLocal.GetProperty("BossHealthPercent").GetInt32() == 71 &&
                    savedLocal.GetProperty("Language").GetString() == "简体中文" &&
                    savedLocal.GetProperty("TogglePanelKey").GetProperty("Shift").GetBoolean(),
                    "NoSuffering config persistence differs from callbacks.");
            }
            using (var saved = JsonDocument.Parse(File.ReadAllText(frameworkFile)))
                Require(saved.RootElement.GetProperty("BossHealthPercent").GetInt32() == 71 &&
                    saved.RootElement.GetProperty("Language").GetString() == "简体中文" &&
                    saved.RootElement.GetProperty("TogglePanelKey").GetInt64() == inboundKey.ToEncodedKey(),
                    "Actual framework persistence differs from callbacks.");

            NGame.Instance!.MainMenu!.OpenSettingsMenu();
            var deadline = Time.GetTicksMsec() + 10000;
            Node? entriesNode;
            while ((entriesNode = Descendants(tree.Root).FirstOrDefault(n => n.Name == "Entries_NoSuffering")) == null)
            {
                Require(Time.GetTicksMsec() < deadline, "Actual framework failed to populate native Settings Mods UI.");
                await Bridge.Frame();
            }
            var tab = Descendants(tree.Root).OfType<NSettingsTab>().Single(t => t.Name == "Mods");
            // Native ForceTabPressed emits the tab's Released signal, reaching
            // the framework's actual tab-switch callback.
            tab.ForceTabPressed();
            await Bridge.Frame();
            var controls = Descendants(entriesNode).ToArray();
            Require(controls.OfType<CheckButton>().Count() == 7 && controls.OfType<HSlider>().Count() == 3 &&
                controls.OfType<OptionButton>().Count() == 3 && controls.OfType<Button>().Any(b => b is not CheckButton && b is not OptionButton),
                "Actual Mods UI did not construct all registered controls.");
            Require(entriesNode is Control box && box.IsVisibleInTree(), "Actual Mods UI entries are not visible after native tab selection.");
            return new { framework_assembly = assembly.GetName().FullName,
                framework_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                entry_count = entries.Length, keys = actualKeys, toggle_controls = 7, slider_controls = 3,
                dropdown_controls = 3, bidirectional_values = "PASS", both_config_files = "PASS",
                ui_scope = "actual native Settings Mods tab, engine-created controls and synthetic native release; no human pointer or visual-quality acceptance" };
        }
        finally
        {
            ConfigStore.UpdateLocal(_ => local);
            ConfigStore.SetLocalRules(rules);
            ConfigStore.SetRulesWritable(writable);
            await Bridge.Frame();
            await Bridge.Frame();
            RestoreBytes(ownFile, ownBytes);
            RestoreBytes(frameworkFile, frameworkBytes);
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void RestoreBytes(string path, byte[]? bytes)
    {
        if (bytes == null) File.Delete(path);
        else File.WriteAllBytes(path, bytes);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
