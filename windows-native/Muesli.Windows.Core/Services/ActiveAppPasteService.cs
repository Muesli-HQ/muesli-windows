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

    /// <summary>
    /// macOS pastes into whatever is frontmost when the transcript is ready, so clicking into a text
    /// box after starting to speak sends the words there. MAC-20 (macOS c7eed1a4): Muesli's own app
    /// window is a real target, like any editor. Only its helper processes (the indicator pill) are
    /// not; then, or when nothing is focused, the window from the start is kept.
    /// </summary>
    public IntPtr ResolvePasteTarget(IntPtr startTarget)
    {
        var current = _windows.ForegroundWindow;
        if (current == IntPtr.Zero || current == startTarget || !_windows.Exists(current)) return startTarget;
        var info = DescribeWindow(current);
        var isMuesliHelper = info.ProcessId != (uint)Environment.ProcessId &&
            info.ProcessName.StartsWith("Muesli.Windows", StringComparison.OrdinalIgnoreCase);
        return isMuesliHelper ? startTarget : current;
    }

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

        if (DescribeWindow(targetWindow).RunsAboveMuesli)
        {
            // Windows drops input sent to a higher-integrity window without reporting an error.
            throw new PasteBlockedByElevationException();
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

    /// <summary>
    /// macOS PasteController transaction (f5d21107): one clipboard owner per paste. Ownership is the clipboard
    /// generation read right after Muesli's own write, never a text comparison. A newer transaction inherits the
    /// user's original snapshot only while the older one still owns the clipboard, so overlapping pastes can't
    /// "restore" Muesli's own text. Only the current owner may dispatch or restore.
    /// </summary>
    private sealed class Transaction(ClipboardSnapshot original, uint generation)
    {
        public ClipboardSnapshot Original { get; } = original;
        public uint Generation { get; } = generation;
    }

    private readonly object _transactionGate = new();
    private Transaction? _current;

    private async Task<PasteOperationResult> PasteThroughClipboardAsync(
        string text,
        Stopwatch totalStarted,
        long focusWaitMs,
        CancellationToken cancellationToken)
    {
        var clipboardStarted = Stopwatch.StartNew();
        Transaction? previous;
        lock (_transactionGate) previous = _current;
        var original = previous is not null && previous.Generation == _clipboard.Generation
            ? previous.Original
            : await _clipboard.CaptureAsync(cancellationToken);
        Transaction? transaction = null;
        var beforeWrite = _clipboard.Generation;
        try
        {
            await _clipboard.SetTextAsync(text, cancellationToken);
            transaction = new Transaction(original, _clipboard.Generation);
            lock (_transactionGate) _current = transaction;
            clipboardStarted.Stop();

            // Dispatch only while Muesli still owns what it staged; otherwise Ctrl+V would paste another app's data.
            if (_clipboard.Generation != transaction.Generation) throw new ClipboardOwnershipLostException();
            var inputStarted = Stopwatch.StartNew();
            var sent = _keyboard.SendPasteShortcut();
            inputStarted.Stop();
            if (!sent)
            {
                throw new InvalidOperationException(
                    "Windows did not accept the paste input. The transcript was saved in Muesli history and the clipboard was left unchanged.");
            }

            _ = RestoreClipboardSafelyAsync(transaction, TimeSpan.FromMilliseconds(450));

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
            if (transaction is not null) await RestoreClipboardSafelyAsync(transaction, TimeSpan.Zero);
            // A failed write may still have emptied the clipboard; put the original back only if it changed.
            else if (_clipboard.Generation != beforeWrite)
                await RestoreFailedWriteAsync(original);
            throw;
        }
    }

    /// <summary>Restores only for the current transaction while it still owns the clipboard, then releases it.</summary>
    private async Task RestoreClipboardSafelyAsync(Transaction transaction, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await _delay.DelayAsync(delay, CancellationToken.None);
            }

            lock (_transactionGate)
            {
                if (!ReferenceEquals(_current, transaction)) return; // superseded: the newer transaction owns restoration
                _current = null;
            }
            if (_clipboard.Generation == transaction.Generation)
            {
                await _clipboard.RestoreAsync(transaction.Original, CancellationToken.None);
            }
        }
        catch
        {
            // Clipboard restoration is best-effort and never surfaces as an unobserved error.
        }
    }

    private async Task RestoreFailedWriteAsync(ClipboardSnapshot original)
    {
        try { await _clipboard.RestoreAsync(original, CancellationToken.None); }
        catch { /* best-effort, as above */ }
    }
}

public interface IClipboardAdapter
{
    /// <summary>The system clipboard generation (GetClipboardSequenceNumber); every write changes it.</summary>
    uint Generation => 0;
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

/// <param name="RunsAboveMuesli">The app runs as administrator (higher integrity) and Muesli does not.</param>
public sealed record PasteTargetInfo(string ProcessName, uint ProcessId, bool RunsAboveMuesli = false)
{
    public static PasteTargetInfo Unknown { get; } = new("unknown", 0);
}

/// <summary>macOS 784e1a47: another app wrote the clipboard between Muesli's write and the paste shortcut.</summary>
public sealed class ClipboardOwnershipLostException() : InvalidOperationException(
    "Another app changed the clipboard before Muesli could paste. The transcript was saved in Muesli history.");

public sealed class PasteBlockedByElevationException() : InvalidOperationException(
    "The app runs as administrator, so Windows blocks Muesli from typing into it.");
