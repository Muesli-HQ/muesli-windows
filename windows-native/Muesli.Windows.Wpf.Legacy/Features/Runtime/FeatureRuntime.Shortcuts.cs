using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
    private void CaptureHotkey_Click(object sender, RoutedEventArgs e)
    {
        _isCapturingHotkey = true;
        OnPropertyChanged(nameof(CaptureHotkeyButtonText));
        OnPropertyChanged(nameof(ShortcutCaptureLabel));
        DictationStatus = "Press a new dictation shortcut";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            if (e.Key == Key.Escape && !string.IsNullOrWhiteSpace(SearchQuery))
            {
                SearchQuery = "";
                e.Handled = true;
            }
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            StopHotkeyCapture("Shortcut capture cancelled");
            return;
        }

        var label = BuildCapturedHotkeyLabel(e);
        if (label is null)
        {
            DictationStatus = "Press a function key or include Ctrl, Alt, Shift, or Win";
            return;
        }

        AddHotkeyOptionIfMissing(label);
        SelectedHotkey = label;
        StopHotkeyCapture($"Shortcut set to {label}");
    }

    private void StopHotkeyCapture(string status)
    {
        _isCapturingHotkey = false;
        DictationStatus = status;
        OnPropertyChanged(nameof(CaptureHotkeyButtonText));
        OnPropertyChanged(nameof(ShortcutCaptureLabel));
    }

    private static string? BuildCapturedHotkeyLabel(WpfKeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return null;
        }

        var modifiers = Keyboard.Modifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key is not (>= Key.F1 and <= Key.F24) && parts.Count == 0) return null;

        parts.Add(key == Key.Space ? "Space" : key.ToString());
        return string.Join("+", parts);
    }

    private void AddHotkeyOptionIfMissing(string hotkey)
    {
        if (!HotkeyOptions.Any(option => option.Equals(hotkey, StringComparison.OrdinalIgnoreCase)))
        {
            HotkeyOptions.Add(hotkey);
        }
    }
}
