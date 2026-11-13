using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Turns key presses into a hotkey gesture string, as the macOS recorder does. A key with any
/// modifiers records on key-down ("Ctrl+Shift+Space"); a right-side modifier pressed and released
/// on its own records on release ("Right Ctrl"). Shared by the Shortcuts page and setup.
/// </summary>
internal sealed class HotkeyRecorder
{
    private string? _pendingModifier;

    public void Reset() => _pendingModifier = null;

    /// <returns>The recorded gesture, or null while the user is still pressing keys.</returns>
    public string? KeyDown(KeyRoutedEventArgs e)
    {
        if (IsModifier(e.Key))
        {
            // A second modifier makes it a combination; a left-side one is never a lone hold key.
            _pendingModifier = _pendingModifier is null && OnlyThisModifierDown(e) ? RightSideLabel(e) : null;
            return null;
        }

        _pendingModifier = null;
        var label = KeyLabel(e.Key);
        if (label is null) return null;
        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(label);
        return string.Join('+', parts);
    }

    /// <returns>"Right Ctrl" etc. when a right-side modifier was pressed and released alone.</returns>
    public string? KeyUp(KeyRoutedEventArgs e)
    {
        if (_pendingModifier is null || !IsModifier(e.Key)) return null;
        var released = RightSideLabel(e);
        var recorded = released == _pendingModifier ? _pendingModifier : null;
        _pendingModifier = null;
        return recorded;
    }

    private static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>
    /// XAML reports Ctrl, Alt and Shift without a side, so the scan code decides: Right Ctrl and
    /// Right Alt are extended keys, Right Shift has scan code 0x36.
    /// </summary>
    private static string? RightSideLabel(KeyRoutedEventArgs e) => e.Key switch
    {
        VirtualKey.RightControl => "Right Ctrl",
        VirtualKey.RightMenu => "Right Alt",
        VirtualKey.RightShift => "Right Shift",
        VirtualKey.RightWindows => "Right Win",
        VirtualKey.Control or VirtualKey.LeftControl when e.KeyStatus.IsExtendedKey => "Right Ctrl",
        VirtualKey.Menu or VirtualKey.LeftMenu when e.KeyStatus.IsExtendedKey => "Right Alt",
        VirtualKey.Shift or VirtualKey.LeftShift when e.KeyStatus.ScanCode == 0x36 => "Right Shift",
        _ => null
    };

    private static bool OnlyThisModifierDown(KeyRoutedEventArgs e)
    {
        var label = RightSideLabel(e);
        if (label is null) return false;
        // AltGr layouts send Ctrl together with Right Alt; that Ctrl belongs to the key.
        var ctrlAllowed = label is "Right Ctrl" or "Right Alt";
        return (ctrlAllowed || !IsDown(VirtualKey.Control)) &&
               (label == "Right Shift" || !IsDown(VirtualKey.Shift)) &&
               (label == "Right Alt" || !IsDown(VirtualKey.Menu)) &&
               (label == "Right Win" || (!IsDown(VirtualKey.LeftWindows) && !IsDown(VirtualKey.RightWindows)));
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    internal static string? KeyLabel(VirtualKey key)
    {
        var value = (int)key;
        if (value is >= (int)VirtualKey.A and <= (int)VirtualKey.Z ||
            value is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return ((char)value).ToString();
        }
        if (value is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return $"F{value - (int)VirtualKey.F1 + 1}";
        }
        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "PageUp",
            VirtualKey.PageDown => "PageDown",
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Left => "Left",
            VirtualKey.Right => "Right",
            _ => null
        };
    }
}
