using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Muesli.Windows.Core.Contracts;
using Windows.System;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Feeds WinUI key events to <see cref="HotkeyRecorderCore"/>. Shared by the Shortcuts page and
/// setup; the recording rules live in the core.
/// </summary>
internal sealed class HotkeyRecorder
{
    private readonly HotkeyRecorderCore _core = new();

    public void Reset() => _core.Reset();

    /// <returns>The recorded gesture, or null while the user is still pressing keys.</returns>
    public string? KeyDown(KeyRoutedEventArgs e) => _core.KeyDown(ToKey(e), Held());

    /// <returns>"Right Ctrl" etc. when a right-side modifier was pressed and released alone.</returns>
    public string? KeyUp(KeyRoutedEventArgs e) => _core.KeyUp(ToKey(e));

    private static RecorderKey ToKey(KeyRoutedEventArgs e) =>
        new((int)e.Key, e.KeyStatus.IsExtendedKey, e.KeyStatus.ScanCode, e.KeyStatus.WasKeyDown);

    private static HeldModifiers Held() => new(
        IsDown(VirtualKey.Control),
        IsDown(VirtualKey.Shift),
        IsDown(VirtualKey.Menu),
        IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows));

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
}
