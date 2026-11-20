using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using FlaUI.UIA3;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS AccessibilityTarget on UI Automation. Reads the focused field's text and selection (UTF-16
/// offsets), selects ranges, and types acknowledgment groups with SendInput. Only a focused, writable
/// Edit/Document field with Text and Value patterns in the foreground app qualifies; Muesli itself,
/// password fields and elevated apps never do.
/// </summary>
public sealed class UiaLiveDictationTarget
{
    private readonly UIA3Automation _automation;
    private readonly AutomationElement _element;
    private readonly uint _processId;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly List<IDisposable> _handlers = [];
    private int _invalidated;

    private UiaLiveDictationTarget(UIA3Automation automation, AutomationElement element, uint processId)
    {
        _automation = automation;
        _element = element;
        _processId = processId;
    }

    /// <summary>Captures the focused field of <paramref name="window"/>, or null when live text is unsupported there.</summary>
    public static async Task<LiveDictationTextTarget?> CaptureAsync(IntPtr window, Action<string> onDiagnostic)
    {
        if (window == IntPtr.Zero || WindowsInputInterop.GetForegroundWindowNative() != window) return Unavailable("foreground_changed");
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return Unavailable("own_or_unknown_process");
        var info = new NativeWindowActivationAdapter().DescribeWindow(window);
        // Windows drops input sent to a higher-integrity window without reporting an error.
        if (info.RunsAboveMuesli) return Unavailable("elevated_target");
        var automation = new UIA3Automation();
        // The first focused-element lookup shares the deadline: no late result can establish a lease.
        var native = await Task.Run(() =>
        {
            var element = automation.FocusedElement();
            if (element is null) return null;
            var target = new UiaLiveDictationTarget(automation, element, processId);
            return target.Supports() && target.ReadFocusStatus().Kind == LiveDictationFocusStatus.Kinds.Focused ? target : null;
        }).WaitAsync(TimeSpan.FromSeconds(0.75)).ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
        if (native is null || native.ReadSnapshot() is not { } snapshot || WindowsInputInterop.GetForegroundWindowNative() != window)
        {
            automation.Dispose();
            return Unavailable("unsupported_field");
        }
        var live = LiveDictationTextTarget.Create(native.Access(), snapshot, onDiagnostic, applicationName: info.ProcessName);
        if (live is null)
        {
            native.StopObserving();
            return Unavailable("invalid_selection");
        }
        onDiagnostic("transport:targetedKeyboard");
        if (!native.Observe(live))
        {
            onDiagnostic("observer_unavailable");
            live.Cancel();
            return null;
        }
        return live;

        LiveDictationTextTarget? Unavailable(string reason)
        {
            onDiagnostic($"capture_unavailable:{reason}");
            return null;
        }
    }

    /// <summary>
    /// Append-only fallback when <see cref="CaptureAsync"/> finds no verifiable field: still never Muesli,
    /// an elevated window, or a password field, and every burst requires the same foreground window.
    /// </summary>
    public static AppendOnlyLiveDictationTarget? CaptureAppendOnly(IntPtr window, Action<string> onDiagnostic)
    {
        if (window == IntPtr.Zero || WindowsInputInterop.GetForegroundWindowNative() != window) return null;
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return null;
        if (new NativeWindowActivationAdapter().DescribeWindow(window).RunsAboveMuesli) return null;
        using (var automation = new UIA3Automation())
        {
            // An unreadable focus is allowed (terminals); a known password field never is.
            if (Safe(() => automation.FocusedElement()?.Properties.IsPassword.ValueOrDefault) == true)
            {
                onDiagnostic("append_only_unavailable:password");
                return null;
            }
        }
        onDiagnostic("transport:appendOnlyKeyboard");
        return new AppendOnlyLiveDictationTarget(
            () => WindowsInputInterop.GetForegroundWindowNative() == window,
            text => WindowsInputInterop.SendUnicodeText(text).Succeeded,
            onDiagnostic);
    }

