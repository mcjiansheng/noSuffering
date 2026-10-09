using Godot;

namespace NoSuffering.Config;

public sealed record LocalConfig
{
    public HotkeyBinding TogglePanelKey { get; init; } = new(Key.F6);
    public string Language { get; init; } = "Auto";
}

public sealed record HotkeyBinding
{
    public Key Key { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Shift { get; init; }
    public bool Meta { get; init; }

    public HotkeyBinding() { }

    public HotkeyBinding(Key key, bool ctrl = false, bool alt = false, bool shift = false, bool meta = false)
    {
        Key = key;
        Ctrl = ctrl;
        Alt = alt;
        Shift = shift;
        Meta = meta;
    }

    // ModConfig stores the native Godot key code and modifier flags in one long.
    internal long ToEncodedKey()
    {
        using var input = new InputEventKey
        {
            Keycode = Key, CtrlPressed = Ctrl, AltPressed = Alt, ShiftPressed = Shift, MetaPressed = Meta
        };
        return (long)input.GetKeycodeWithModifiers();
    }

    internal static HotkeyBinding FromEncodedKey(long encoded) => new(
        (Key)(encoded & (long)KeyModifierMask.CodeMask),
        (encoded & (long)KeyModifierMask.MaskCtrl) != 0,
        (encoded & (long)KeyModifierMask.MaskAlt) != 0,
        (encoded & (long)KeyModifierMask.MaskShift) != 0,
        (encoded & (long)KeyModifierMask.MaskMeta) != 0);

    public bool Matches(InputEventKey input) =>
        input.Pressed && !input.Echo && input.Keycode == Key &&
        input.CtrlPressed == Ctrl && input.AltPressed == Alt &&
        input.ShiftPressed == Shift && input.MetaPressed == Meta;
}
