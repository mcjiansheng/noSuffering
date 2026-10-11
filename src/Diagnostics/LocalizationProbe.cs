using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using NoSuffering.Config;
using NoSuffering.UI;
using Bridge = NoSuffering.GameBridge.GameBridge;

namespace NoSuffering.Diagnostics;

// Called only behind GameplayProbe's isolated Steam-off lab gate. No run is started.
public static class LocalizationProbe
{
    public static async Task Run(SceneTree tree)
    {
        var results = new Dictionary<string, string> { ["game_language_and_settings"] = "UNEXECUTED" };
        var state = new Dictionary<string, object>();
        string? originalLanguage = null;
        try
        {
            var deadline = Time.GetTicksMsec() + 60000;
            while (NGame.Instance?.MainMenu?.IsNodeReady() != true)
            {
                Require(Time.GetTicksMsec() < deadline, "Native main menu did not become ready.");
                await Bridge.Frame();
            }
#if !STS2_STABLE
            await NGame.Instance!.GameStartupComplete;
#endif
            originalLanguage = LocManager.Instance.Language;
            var local = ConfigStore.Local;
            var rules = ConfigStore.Rules;
            var ownFile = ProjectSettings.GlobalizePath("user://NoSuffering/config.json");
            if (!File.Exists(ownFile)) ConfigStore.UpdateLocal(c => c);
            using (var before = JsonDocument.Parse(File.ReadAllText(ownFile)))
            {
                var saved = before.RootElement.TryGetProperty("Local", out var nested) ? nested : before.RootElement;
                if (saved.TryGetProperty("Language", out var legacy))
                    state["legacy_language_override"] = legacy.GetString()!;
            }
            using (var key = new InputEventKey { Pressed = true, Keycode = local.TogglePanelKey.Key, PhysicalKeycode = local.TogglePanelKey.Key,
                       CtrlPressed = local.TogglePanelKey.Ctrl, AltPressed = local.TogglePanelKey.Alt,
                       ShiftPressed = local.TogglePanelKey.Shift, MetaPressed = local.TogglePanelKey.Meta })
            {
                NGame.Instance!.GetViewport().PushInput(key, true);
                key.Pressed = false;
                NGame.Instance.GetViewport().PushInput(key, true);
            }
            await Bridge.Frame();
            var overlay = tree.Root.GetNode<CanvasLayer>("NoSufferingOverlay");
            var configBytes = File.ReadAllBytes(ownFile);
            foreach (var language in new[] { "eng", "zhs", "eng" })
            {
                LocManager.Instance.SetLanguage(language);
                await Bridge.Frame();
                var controls = Descendants(overlay).OfType<Control>().ToArray();
                var labels = controls.OfType<Label>().Select(label => label.Text).ToArray();
                Require(labels.Contains(language == "eng" ? "NoSuffering · Settings" : "不吃苦 · 设置"), "Open settings did not follow the game's language.");
                Require(labels.Contains(language == "eng" ? "Rollback route" : "路线回滚"), "Operation label did not follow game language.");
                Require(controls.OfType<CheckButton>().Count() == 7 && controls.OfType<OptionButton>().Count() == 2,
                    "Settings controls changed or the language dropdown is still present.");
                Require(!labels.Any(text => text is "Language" or "语言"), "Manual language row remains.");
                Require(ConfigStore.Local == local && ConfigStore.Rules == rules && File.ReadAllBytes(ownFile).SequenceEqual(configBytes),
                    "Switching game language changed saved preferences or host rules.");
            }
            Overlay.Close();
            ConfigStore.UpdateLocal(c => c);
            using (var saved = JsonDocument.Parse(File.ReadAllText(ownFile)))
                Require(!saved.RootElement.GetProperty("Local").TryGetProperty("Language", out _), "Obsolete override was saved again.");
            results["game_language_and_settings"] = "PASS: eng/zhs/eng through native LocManager; open standalone settings update; no manual row; legacy override ignored; other preferences unchanged";
            if (OS.GetCmdlineUserArgs().Contains("--ns-baselib-probe"))
            {
                results["optional_baselib_settings"] = "UNEXECUTED";
                state["baselib"] = JsonSerializer.Deserialize<JsonElement>(await BaseLibSettings.ProbeAsync());
                results["optional_baselib_settings"] = "PASS: native 13 rows, locale updates, callbacks and persistence";
            }
            if (FrameworkProbe.IsPresent)
            {
                results["optional_modconfig_settings"] = "UNEXECUTED";
                state["modconfig"] = await FrameworkProbe.Run(tree);
                results["optional_modconfig_settings"] = "PASS: actual registration and native UI contain 13 keys without Language; bidirectional values persisted";
            }
        }
        catch (Exception e) { results["error"] = "FAIL: " + e; }
        finally { Overlay.Close(); if (originalLanguage != null) LocManager.Instance.SetLanguage(originalLanguage); }
        var report = JsonSerializer.Serialize(new { results, state, scope = "Focused native settings checks; not a playthrough or multiplayer test" });
        Log.Info("[NoSuffering] LOCALIZATION_PROBE_RESULTS " + report);
        tree.Quit(results.Values.All(value => value.StartsWith("PASS")) ? 0 : 1);
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren()) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