    /// <summary>
    /// Rich editors that expose no writable value and text selection are unsupported. We never reconstruct
    /// surrounding formatting.
    /// </summary>
    private bool Supports() => Safe(() =>
        _element.Properties.ProcessId.ValueOrDefault == _processId &&
        !_element.Properties.IsPassword.ValueOrDefault &&
        _element.Properties.IsEnabled.ValueOrDefault &&
        _element.Properties.ControlType.ValueOrDefault is ControlType.Edit or ControlType.Document &&
        _element.Patterns.Text.IsSupported &&
        _element.Patterns.Value.PatternOrDefault is { } value && !value.IsReadOnly.ValueOrDefault);

    private bool IsValid => Volatile.Read(ref _invalidated) == 0;

    private LiveDictationSnapshot? ReadSnapshot()
    {
        if (!IsValid) return null;
        try
        {
            var pattern = _element.Patterns.Text.Pattern;
            var document = pattern.DocumentRange;
            var text = document.GetText(-1) ?? "";
            var selection = pattern.GetSelection();
            if (selection.Length != 1) return null;
            var before = document.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            var location = before.GetText(-1)?.Length ?? -1;
            var length = selection[0].GetText(-1)?.Length ?? -1;
            if (location < 0 || length < 0 || location + length > text.Length) return null;
            return new LiveDictationSnapshot(text, new LiveTextRange(location, length));
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private LiveDictationFocusStatus CurrentProcessStatus()
    {
        if (!IsValid || ProcessExited()) return LiveDictationFocusStatus.Departed("focus_application_terminated");
        var foreground = WindowsInputInterop.GetForegroundWindowNative();
        if (foreground == IntPtr.Zero) return LiveDictationFocusStatus.Unavailable("frontmost_application_unavailable");
        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == _processId ? LiveDictationFocusStatus.Focused : LiveDictationFocusStatus.Departed("focus_application_changed");
    }

    private LiveDictationFocusStatus ReadFocusStatus()
    {
        var process = CurrentProcessStatus();
        if (process.Kind != LiveDictationFocusStatus.Kinds.Focused) return process;
        AutomationElement? focused;
        try { focused = _automation.FocusedElement(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // A failed UIA request is not evidence that the user moved focus.
            return LiveDictationFocusStatus.Unavailable("focused_element_error");
        }
        if (focused is null) return LiveDictationFocusStatus.Unavailable("focused_element_unavailable");
        bool same;
        try { same = focused.Equals(_element); }
        catch (COMException) { return LiveDictationFocusStatus.Unavailable("focused_element_error"); }
        if (!same) return LiveDictationFocusStatus.Departed("focus_field_changed");
        try
        {
            if (_element.Patterns.Value.PatternOrDefault is not { } value || value.IsReadOnly.ValueOrDefault)
                return LiveDictationFocusStatus.Departed("focus_target_read_only");
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040201)) // UIA_E_ELEMENTNOTAVAILABLE
        {
            return LiveDictationFocusStatus.Departed("focus_target_invalid");
        }
        catch (COMException)
        {
            return LiveDictationFocusStatus.Unavailable("capability_error");
        }
        return LiveDictationFocusStatus.Focused;
    }

    private bool ProcessExited()
    {
        try
        {
            using var process = Process.GetProcessById((int)_processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>
    /// Serial UIA executor. A provider can hang, so callers also get a wall-clock deadline; a late result
    /// is discarded and never resumes a lease.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<Func<bool>, T> operation, T fallback, double timeoutSeconds = 0.75)
    {
        if (!IsValid) return fallback;
        var deadline = Stopwatch.StartNew();
        bool MayProceed() => IsValid && deadline.Elapsed.TotalSeconds < timeoutSeconds;
        if (!await _worker.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)).ConfigureAwait(false)) return fallback;
        var work = Task.Run(() =>
        {
            try { return MayProceed() ? operation(MayProceed) : fallback; }
            finally { _worker.Release(); }
        });
        var remaining = TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds - deadline.Elapsed.TotalSeconds));
        try { return await work.WaitAsync(remaining).ConfigureAwait(false); }
        catch (TimeoutException) { return fallback; }
    }

    private LiveDictationTextAccess Access() => new()
    {
        Snapshot = async () =>
        {
            var observed = await RunAsync(_ => ReadFocusStatus().Kind == LiveDictationFocusStatus.Kinds.Focused ? ReadSnapshot() : null,
                (LiveDictationSnapshot?)null).ConfigureAwait(false);
            return CurrentProcessStatus().Kind == LiveDictationFocusStatus.Kinds.Focused ? observed : null;
        },
        FocusStatus = async () =>
        {
            var before = CurrentProcessStatus();
            if (before.Kind != LiveDictationFocusStatus.Kinds.Focused) return before;
            var status = await RunAsync(_ => (LiveDictationFocusStatus?)ReadFocusStatus(), null).ConfigureAwait(false);
            // Recheck cheap process identity after the potentially slow UIA call.
            var after = CurrentProcessStatus();
            if (after.Kind != LiveDictationFocusStatus.Kinds.Focused) return after;
            return status ?? LiveDictationFocusStatus.Unavailable("focus_read_timeout");
        },
        Select = (selection, expected) => RunAsync(mayProceed =>
        {
            if (ReadFocusStatus().Kind != LiveDictationFocusStatus.Kinds.Focused || ReadSnapshot() != expected || !mayProceed()) return false;
            try
            {
                // ponytail: UIA Character units are assumed to be UTF-16 units. Providers that count
                // graphemes mis-select non-BMP text, which the readback rejects (fail closed).
                var range = _element.Patterns.Text.Pattern.DocumentRange.Clone();
                range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, selection.End);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, selection.Location);
                range.Select();
                return true;
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }, false),
        CurrentProcessStatus = CurrentProcessStatus,
        SnapshotNow = ReadSnapshot,
        SendBurst = SendBurst,
        StopObserving = StopObserving
    };

