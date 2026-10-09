using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace NoSuffering.Config;

public static class ConfigStore
{
    private const string ModId = "NoSuffering";
    private const string ConfigPath = "user://NoSuffering/config.json";

    private static LocalConfig _local = new();
    private static HostRules _rules = new();
    private static HostRules _singlePlayerRules = new();
    private static LocalConfig? _pendingLocal;
    private static HostRules? _pendingRules;
    private static HostRules? _pendingSinglePlayerRules;
    private static bool _operationInProgress;
    private static bool _rulesWritable = true;
    private static bool _isMultiplayer;
    private static bool _settingModConfigValue;
    private static bool _initialized;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configType;

    public static LocalConfig Local => _local.Language == "Auto"
        ? _local with { Language = ResolveGameLanguage() }
        : _local;
    public static HostRules Rules => _rules;
    public static LocalConfig EditableLocal => _pendingLocal ?? _local;
    public static HostRules EditableRules => _pendingRules ?? _rules;
    public static bool RulesWritable => _rulesWritable;
    public static event Action? Changed;

    public static bool OperationInProgress
    {
        get => _operationInProgress;
        set
        {
            if (_operationInProgress == value) return;
            _operationInProgress = value;
            if (!value) ApplyPending();
        }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        LoadLocal();

        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame += DetectModConfig;
    }

    /// <summary>Marks host rules editable only for the authoritative host.</summary>
    public static void SetRulesWritable(bool writable) => _rulesWritable = writable;

    /// <summary>Updates the saved solo rules and applies them immediately when outside multiplayer.</summary>
    public static void SetLocalRules(HostRules rules)
    {
        var normalized = rules.Normalize();
        if (_operationInProgress) _pendingSinglePlayerRules = normalized;
        else { _singlePlayerRules = normalized; SaveLocal(); }
        if (!_isMultiplayer) UpdateRules(_ => normalized);
    }

    /// <summary>Applies the authoritative rules received from the host.</summary>
    public static void ApplyHostRules(HostRules rules) => UpdateRules(_ => rules.Normalize());

    /// <summary>Tracks session mode so multiplayer rule edits never replace solo preferences.</summary>
    public static void SetMultiplayer(bool multiplayer) => _isMultiplayer = multiplayer;

    /// <summary>Restores the saved single-player rules after leaving a multiplayer session.</summary>
    public static void RestoreLocalRules()
    {
        _isMultiplayer = false;
        _rulesWritable = true;
        UpdateRules(_ => _pendingSinglePlayerRules ?? _singlePlayerRules);
    }

    private static string ResolveGameLanguage()
    {
        var language = LocManager.Instance?.Language;
        if (string.IsNullOrWhiteSpace(language)) language = TranslationServer.GetLocale();
        return language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || language.Equals("zhs", StringComparison.OrdinalIgnoreCase)
            ? "简体中文"
            : "English";
    }

