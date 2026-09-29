using System.Diagnostics;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

/// <summary>
/// Coordinates safe delivery of a transcript to the application that was active when
/// dictation began. The algorithm is UI-framework independent; shells supply clipboard,
/// focus, and keyboard adapters.
/// </summary>
public sealed class ActiveAppPasteService
{
    private readonly IClipboardAdapter _clipboard;
    private readonly IWindowActivationAdapter _windows;
    private readonly IKeyboardInputAdapter _keyboard;
    private readonly IAsyncDelay _delay;

    public ActiveAppPasteService(
        IClipboardAdapter clipboard,
        IWindowActivationAdapter windows,
        IKeyboardInputAdapter keyboard,
        IAsyncDelay? delay = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _delay = delay ?? new SystemAsyncDelay();
    }

    // Compatibility diagnostic retained for the qualification suite. The platform adapter
    // performs the actual ABI marshalling; this documents the expected INPUT sizes.
    internal static int NativeInputStructureSize => IntPtr.Size == 8 ? 40 : 28;

    public IntPtr CaptureForegroundWindow() => _windows.ForegroundWindow;

    public PasteTargetInfo DescribeWindow(IntPtr targetWindow) =>
        _windows is IWindowDescriptionAdapter description
            ? description.DescribeWindow(targetWindow)
            : PasteTargetInfo.Unknown;

    public async Task<long> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var started = Stopwatch.StartNew();
        await _clipboard.SetTextAsync(text, cancellationToken);
        started.Stop();
        return started.ElapsedMilliseconds;
    }

    public async Task<PasteOperationResult> PasteTextAsync(
        string text,
        IntPtr targetWindow,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PasteOperationResult(0, 0, 0, 0, false);
        }

        var totalStarted = Stopwatch.StartNew();
        var focusStarted = Stopwatch.StartNew();
        if (!_windows.Exists(targetWindow))
        {
            throw new InvalidOperationException(
                "The app that was active when dictation started is no longer available. The transcript was saved in Muesli history and the clipboard was left unchanged.");
        }

        _windows.RequestForeground(targetWindow);
        var targetWasForeground = await WaitForTargetForegroundAsync(targetWindow, cancellationToken);
        focusStarted.Stop();
        if (!targetWasForeground)
        {
            throw new InvalidOperationException(
                "Windows could not restore focus to the original app. The transcript was saved in Muesli history and the clipboard was left unchanged.");
        }

        var inputStarted = Stopwatch.StartNew();
        var directInput = _keyboard.SendText(text);
        inputStarted.Stop();
        if (directInput.Succeeded)
        {
            totalStarted.Stop();
            return new PasteOperationResult(
                totalStarted.ElapsedMilliseconds,
                0,
                focusStarted.ElapsedMilliseconds,
                inputStarted.ElapsedMilliseconds,
                targetWasForeground);
        }

        if (directInput.MayHaveInsertedText)
        {
            throw new InvalidOperationException(
                "Windows only accepted part of the transcript input. The transcript was saved in Muesli history and the clipboard was left unchanged.");
        }

        // Unicode input is the normal path because it never touches the user's clipboard. If
        // Windows rejects it before inserting anything, use Ctrl+V and restore safely.
        return await PasteThroughClipboardAsync(
            text,
            totalStarted,
            focusStarted.ElapsedMilliseconds,
            cancellationToken);
    }

    public static bool ShouldRestoreClipboard(string expectedMuesliText, string? currentText) =>
        string.Equals(expectedMuesliText, currentText, StringComparison.Ordinal);

    private async Task<bool> WaitForTargetForegroundAsync(IntPtr targetWindow, CancellationToken cancellationToken)
    {
        if (!_windows.Exists(targetWindow))
        {
            return false;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!_windows.Exists(targetWindow))
            {
                return false;
            }

            if (_windows.ForegroundWindow == targetWindow)
            {
                return true;
            }

            await _delay.DelayAsync(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        return _windows.ForegroundWindow == targetWindow;
    }

    private async Task<PasteOperationResult> PasteThroughClipboardAsync(
        string text,
        Stopwatch totalStarted,
        long focusWaitMs,
        CancellationToken cancellationToken)
    {
        var clipboardStarted = Stopwatch.StartNew();
        ClipboardSnapshot? previousClipboard = null;
        var clipboardWasSet = false;
        try
        {
            previousClipboard = await _clipboard.CaptureAsync(cancellationToken);
            // Treat the clipboard as potentially modified for the whole SetText call. An
            // adapter can fail after handing the data to the OS, so always try to restore.
            clipboardWasSet = true;
            await _clipboard.SetTextAsync(text, cancellationToken);
            clipboardStarted.Stop();

            var inputStarted = Stopwatch.StartNew();
            var sent = _keyboard.SendPasteShortcut();
            inputStarted.Stop();
            if (!sent)
            {
                throw new InvalidOperationException(
                    "Windows did not accept the paste input. The transcript was saved in Muesli history and the clipboard was left unchanged.");
            }

            _ = RestoreClipboardSafelyAsync(previousClipboard, text, TimeSpan.FromMilliseconds(450));

            totalStarted.Stop();
            return new PasteOperationResult(
                totalStarted.ElapsedMilliseconds,
                clipboardStarted.ElapsedMilliseconds,
                focusWaitMs,
                inputStarted.ElapsedMilliseconds,
                true);
        }
        catch
        {
            if (clipboardWasSet && previousClipboard is not null)
            {
                await RestoreClipboardSafelyAsync(previousClipboard, text, TimeSpan.Zero);
            }

            throw;
        }
    }

    private async Task RestoreClipboardSafelyAsync(
        ClipboardSnapshot previousClipboard,
        string placedText,
        TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await _delay.DelayAsync(delay, CancellationToken.None);
            }

            var currentText = await _clipboard.ReadTextAsync(CancellationToken.None);
            if (ShouldRestoreClipboard(placedText, currentText))
            {
                await _clipboard.RestoreAsync(previousClipboard, CancellationToken.None);
            }
        }
        catch
        {
            // Clipboard restoration is best-effort and never surfaces as an unobserved error.
        }
    }
}

public interface IClipboardAdapter
{
    Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task SetTextAsync(string text, CancellationToken cancellationToken);
    Task<string?> ReadTextAsync(CancellationToken cancellationToken);
    Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IWindowActivationAdapter
{
    IntPtr ForegroundWindow { get; }
    bool Exists(IntPtr window);
    bool RequestForeground(IntPtr window);
}

public interface IWindowDescriptionAdapter
{
    PasteTargetInfo DescribeWindow(IntPtr window);
}

public interface IKeyboardInputAdapter
{
    bool SendPasteShortcut();
    TextInputResult SendText(string text);
}

public readonly record struct TextInputResult(bool Succeeded, bool MayHaveInsertedText);

public interface IAsyncDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed record ClipboardSnapshot(object? Data) : IClipboardSnapshot
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record PasteOperationResult(
    long TotalMs,
    long ClipboardMs,
    long FocusWaitMs,
    long InputMs,
    bool TargetWasForeground);

public sealed record PasteTargetInfo(string ProcessName, uint ProcessId)
{
    public static PasteTargetInfo Unknown { get; } = new("unknown", 0);
}