    /// <summary>
    /// macOS dispatchBurst: every guard runs before the first event; then one SendInput call posts the whole
    /// group, so no other input can interleave with it. Windows routes input to the foreground focus, so
    /// the foreground process is checked again immediately before dispatch.
    /// </summary>
    private bool SendBurst(string text, Func<bool> validate, Action<string> onFailure)
    {
        if (_processId == 0 || _processId == (uint)Environment.ProcessId) { onFailure("invalid_process"); return false; }
        var count = System.Globalization.StringInfo.ParseCombiningCharacters(text).Length;
        if (count is 0 or > LiveDictationKeyboardInput.MaximumBurstGraphemes) { onFailure("invalid_burst_size"); return false; }
        if (!LiveDictationKeyboardInput.IsSupported(text)) { onFailure("event_creation"); return false; }
        if (!validate()) { onFailure("target_validation_failed"); return false; }
        var process = CurrentProcessStatus();
        if (process.Kind != LiveDictationFocusStatus.Kinds.Focused)
        {
            onFailure(process.Reason == "focus_application_terminated" ? "application_unavailable" : "frontmost_changed");
            return false;
        }
        var result = WindowsInputInterop.SendUnicodeText(text);
        if (!result.Succeeded) onFailure(result.MayHaveInsertedText ? "partial_dispatch" : "send_input_rejected");
        return result.Succeeded;
    }

    private bool Observe(LiveDictationTextTarget target)
    {
        try
        {
            var text = _automation.EventLibrary.Text;
            _handlers.Add(_element.RegisterAutomationEvent(text.TextChangedEvent, TreeScope.Element, (_, _) => target.TextTargetDidChange()));
            _handlers.Add(_element.RegisterAutomationEvent(text.TextSelectionChangedEvent, TreeScope.Element, (_, _) => target.TextTargetDidChange()));
            _handlers.Add(_automation.RegisterFocusChangedEvent(element =>
            {
                // macOS didActivateApplication: only another app taking the foreground is a known departure.
                // UIA focus events are global, so a tooltip, IME or overlay in another process that leaves the
                // target app frontmost only revalidates.
                var processId = Safe(() => element.Properties.ProcessId.ValueOrDefault);
                if (processId != 0 && processId != _processId &&
                    CurrentProcessStatus().Kind == LiveDictationFocusStatus.Kinds.Departed) target.FocusTargetDidDepart();
                else target.FocusTargetDidChange();
            }));
            return IsValid;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private void StopObserving()
    {
        if (Interlocked.Exchange(ref _invalidated, 1) != 0) return;
        // Unregistering UIA handlers can block on the provider; never do it on the caller's thread.
        var handlers = _handlers.ToList();
        _ = Task.Run(() =>
        {
            foreach (var handler in handlers)
            {
                try { handler.Dispose(); }
                catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
            }
            _automation.Dispose();
        });
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
