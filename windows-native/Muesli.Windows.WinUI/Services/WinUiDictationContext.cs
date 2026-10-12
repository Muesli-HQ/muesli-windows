using System.Diagnostics;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

public sealed class WinUiDictationContext : IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiSettingsContext _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log = new();
    private readonly NativeTranscriptionClient _transcription;
    private readonly DictationCoordinator _coordinator;
    private readonly TranscriptionPipelineService _pipeline;
    private readonly NativeTextCleanupService _cleanup;
    private readonly ActiveAppPasteService _paste;
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly DictationHotkeyStateMachine _hotkeyState = new();
    private CancellationTokenSource? _timerCancellation;
    private CancellationTokenSource? _operationCancellation;
    private IntPtr _pasteTarget;
    private string? _captureTraceId;
    private int _disposed;

    // WPF dispatched every shortcut callback with DispatcherPriority.Send, so shortcut down, up,
    // Escape, other-key and the delay timers could never interleave. Appending to one chain under
    // a lock reproduces that: work starts in the order the hook delivered it and each item
    // completes before the next begins, with no async void and no unobserved task.
    private readonly object _hotkeyChainGate = new();
    private Task _hotkeyChain = Task.CompletedTask;

    public WinUiDictationContext(
        WinUiLibraryContext library,
        WinUiSettingsContext settings,
        IUiDispatcher dispatcher)
    {
        _library = library;
        _settings = settings;
        _dispatcher = dispatcher;
        var current = settings.Load();
        _transcription = new NativeTranscriptionClient(current.DictationModelId);
        _coordinator = new DictationCoordinator(_transcription);
        _coordinator.LevelChanged += OnRecordingLevelChanged;
        _cleanup = new NativeTextCleanupService(_log);
        _pipeline = new TranscriptionPipelineService(_cleanup, _log);
        _paste = new ActiveAppPasteService(
            new WinRtClipboardAdapter(),
            new NativeWindowActivationAdapter(),
            new NativeKeyboardInputAdapter());
        Status = "Ready";
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Raised with the live microphone RMS level while a dictation is recording. Marshalled to the UI
    /// thread so the indicator can drive its waveform bars from real capture data.
    /// </summary>
    public event EventHandler<float>? RecordingLevelChanged;

    public bool IsRecording => _coordinator.IsRecording;
    public bool IsBusy => _coordinator.IsBusy || _coordinator.IsTranscribing;
    public bool IsTranscribing => _coordinator.IsTranscribing;
    public bool IsHotkeyRegistered { get; private set; }
    public string Status { get; private set; }

    public void RegisterHotkey()
    {
        ThrowIfDisposed();
        try
        {
            _hotkey.Register(
                _settings.Load().Hotkey,
                () => EnqueueHotkeyWork(() => ExecuteActionAsync(
                    _hotkeyState.KeyDown(_settings.Load().EnableDoubleTapDictation)), "down"),
                () => EnqueueHotkeyWork(() => ExecuteActionAsync(
                    _hotkeyState.KeyUp(_settings.Load().EnableDoubleTapDictation)), "up"),
                // WPF's cancellation predicate. Escape must reach a dictation that is merely armed
                // or preparing, one that is transcribing, and one whose operation token is still
                // live — not only an actively recording one.
                () => DictationCancellationPolicy.CanCancel(
                    _hotkeyState.IsLive,
                    _coordinator.IsRecording,
                    _coordinator.IsTranscribing,
                    _coordinator.IsBusy,
                    _operationCancellation is not null),
                () =>
                {
                    // StopAsync can spend seconds in inference. Signal its token before the
                    // serialized shortcut chain reaches the rest of cancellation cleanup.
                    RequestCancellation();
                    EnqueueHotkeyWork(CancelAsync, "escape");
                },
                () => EnqueueHotkeyWork(() => ExecuteActionAsync(_hotkeyState.OtherKeyWhileArmed()), "other-key"));
            IsHotkeyRegistered = true;
            PrepareMicrophone();
            Status = $"Ready · {_settings.Load().Hotkey}";
            _log.Info($"Dictation shortcut registered. gesture={_settings.Load().Hotkey}");
        }
        catch (Exception exception)
        {
            IsHotkeyRegistered = false;
            Status = $"Shortcut unavailable: {exception.Message}";
            _log.Error("WinUI dictation hotkey registration failed.", exception);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Pre-initializes the microphone for low-latency starts, but only after onboarding has
    /// explained capture. Before that the microphone opens strictly on demand.
    /// </summary>
    public void PrepareMicrophone()
    {
        var settings = _settings.Load();
        if (settings.OnboardingCompleted) _coordinator.PrepareMicrophone(settings.MicrophoneName);
    }

    public IReadOnlyList<string> ListMicrophones() => _coordinator.ListMicrophones();

    public Task StartFromDashboardAsync() => StartAsync(shouldPasteToActiveApp: false);

    public Task StopFromDashboardAsync() => StopAsync();

    /// <summary>Stops the active dictation and transcribes it (the indicator's "stop" action).</summary>
    public Task StopRecordingAsync() => StopAsync();

    public async Task StartAuxiliaryAsync(string? microphoneName = null)
    {
        ThrowIfDisposed();
        if (IsRecording || IsBusy) return;
        Status = "Listening";
        RaiseChanged();
        await _coordinator.StartAsync(microphoneName ?? _coordinator.PickPreferredMicrophone(), DictationSessionKind.Auxiliary);
        Status = _coordinator.IsRecording ? "Listening" : "Ready";
        RaiseChanged();
    }

    public async Task<string> StopWithoutPersistingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_coordinator.IsRecording) return "";
        Status = "Transcribing locally…";
        RaiseChanged();
        var result = await _coordinator.StopForOnboardingTestAsync(cancellationToken);
        var text = result.Text?.Trim() ?? "";
        Status = string.IsNullOrWhiteSpace(text) ? "No speech detected" : "Local test captured";
        RaiseChanged();
        return text;
    }

    public async Task CancelAsync()
    {
        StopTimers();
        RequestCancellation();
        await _coordinator.CancelAsync();
        _hotkeyState.Reset();
        _pasteTarget = IntPtr.Zero;
        Status = "Dictation cancelled";
        _log.Info($"Dictation cancelled. trace={_captureTraceId ?? "not-started"}");
        RaiseChanged();
    }

    private void RequestCancellation()
    {
        try
        {
            _operationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed operation can replace its token while the hook callback arrives.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopTimers();
        _operationCancellation?.Cancel();
        _hotkey.Dispose();
        _coordinator.LevelChanged -= OnRecordingLevelChanged;
        _coordinator.Dispose();
        _cleanup.Dispose();
        _operationCancellation?.Dispose();
    }

    /// <summary>
    /// Appends hotkey work to the single serialized chain. Order of arrival is preserved and each
    /// item runs to completion before the next starts, so a key-up can never overtake the delay
    /// timer that is still deciding whether to start recording.
    /// </summary>
    private void EnqueueHotkeyWork(Func<Task> work, string signal = "timer")
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_hotkeyChainGate)
        {
            var previous = _hotkeyChain;
            var receivedAt = Stopwatch.GetTimestamp();
            // An async method runs inline until its first incomplete await. In particular,
            // settings I/O and microphone setup must never execute inside the keyboard hook.
            _hotkeyChain = Task.Run(() => RunChainedAsync(previous, work, signal, receivedAt));
        }
    }

    private async Task RunChainedAsync(Task previous, Func<Task> work, string signal, long receivedAt)
    {
        if (signal != "timer")
            _log.Info($"Dictation shortcut received. signal={signal}; recording={IsRecording}; busy={IsBusy}");
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The previous link already reported itself; never break the chain.
        }

        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            var queuedMs = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds;
            if (queuedMs >= 1000)
                _log.Info($"Dictation shortcut delayed. signal={signal}; queuedMs={queuedMs:F0}");
            await work().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Status = $"Dictation failed: {exception.Message}";
            _log.Error("WinUI dictation hotkey action failed.", exception);
            ResetHotkeyDictationState();
            RaiseChanged();
        }
    }

    /// <summary>
    /// WPF's <c>ResetHotkeyDictationState</c>: stop every delay timer and return the state machine
    /// to idle so the next shortcut press is accepted without restarting Muesli.
    /// </summary>
    private void ResetHotkeyDictationState()
    {
        StopTimers();
        _hotkeyState.Reset();
    }

    private async Task ExecuteActionAsync(DictationHotkeyAction action)
    {
        switch (action)
        {
            case DictationHotkeyAction.Arm:
                // WPF parity: arming only starts the delay timers. The pill must stay idle until
                // the prepare delay actually elapses, so a quick tap never flashes "preparing".
                StopTimers();
                Status = "Ready";
                RaiseChanged();
                StartTimers();
                break;

            case DictationHotkeyAction.ShowPreparing:
                Status = "Preparing microphone…";
                RaiseChanged();
                break;

            case DictationHotkeyAction.StartRecording:
                StopTimers();
                await StartAsync(shouldPasteToActiveApp: true);
                if (!_coordinator.IsRecording) ReleaseAfterFailedStart();
                break;

            case DictationHotkeyAction.EnterHandsFree:
                // Hands-free is a distinct mode: it keeps recording after the shortcut is
                // released, and the next shortcut action stops it.
                StopTimers();
                Status = "Hands-free dictation active";
                RaiseChanged();
                await StartAsync(shouldPasteToActiveApp: true);
                if (!_coordinator.IsRecording) ReleaseAfterFailedStart();
                break;

            case DictationHotkeyAction.StopRecording:
                StopTimers();
                await StopAsync();
                break;

            case DictationHotkeyAction.Cancel:
                StopTimers();
                await CancelAsync();
                break;

            case DictationHotkeyAction.StartDoubleTapTimer:
                StopTimers();
                StartDoubleTapTimer();
                break;
        }
    }

    /// <summary>
    /// WPF's recovery when the microphone never opened: drop every timer, reset the state machine,
    /// clear the paste target and the operation token, and release the live indicator so the pill
    /// leaves its recording state instead of hanging there until the app restarts.
    /// </summary>
    private void ReleaseAfterFailedStart()
    {
        ResetHotkeyDictationState();
        _pasteTarget = IntPtr.Zero;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        RaiseChanged();
    }

    private async Task StartAsync(bool shouldPasteToActiveApp)
    {
        if (IsRecording || IsBusy)
        {
            _log.Info($"Dictation start ignored. recording={IsRecording}; busy={IsBusy}; transcribing={IsTranscribing}");
            return;
        }
        _captureTraceId = Guid.NewGuid().ToString("N")[..12];
        _log.Info($"Dictation start requested. trace={_captureTraceId}; source={(shouldPasteToActiveApp ? "shortcut" : "dashboard")}");
        var current = _settings.Load();
        if (!string.Equals(_coordinator.ModelId, current.DictationModelId, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Switching transcription model…";
            RaiseChanged();
            await _coordinator.SwitchModelAsync(current.DictationModelId);
        }

        // Capture the paste target before any indicator work: the floating pill must never become
        // the foreground window that the transcript is pasted into.
        _pasteTarget = shouldPasteToActiveApp ? _paste.CaptureForegroundWindow() : IntPtr.Zero;
        _coordinator.SoundFeedback.Enabled = current.SoundEnabled;
        Status = "Listening";
        RaiseChanged();
        try
        {
            await _coordinator.StartAsync(current.MicrophoneName ?? _coordinator.PickPreferredMicrophone());
        }
        catch (Exception exception)
        {
            // A denied microphone is not a broken shortcut; say which it is and how to fix it.
            Status = MicrophoneAccessDiagnostics.DescribeFailure(exception);
            if (MicrophoneAccessDiagnostics.IsAccessDenied(exception))
            {
                _log.Error(
                    "Dictation microphone access was denied by Windows. Grant it at " +
                    $"{MicrophoneAccessDiagnostics.PrivacySettingsUri}, and ensure the package " +
                    "declares the microphone device capability.",
                    exception);
            }
            else
            {
                _log.Error("Could not start dictation microphone.", exception);
            }
            _pasteTarget = IntPtr.Zero;
            RaiseChanged();
            return;
        }

        _log.Info($"Dictation capture start result. trace={_captureTraceId}; recording={_coordinator.IsRecording}; model={_coordinator.ModelId}");
        Status = !_coordinator.IsRecording ? "Ready" :
            _coordinator.ModelId == current.DictationModelId ? "Listening" : "Listening with Parakeet (low memory)";
        RaiseChanged();
    }

    private async Task StopAsync()
    {
        _log.Info($"Dictation stop requested. trace={_captureTraceId ?? "not-started"}; recording={IsRecording}; busy={IsBusy}");
        if (!_coordinator.IsRecording) return;
        var totalStarted = Stopwatch.StartNew();
        var pipelineMs = 0L;
        var persistenceMs = 0L;
        var deliveryMs = 0L;
        var deliveryFocusMs = 0L;
        var deliveryInputMs = 0L;
        var deliveryClipboardMs = 0L;
        var persisted = false;
        var asrPath = "unavailable";
        DictationLatencyMetrics? latency = null;
        _hotkeyState.Reset();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        Status = "Transcribing locally…";
        RaiseChanged();
        try
        {
            var stop = await _coordinator.StopAsync(
                _captureTraceId ?? Guid.NewGuid().ToString("N")[..12],
                _operationCancellation.Token);
            latency = stop.Latency;
            var result = stop.Transcription;
            asrPath = result.Diagnostic?.Split('\n')[0].TrimEnd('\r') ?? "";
            if (!asrPath.StartsWith("ASR during capture:", StringComparison.Ordinal) &&
                !asrPath.StartsWith("Rolling fallback:", StringComparison.Ordinal))
                asrPath = "whole-file";
            if (string.IsNullOrWhiteSpace(result.Text) ||
                result.Text.Contains("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
            {
                Status = result.DurationMs < HotkeyTriggerTiming.ShortDiscardMilliseconds
                    ? "Ready"
                    : "No speech detected";
                return;
            }

            var settings = _settings.Load();
            var stageStarted = Stopwatch.StartNew();
            var text = await _pipeline.PrepareDictationTextAsync(
                result.Text,
                settings.EnableLocalCleanup,
                settings.RemoveFillerWords,
                _library.LoadDictionary());
            pipelineMs = stageStarted.ElapsedMilliseconds;
            _operationCancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text))
            {
                Status = "No text remained after cleanup";
                return;
            }

            stageStarted.Restart();
            _library.History.AppendDictation(new PersistedDictation(
                $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}",
                DateTime.Now,
                text,
                result.DurationMs,
                _coordinator.ModelId));
            persisted = true;
            persistenceMs = stageStarted.ElapsedMilliseconds;

            if (_pasteTarget != IntPtr.Zero)
            {
                try
                {
                    _operationCancellation.Token.ThrowIfCancellationRequested();
                    stageStarted.Restart();
                    var paste = await _paste.PasteTextAsync(text, _pasteTarget, _operationCancellation.Token);
                    deliveryMs = stageStarted.ElapsedMilliseconds;
                    deliveryFocusMs = paste.FocusWaitMs;
                    deliveryInputMs = paste.InputMs;
                    deliveryClipboardMs = paste.ClipboardMs;
                    Status = "Dictation inserted";
                }
                catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception pasteException)
                {
                    await _paste.CopyTextAsync(text, CancellationToken.None);
                    deliveryMs = stageStarted.ElapsedMilliseconds;
                    Status = "Paste failed; transcript saved and copied";
                    _log.Error("WinUI active-app paste failed after dictation persistence.", pasteException);
                }
            }
            else
            {
                Status = "Dictation saved";
            }
        }
        catch (OperationCanceledException)
        {
            Status = persisted ? "Dictation saved; insertion cancelled" : "Dictation cancelled";
        }
        finally
        {
            _log.Info(
                $"Dictation delivery latency. trace={latency?.TraceId ?? "unavailable"}; model={_coordinator.ModelId}; " +
                $"captureStopMs={latency?.CaptureTotalMs ?? 0}; " +
                $"capturePreparationMs={latency?.CapturePreparationMs ?? 0}; " +
                $"asrWallMs={latency?.TranscriptionWallMs ?? 0}; " +
                $"asrPath={asrPath}; " +
                $"textPipelineMs={pipelineMs}; persistenceMs={persistenceMs}; " +
                $"deliveryMs={deliveryMs}; deliveryFocusMs={deliveryFocusMs}; " +
                $"deliveryInputMs={deliveryInputMs}; deliveryClipboardMs={deliveryClipboardMs}; " +
                $"totalMs={totalStarted.ElapsedMilliseconds}; " +
                $"outcome={Status}");
            _pasteTarget = IntPtr.Zero;
            // A live token makes the Escape hook swallow the key in every app, so it must not
            // outlive the dictation it belongs to.
            var finished = _operationCancellation;
            _operationCancellation = null;
            finished?.Dispose();
            RaiseChanged();
        }
    }

    private void StartTimers()
    {
        StopTimers();
        _timerCancellation = new CancellationTokenSource();
        var token = _timerCancellation.Token;
        var settings = _settings.Load();
        _ = RunTimerAsync(
            HotkeyTriggerTiming.PrepareDelay(settings.HotkeyTriggerThresholdMs, settings.EnableDoubleTapDictation),
            () => _hotkeyState.PrepareDelayElapsed(),
            token);
        _ = RunTimerAsync(
            HotkeyTriggerTiming.StartDelay(settings.HotkeyTriggerThresholdMs, settings.EnableDoubleTapDictation),
            () => _hotkeyState.StartDelayElapsed(),
            token);
    }

    private void StartDoubleTapTimer()
    {
        StopTimers();
        _timerCancellation = new CancellationTokenSource();
        _ = RunTimerAsync(
            HotkeyTriggerTiming.DoubleTapWindow,
            () => _hotkeyState.DoubleTapWindowElapsed(),
            _timerCancellation.Token);
    }

    private async Task RunTimerAsync(
        TimeSpan delay,
        Func<DictationHotkeyAction> next,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            // Through the same chain as the shortcut callbacks: a tick and a key-up that arrive
            // together must not both drive the state machine at once.
            EnqueueHotkeyWork(() => ExecuteActionAsync(next()));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopTimers()
    {
        _timerCancellation?.Cancel();
        _timerCancellation?.Dispose();
        _timerCancellation = null;
    }

    private void RaiseChanged() => _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));

    private void OnRecordingLevelChanged(object? sender, AudioLevelEventArgs e) =>
        _dispatcher.TryEnqueue(() => RecordingLevelChanged?.Invoke(this, e.Rms));

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
