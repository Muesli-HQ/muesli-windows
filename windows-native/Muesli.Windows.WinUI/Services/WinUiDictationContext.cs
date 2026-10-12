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
    private readonly DictationMediaControl _media;
    private Task<string?>? _appContext;
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
    private volatile bool _auxiliarySession;
    private volatile Action<string, string?>? _shortcutTest;
    private int _disposed;

    // WPF dispatched every shortcut callback with DispatcherPriority.Send, so shortcut down, up,
    // Escape, other-key and the delay timers could never interleave. Appending to one chain under
    // a lock reproduces that: work starts in the order the hook delivered it and each item
    // completes before the next begins, with no async void and no unobserved task.
    private readonly object _hotkeyChainGate = new();
    private Task _hotkeyChain = Task.CompletedTask;

    /// <summary>Raised after a dictation was pasted into another app (macOS starts its correction monitor here).</summary>
    public event Action<DictationPaste>? Pasted;

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
            new NativeKeyboardInputAdapter(() => _settings.Load().PasteShortcut));
        _media = new DictationMediaControl(_log);
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
        if (!_settings.Load().PushToTalkEnabled)
        {
            // Off means the key belongs to other apps again, so the hook is removed, not muted.
            _hotkey.Dispose();
            IsHotkeyRegistered = false;
            Status = "Push to Talk is turned off.";
            _log.Info("Dictation shortcut disabled.");
            RaiseChanged();
            return;
        }
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
                    // serialized shortcut chain reaches the rest of cancellation cleanup. Cancel()
                    // runs token callbacks inline, so it must not run on the keyboard-hook thread.
                    _ = Task.Run(RequestCancellation);
                    EnqueueHotkeyWork(CancelCoreAsync, "escape");
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

    // Dashboard, tray and pill actions join the same serialized chain as the shortcut, so a stop
    // from one surface can never overlap a stop from another and clear its paste target.
    public Task StartFromDashboardAsync() => RunSerializedAsync(() => StartAsync(shouldPasteToActiveApp: false), "dashboard-start");

    public Task StopFromDashboardAsync() => RunSerializedAsync(StopAsync, "dashboard-stop");

    /// <summary>Stops the active dictation and transcribes it (the indicator's "stop" action).</summary>
    public Task StopRecordingAsync() => RunSerializedAsync(StopAsync, "indicator-stop");

    /// <summary>
    /// True while an auxiliary (setup test or Computer Use) capture owns the microphone. Its owner
    /// stops it without persisting; the normal stop path must never save or paste it.
    /// </summary>
    public bool IsAuxiliarySession => _auxiliarySession;

    public async Task StartAuxiliaryAsync(string? microphoneName = null)
    {
        ThrowIfDisposed();
        if (IsRecording || IsBusy) return;
        Status = "Listening";
        RaiseChanged();
        _auxiliarySession = true;
        await _coordinator.StartAsync(microphoneName ?? _coordinator.PickPreferredMicrophone(), DictationSessionKind.Auxiliary);
        if (!_coordinator.IsRecording) _auxiliarySession = false;
        Status = _coordinator.IsRecording ? "Listening" : "Ready";
        RaiseChanged();
    }

    /// <param name="reportResult">False when the caller reports the outcome itself (Computer Use):
    /// a leftover "Local test captured" would otherwise read as a dictation result on the pill
    /// and hide the run's own message.</param>
    public async Task<string> StopWithoutPersistingAsync(CancellationToken cancellationToken = default, bool reportResult = true)
    {
        ThrowIfDisposed();
        if (!_coordinator.IsRecording) return "";
        Status = "Transcribing locally…";
        RaiseChanged();
        try
        {
            var result = await _coordinator.StopForOnboardingTestAsync(cancellationToken);
            var text = result.Text?.Trim() ?? "";
            Status = !reportResult ? "Ready" : string.IsNullOrWhiteSpace(text) ? "No speech detected" : "Local test captured";
            RaiseChanged();
            return text;
        }
        finally
        {
            _auxiliarySession = false;
        }
    }

    /// <summary>
    /// macOS dictationTestCallback: while set, the real shortcut records a setup test that is
    /// transcribed and handed back (text, or an error) but never saved or pasted.
    /// </summary>
    public void BeginShortcutTest(Action<string, string?> result) => _shortcutTest = result;

    public void EndShortcutTest()
    {
        _shortcutTest = null;
        if (IsRecording && !_auxiliarySession) _ = CancelAsync();
    }

    public Task CancelAsync()
    {
        RequestCancellation();
        return RunSerializedAsync(CancelCoreAsync, "cancel");
    }

    private async Task CancelCoreAsync()
    {
        _auxiliarySession = false;
        StopTimers();
        RequestCancellation();
        await _coordinator.CancelAsync();
        await _media.RestoreAsync();
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
        Task.Run(_media.RestoreAsync).Wait(TimeSpan.FromSeconds(2));
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

    /// <summary>Runs work on the serialized chain and completes when that work does.</summary>
    private Task RunSerializedAsync(Func<Task> work, string signal)
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueHotkeyWork(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }, signal);
        return done.Task;
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
                await CancelCoreAsync();
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

        // Fail closed before opening the microphone (macOS blocks at the hotkey press): speaking into
        // a session that cannot produce text throws the user's words away.
        if (!_coordinator.IsModelReady)
        {
            Status = $"Download {_coordinator.ModelDisplayName} in Models to dictate";
            _log.Info($"Dictation start blocked. trace={_captureTraceId}; model={_coordinator.ModelId}; reason=model-not-ready");
            RaiseChanged();
            return;
        }

        // Capture the paste target before any indicator work: the floating pill must never become
        // the foreground window that the transcript is pasted into.
        // A setup test and macOS voice notes keep the text in Muesli instead of pasting it.
        _pasteTarget = shouldPasteToActiveApp && _shortcutTest is null && !OnboardingFlow.IsVoiceNotesOnly(current.OnboardingUseCase)
            ? _paste.CaptureForegroundWindow()
            : IntPtr.Zero;
        // Read while the target app still has focus; only hosted cleanup with app context uses it.
        _appContext = _pasteTarget != IntPtr.Zero && current.EnableLocalCleanup && current.EnableAppContext &&
            MeetingSummaryService.IsHostedProvider(current.CleanupSource)
            ? Task.Run(() => { try { return FocusedTextSelection.ReadAppContext(_pasteTarget); } catch (Exception) { return null; } })
            : null;
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
        if (_coordinator.IsRecording) _media.Apply(current.PauseMediaDuringDictation, current.MuteAudioDuringDictation);
        Status = !_coordinator.IsRecording ? "Ready" :
            _coordinator.ModelId == current.DictationModelId ? "Listening" : "Listening with Parakeet (low memory)";
        RaiseChanged();
    }

    private async Task StopAsync()
    {
        _log.Info($"Dictation stop requested. trace={_captureTraceId ?? "not-started"}; recording={IsRecording}; busy={IsBusy}");
        // Restoring is a no-op when nothing was paused or muted, so it runs before every early exit.
        await _media.RestoreAsync();
        if (!_coordinator.IsRecording) return;
        if (_auxiliarySession)
        {
            // A setup test or Computer Use command is stopped by its owner, never saved or pasted.
            _log.Info("Dictation stop ignored for an auxiliary capture.");
            return;
        }
        if (_shortcutTest is { } test)
        {
            _hotkeyState.Reset();
            Status = "Transcribing locally…";
            RaiseChanged();
            try
            {
                var tested = await _coordinator.StopForOnboardingTestAsync(CancellationToken.None);
                var heard = TranscriptionEngineArtifactsFilter.Apply(tested.Text ?? "").Trim();
                _dispatcher.TryEnqueue(() => test(heard, null));
            }
            catch (Exception exception)
            {
                _dispatcher.TryEnqueue(() => test("", exception.Message));
            }
            finally
            {
                _pasteTarget = IntPtr.Zero;
                Status = "Ready";
                RaiseChanged();
            }
            return;
        }
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
            // Strip engine tokens like [BLANK_AUDIO] instead of discarding the speech around them.
            var recognized = TranscriptionEngineArtifactsFilter.Apply(result.Text ?? "");
            if (string.IsNullOrWhiteSpace(recognized))
            {
                Status = result.DurationMs < HotkeyTriggerTiming.ShortDiscardMilliseconds
                    ? "Ready"
                    : "No speech detected";
                return;
            }

            var settings = _settings.Load();
            var stageStarted = Stopwatch.StartNew();
            var hostedCleanup = settings.EnableLocalCleanup && MeetingSummaryService.IsHostedProvider(settings.CleanupSource);
            if (hostedCleanup)
            {
                Status = "Cleaning up…";
                RaiseChanged();
                var appContext = _appContext is { } pending ? await pending : null;
                recognized = await HostedTranscriptCleanup.CleanAsync(recognized, appContext,
                    (system, user, token) => MeetingSummaryService.CompleteAsync(settings.CleanupSource, system, user, settings, null, token),
                    (message, exception) => _log.Error(message, exception),
                    _operationCancellation.Token);
            }
            var text = await _pipeline.PrepareDictationTextAsync(
                recognized,
                settings.EnableLocalCleanup && !hostedCleanup,
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
            var dictationId = $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}";
            _library.History.AppendDictation(new PersistedDictation(
                dictationId,
                DateTime.Now,
                text,
                result.DurationMs,
                _coordinator.ModelId));
            persisted = true;
            persistenceMs = stageStarted.ElapsedMilliseconds;
            // macOS records the destination app on each dictation (the row's app icon and filter).
            var pasteTarget = _pasteTarget;
            var targetApp = DictationTargetApps.Capture(pasteTarget);
            if (targetApp is not null)
            {
                try { DictationTargetApps.Record(_library.Profile.RootDirectory, dictationId, targetApp); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _log.Error("The dictation's destination app could not be saved.", exception);
                }
            }

            if (_pasteTarget != IntPtr.Zero)
            {
                try
                {
                    _operationCancellation.Token.ThrowIfCancellationRequested();
                    stageStarted.Restart();
                    var paste = await _paste.PasteTextAsync(
                        text + DictationPasteSpacing.TrailingSeparator(text), _pasteTarget, _operationCancellation.Token);
                    deliveryMs = stageStarted.ElapsedMilliseconds;
                    deliveryFocusMs = paste.FocusWaitMs;
                    deliveryInputMs = paste.InputMs;
                    deliveryClipboardMs = paste.ClipboardMs;
                    Status = "Dictation inserted";
                    Pasted?.Invoke(new DictationPaste(text, targetApp, pasteTarget));
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

public sealed record DictationPaste(string Text, DictationTargetApp? App, IntPtr Window);
