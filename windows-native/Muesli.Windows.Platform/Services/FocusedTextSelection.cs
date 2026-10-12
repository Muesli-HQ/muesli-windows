using System.Runtime.InteropServices;
using System.Text;
using FlaUI.UIA3;

namespace Muesli.Windows.Services;

/// <summary>
/// Quill's view of the text the user highlighted. UI Automation first, because it reads the
/// selection without touching the clipboard; apps that expose no text pattern (many Electron apps)
/// fall back to a copy that the caller wraps in a clipboard save and restore.
/// </summary>
public static class FocusedTextSelection
{
    /// <summary>
    /// The focused control's selected text: "" when it has none, null when the control does not
    /// expose its text. Throws <see cref="InvalidOperationException"/> for password fields.
    /// Call from a background (MTA) thread.
    /// </summary>
    public static string? ReadSelection()
    {
        using var automation = new UIA3Automation();
        var focused = automation.FocusedElement();
        if (focused is null) return null;
        if (focused.Properties.IsPassword.ValueOrDefault)
            throw new InvalidOperationException("Quill does not read or rewrite password fields.");
        if (!focused.Patterns.Text.TryGetPattern(out var text)) return null;
        var selection = text.GetSelection();
        return selection.Length == 0 ? "" : selection[0].GetText(-1) ?? "";
    }

    /// <summary>
    /// macOS DictationContextCapture for the cleanup prompt: the foreground app, its window title,
    /// up to <paramref name="maxBefore"/> characters before the cursor and the selected text.
    /// Password fields contribute nothing but the app name. Call from a background (MTA) thread.
    /// </summary>
    public static string? ReadAppContext(IntPtr window, int maxBefore = 200)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out var processId);
        string app;
        try { app = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName; }
        catch (ArgumentException) { return null; }
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        var context = new StringBuilder($"App: {app}");
        if (title.Length > 0) context.Append($" ({title})");
        try
        {
            using var automation = new UIA3Automation();
            var focused = automation.FocusedElement();
            if (focused is null || focused.Properties.IsPassword.ValueOrDefault ||
                !focused.Patterns.Text.TryGetPattern(out var text)) return context.ToString();
            var selection = text.GetSelection();
            if (selection.Length == 0) return context.ToString();
            var selected = selection[0].GetText(2000) ?? "";
            var before = selection[0].Clone();
            before.MoveEndpointByRange(FlaUI.Core.Definitions.TextPatternRangeEndpoint.End, selection[0], FlaUI.Core.Definitions.TextPatternRangeEndpoint.Start);
            before.MoveEndpointByUnit(FlaUI.Core.Definitions.TextPatternRangeEndpoint.Start, FlaUI.Core.Definitions.TextUnit.Character, -maxBefore);
            var preceding = before.GetText(maxBefore) ?? "";
            if (preceding.Trim().Length > 0) context.Append($"\nDocument context: {preceding.Trim()}");
            if (selected.Trim().Length > 0) context.Append($"\nSelected text: {selected.Trim()}");
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            // Some providers fail on range moves; the app name and title are still useful.
        }
        return context.ToString();
    }

    /// <summary>
    /// Consoles and Windows Terminal treat Ctrl+C as "interrupt" when nothing is selected, so the
    /// copy fallback must never be sent to them.
    /// </summary>
    public static bool IsTerminal(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "VirtualConsoleClass";
    }

    public static bool SendCopy() => WindowsInputInterop.SendCopy();

    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) for Ctrl, Shift, Alt and Win to be released, so a
    /// synthetic Ctrl+C or Ctrl+V is not combined with keys still held from the Quill shortcut.
    /// </summary>
    public static async Task WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        int[] modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
        while (modifiers.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0) && DateTime.UtcNow < deadline)
            await Task.Delay(20, cancellationToken);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