    private static void DetectModConfig()
    {
        if (Engine.GetMainLoop() is SceneTree tree) tree.ProcessFrame -= DetectModConfig;
        try
        {
            var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes).ToArray();
            _apiType = types.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType = types.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configType = types.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            if (_apiType == null || _entryType == null || _configType == null) return;

            RegisterModConfig();
            _registered = true;
            SyncModConfig();
        }
        catch (Exception e)
        {
            _apiType = _entryType = _configType = null;
            GD.PrintErr($"[NoSuffering] ModConfig integration failed: {e}");
        }
    }

    private static void RegisterModConfig()
    {
        var entries = new List<object>();
        AddEntry(entries, "TogglePanelKey", "Panel shortcut", "KeyBind", _local.TogglePanelKey.ToEncodedKey(),
            v => UpdateLocal(c => c with { TogglePanelKey = HotkeyBinding.FromEncodedKey(ToLong(v)) }));
        AddEntry(entries, "Language", "Language", "Dropdown", _local.Language,
            v => UpdateLocal(c => c with { Language = NormalizeLanguage(Convert.ToString(v)) }), options: new[] { "Auto", "English", "简体中文" });

        AddEntry(entries, "EnableMapRollback", "Map rollback", "Toggle", _rules.EnableMapRollback, v => ChangeRules(r => r with { EnableMapRollback = Convert.ToBoolean(v) }));
        AddEntry(entries, "EnableAncientReroll", "Ancient reroll", "Toggle", _rules.EnableAncientReroll, v => ChangeRules(r => r with { EnableAncientReroll = Convert.ToBoolean(v) }));
        AddEntry(entries, "EnableAncientOptionsReroll", "Ancient reward reroll", "Toggle", _rules.EnableAncientOptionsReroll, v => ChangeRules(r => r with { EnableAncientOptionsReroll = Convert.ToBoolean(v) }));
        AddEntry(entries, "EnableCombatRestart", "Combat restart", "Toggle", _rules.EnableCombatRestart, v => ChangeRules(r => r with { EnableCombatRestart = Convert.ToBoolean(v) }));
        AddEntry(entries, "EnableCombatReroll", "Restart with a new deck order", "Toggle", _rules.EnableCombatReroll, v => ChangeRules(r => r with { EnableCombatReroll = Convert.ToBoolean(v) }));
        AddEntry(entries, "AncientCostMode", "Ancient reroll cost", "Dropdown", _rules.AncientCostMode.ToString(), v => ChangeRules(r => r with { AncientCostMode = ParseEnum(v, RefreshCostMode.Free) }), options: Enum.GetNames<RefreshCostMode>());
        AddEntry(entries, "AncientHpCost", "Ancient reroll HP", "Slider", (float)_rules.AncientHpCost, v => ChangeRules(r => r with { AncientHpCost = ToInt(v) }), 1, 99, 1);
        AddEntry(entries, "OptionsCostMode", "Reward reroll cost", "Dropdown", _rules.OptionsCostMode.ToString(), v => ChangeRules(r => r with { OptionsCostMode = ParseEnum(v, RefreshCostMode.Free) }), options: Enum.GetNames<RefreshCostMode>());
        AddEntry(entries, "OptionsHpCost", "Reward reroll HP", "Slider", (float)_rules.OptionsHpCost, v => ChangeRules(r => r with { OptionsHpCost = ToInt(v) }), 1, 99, 1);

        var entryArray = Array.CreateInstance(_entryType!, entries.Count);
        for (var i = 0; i < entries.Count; i++) entryArray.SetValue(entries[i], i);
        var method = _apiType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Register")
            .OrderByDescending(m => m.GetParameters().Length)
            .First();
        var args = method.GetParameters().Length == 4
            ? new object[] { ModId, "NoSuffering", new Dictionary<string, string> { ["en"] = "NoSuffering", ["zhs"] = "不吃苦" }, entryArray }
            : new object[] { ModId, "NoSuffering", entryArray };
        method.Invoke(null, args);
    }

    private static void AddEntry(List<object> entries, string key, string label, string type, object defaultValue,
        Action<object> onChanged, float min = 0, float max = 100, float step = 1, string[]? options = null)
    {
        var entry = Activator.CreateInstance(_entryType!)!;
        Set(entry, "Key", key);
        Set(entry, "Label", label);
        var chineseLabel = key switch
        {
            "TogglePanelKey" => "面板快捷键",
            "Language" => "语言",
            "EnableMapRollback" => "路线回滚",
            "EnableAncientReroll" => "刷新先古之民",
            "EnableAncientOptionsReroll" => "刷新先古之民奖励",
            "EnableCombatRestart" => "重新开始战斗",
            "EnableCombatReroll" => "重开并刷新牌序",
            "AncientCostMode" => "先古之民刷新费用",
            "AncientHpCost" => "先古之民刷新生命费用",
            "OptionsCostMode" => "奖励刷新费用",
            "OptionsHpCost" => "奖励刷新生命费用",
            _ => label
        };
        Set(entry, "Labels", new Dictionary<string, string> { ["en"] = label, ["zhs"] = chineseLabel });
        Set(entry, "Type", Enum.Parse(_configType!, type));
        Set(entry, "DefaultValue", defaultValue);
        Set(entry, "OnChanged", new Action<object>(value =>
        {
            if (_settingModConfigValue) return;
            if (!IsLocalKey(key) && !_rulesWritable)
            {
                RestoreModConfigValue(key);
                return;
            }
            onChanged(value);
        }));
        if (type == "Slider")
        {
            Set(entry, "Min", min);
            Set(entry, "Max", max);
            Set(entry, "Step", step);
            Set(entry, "Format", "F0");
        }
        if (options != null) Set(entry, "Options", options);
        entries.Add(entry);
    }

    public static void ChangeRules(Func<HostRules, HostRules> update)
    {
        if (_rulesWritable) UpdateRules(update, persistAsSinglePlayer: !_isMultiplayer);
    }

    public static void UpdateLocal(Func<LocalConfig, LocalConfig> update)
    {
        var current = _pendingLocal ?? _local;
        var next = update(current);
        if (_operationInProgress) _pendingLocal = next;
        else { _local = next; SaveLocal(); SyncModConfig(); Changed?.Invoke(); }
    }

    private static void UpdateRules(Func<HostRules, HostRules> update, bool persistAsSinglePlayer = false)
    {
        var current = _pendingRules ?? _rules;
        var next = update(current).Normalize();
        if (_operationInProgress)
        {
            _pendingRules = next;
            if (persistAsSinglePlayer) _pendingSinglePlayerRules = next;
        }
        else
        {
            _rules = next;
            if (persistAsSinglePlayer) { _singlePlayerRules = next; SaveLocal(); }
            SyncModConfig();
            Changed?.Invoke();
        }
    }

    private static void ApplyPending()
    {
        var changed = _pendingLocal != null || _pendingRules != null || _pendingSinglePlayerRules != null;
        if (_pendingLocal != null) { _local = _pendingLocal; _pendingLocal = null; SaveLocal(); }
        if (_pendingRules != null)
        {
            _rules = _pendingRules;
            _pendingRules = null;
        }
        if (_pendingSinglePlayerRules != null)
        {
            _singlePlayerRules = _pendingSinglePlayerRules;
            _pendingSinglePlayerRules = null;
            SaveLocal();
        }
        if (changed) { SyncModConfig(); Changed?.Invoke(); }
    }

    private static void LoadLocal()
    {
        try
        {
            var path = ProjectSettings.GlobalizePath(ConfigPath);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("Local", out _))
            {
                var saved = JsonSerializer.Deserialize<ConfigDocument>(json);
                if (saved != null)
                {
                    _local = saved.Local with
                    {
                        TogglePanelKey = saved.Local.TogglePanelKey ?? new HotkeyBinding(Key.F6),
                        Language = NormalizeLanguage(saved.Local.Language)
                    };
                    _singlePlayerRules = (saved.SinglePlayerRules ?? new HostRules()).Normalize();
                    _rules = _singlePlayerRules;
                }
                return;
            }

            var legacy = JsonSerializer.Deserialize<LocalConfig>(json);
            if (legacy == null) return;
            _local = legacy with
            {
                TogglePanelKey = legacy.TogglePanelKey ?? new HotkeyBinding(Key.F6),
                Language = NormalizeLanguage(legacy.Language)
            };
        }
        catch (Exception e) { GD.PrintErr($"[NoSuffering] Could not load local config: {e.Message}"); }
    }

    private static void SaveLocal()
    {
        try
        {
            var path = ProjectSettings.GlobalizePath(ConfigPath);
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var tempPath = path + ".tmp";
            var document = new ConfigDocument { Local = _local, SinglePlayerRules = _singlePlayerRules };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, path, true);
        }
        catch (Exception e) { GD.PrintErr($"[NoSuffering] Could not save local config: {e.Message}"); }
    }

    private static void RestoreModConfigValue(string key)
    {
        if (!_registered || _apiType == null) return;
        var value = key switch
        {
            "TogglePanelKey" => (object)_local.TogglePanelKey.ToEncodedKey(),
            "Language" => _local.Language,
            "EnableMapRollback" => _rules.EnableMapRollback,
            "EnableAncientReroll" => _rules.EnableAncientReroll,
            "EnableAncientOptionsReroll" => _rules.EnableAncientOptionsReroll,
            "EnableCombatRestart" => _rules.EnableCombatRestart,
            "EnableCombatReroll" => _rules.EnableCombatReroll,
            "AncientCostMode" => _rules.AncientCostMode.ToString(),
            "AncientHpCost" => (float)_rules.AncientHpCost,
            "OptionsCostMode" => _rules.OptionsCostMode.ToString(),
            "OptionsHpCost" => (float)_rules.OptionsHpCost,
            _ => null
        };
        if (value == null) return;
        try
        {
            _settingModConfigValue = true;
            _apiType.GetMethod("SetValue", BindingFlags.Public | BindingFlags.Static)!
                .Invoke(null, new[] { (object)ModId, key, value });
        }
        catch (Exception e) { GD.PrintErr($"[NoSuffering] Could not synchronize ModConfig: {e.Message}"); }
        finally { _settingModConfigValue = false; }
    }

    private static void SyncModConfig()
    {
        foreach (var key in new[] { "TogglePanelKey", "Language", "EnableMapRollback", "EnableAncientReroll", "EnableAncientOptionsReroll", "EnableCombatRestart", "EnableCombatReroll", "AncientCostMode", "AncientHpCost", "OptionsCostMode", "OptionsHpCost" })
            RestoreModConfigValue(key);
    }

    private static bool IsLocalKey(string key) => key is "TogglePanelKey" or "Language";
    private static void Set(object target, string property, object value) =>
        (_entryType!.GetProperty(property) ?? throw new MissingMemberException(_entryType.FullName, property)).SetValue(target, value);
    private static long ToLong(object? value) => Convert.ToInt64(value);
    private static int ToInt(object? value) => Convert.ToInt32(Math.Round(Convert.ToDouble(value)));
    private static string NormalizeLanguage(string? value) => value is "English" or "简体中文" ? value : "Auto";
    private static T ParseEnum<T>(object? value, T fallback) where T : struct, Enum =>
        Enum.TryParse(Convert.ToString(value), out T parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
        catch { return Type.EmptyTypes; }
    }

    private sealed class ConfigDocument
    {
        public LocalConfig Local { get; set; } = new();
        public HostRules SinglePlayerRules { get; set; } = new();
    }
}
