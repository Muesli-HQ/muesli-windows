using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Muesli.Windows.Services;

/// <summary>
/// Reads the text of the box a dictation was pasted into, through UI Automation, so the correction
/// monitor can compare it with what was pasted. The Windows counterpart of the macOS monitor's
/// Accessibility reads (<c>kAXFocusedUIElementAttribute</c> then <c>kAXValueAttribute</c>).
/// </summary>
/// <remarks>
/// Only the focused element and its nearest ancestors are read, and only while they belong to
/// the process that received the paste. Password fields are never read. Apps that expose no text
/// through UI Automation (some terminals and custom-drawn editors) give nothing, and so never
/// prompt. One instance holds one UI Automation client for a monitoring session; it is not
/// thread-safe and must be disposed.
/// </remarks>
public sealed class FocusedTextReader : IDisposable
{
    private const int MaxAncestors = 3;
    private readonly UIA3Automation _automation = new();

    /// <summary>
    /// Text snapshots (unnormalized) from the focused element in <paramref name="targetWindow"/>'s
    /// process, each at most <paramref name="maxCharacters"/> long. Empty when focus moved to
    /// another app or nothing readable is focused.
    /// </summary>
    public IReadOnlyList<string> ReadSnapshots(IntPtr targetWindow, int maxCharacters)
    {
        if (targetWindow == IntPtr.Zero) return [];
        _ = GetWindowThreadProcessId(targetWindow, out var targetProcessId);
        if (targetProcessId == 0) return [];

        var snapshots = new List<string>();
        try
        {
            var element = _automation.FocusedElement();
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            for (var depth = 0; element is not null && depth <= MaxAncestors; depth++)
            {
                if (element.Properties.ProcessId.ValueOrDefault != targetProcessId) break;
                if (element.Properties.IsPassword.ValueOrDefault) break;
                if (ReadText(element, maxCharacters) is { } text && !snapshots.Contains(text, StringComparer.Ordinal))
                {
                    snapshots.Add(text);
                }
                element = walker.GetParent(element);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or TimeoutException or
                                          FlaUI.Core.Exceptions.PropertyNotSupportedException or
                                          FlaUI.Core.Exceptions.PatternNotSupportedException or
                                          UnauthorizedAccessException or ArgumentException)
        {
            // Focus moved mid-read, the element went away, or the app refuses UI Automation (for
            // example an elevated window). This poll simply sees nothing.
        }

        return snapshots;
    }

    private static string? ReadText(AutomationElement element, int maxCharacters)
    {
        // Rich editors can report an empty value while their text pattern holds the document, so an
        // empty value falls through to the text pattern.
        if (element.Patterns.Value.TryGetPattern(out var value) && value.Value.TryGetValue(out var current) && !string.IsNullOrEmpty(current))
        {
            return current.Length <= maxCharacters ? current : null;
        }

        if (element.Patterns.Text.TryGetPattern(out var text))
        {
            // One character more than allowed tells an over-long document from one exactly at the limit.
            var content = text.DocumentRange.GetText(maxCharacters + 1);
            return content is not null && content.Length <= maxCharacters ? content : null;
        }

        return null;
    }

    public void Dispose() => _automation.Dispose();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
