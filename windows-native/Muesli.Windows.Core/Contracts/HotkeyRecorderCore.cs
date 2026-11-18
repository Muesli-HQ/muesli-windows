namespace Muesli.Windows.Core.Contracts;

/// <summary>One key event as the shortcut recorder sees it (Windows virtual-key codes).</summary>
/// <param name="IsExtended">Right Ctrl and Right Alt are extended keys; their left twins are not.</param>
/// <param name="ScanCode">Tells Right Shift (0x36) from Left Shift.</param>
/// <param name="IsRepeat">Auto-repeat while the key is held.</param>
public readonly record struct RecorderKey(int VirtualKey, bool IsExtended = false, uint ScanCode = 0, bool IsRepeat = false);

/// <summary>Which modifiers are physically down at the moment of the event.</summary>
public readonly record struct HeldModifiers(bool Ctrl = false, bool Shift = false, bool Alt = false, bool Win = false);

/// <summary>
/// Turns key presses into a hotkey gesture string, as the macOS recorder does. A key with any
/// modifiers records on key-down ("Ctrl+Shift+Space"); a right-side modifier pressed and released
/// on its own records on release ("Right Ctrl"). Framework-free so it can be tested; the WinUI
/// recorder feeds it from key events.
/// </summary>
public sealed class HotkeyRecorderCore
{
    private const int Control = 0x11, LeftControl = 0xA2, RightControl = 0xA3;
    private const int Shift = 0x10, LeftShift = 0xA0, RightShift = 0xA1;
    private const int Menu = 0x12, LeftMenu = 0xA4, RightMenu = 0xA5;
    private const int LeftWindows = 0x5B, RightWindows = 0x5C;
    private const uint RightShiftScanCode = 0x36;

    private string? _pending;

    public void Reset() => _pending = null;

    /// <returns>The recorded gesture, or null while the user is still pressing keys.</returns>
    public string? KeyDown(RecorderKey key, HeldModifiers held)
    {
        // Holding a key repeats its key-down; only the first press means anything. Without this a
        // right-side modifier held past the typematic delay was never recorded.
        if (key.IsRepeat) return null;

        if (IsModifier(key.VirtualKey))
        {
            // AltGr layouts send a Left Ctrl along with Right Alt; it is part of that key.
            if (_pending == "Right Alt" && IsLeftCtrl(key)) return null;
            // A second modifier makes it a combination; a left-side one is never a lone hold key.
            _pending = _pending is null && OnlyThisModifierDown(key, held) ? RightSideLabel(key) : null;
            return null;
        }

        _pending = null;
        var label = KeyLabel(key.VirtualKey);
        if (label is null) return null;
        var parts = new List<string>();
        if (held.Ctrl) parts.Add("Ctrl");
        if (held.Shift) parts.Add("Shift");
        if (held.Alt) parts.Add("Alt");
        if (held.Win) parts.Add("Win");
        parts.Add(label);
        return string.Join('+', parts);
    }

    /// <returns>"Right Ctrl" etc. when a right-side modifier was pressed and released alone.</returns>
    public string? KeyUp(RecorderKey key)
    {
        if (_pending is null || !IsModifier(key.VirtualKey)) return null;
        // AltGr's Left Ctrl is released with Right Alt; it must not cancel the recording.
        if (_pending == "Right Alt" && IsLeftCtrl(key)) return null;
        var recorded = RightSideLabel(key) == _pending ? _pending : null;
        _pending = null;
        return recorded;
    }

    private static bool IsModifier(int key) => key is
        Control or LeftControl or RightControl or
        Shift or LeftShift or RightShift or
        Menu or LeftMenu or RightMenu or
        LeftWindows or RightWindows;

    private static bool IsLeftCtrl(RecorderKey key) =>
        key.VirtualKey is Control or LeftControl && !key.IsExtended;

    /// <summary>
    /// Window key events report Ctrl, Alt and Shift without a side, so the scan code decides:
    /// Right Ctrl and Right Alt are extended keys, Right Shift has scan code 0x36.
    /// </summary>
    private static string? RightSideLabel(RecorderKey key) => key.VirtualKey switch
    {
        RightControl => "Right Ctrl",
        RightMenu => "Right Alt",
        RightShift => "Right Shift",
        RightWindows => "Right Win",
        Control or LeftControl when key.IsExtended => "Right Ctrl",
        Menu or LeftMenu when key.IsExtended => "Right Alt",
        Shift or LeftShift when key.ScanCode == RightShiftScanCode => "Right Shift",
        _ => null
    };

    private static bool OnlyThisModifierDown(RecorderKey key, HeldModifiers held)
    {
        var label = RightSideLabel(key);
        if (label is null) return false;
        // AltGr layouts hold Ctrl together with Right Alt; that Ctrl belongs to the key.
        var ctrlAllowed = label is "Right Ctrl" or "Right Alt";
        return (ctrlAllowed || !held.Ctrl) &&
               (label == "Right Shift" || !held.Shift) &&
               (label == "Right Alt" || !held.Alt) &&
               (label == "Right Win" || !held.Win);
    }

    public static string? KeyLabel(int key)
    {
        if (key is >= 0x41 and <= 0x5A || key is >= 0x30 and <= 0x39) return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87) return $"F{key - 0x70 + 1}";
        return key switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            _ => null
        };
    }
}
