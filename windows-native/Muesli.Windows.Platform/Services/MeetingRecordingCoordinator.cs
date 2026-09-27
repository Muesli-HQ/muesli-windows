namespace Muesli.Windows.Services;

public sealed class MeetingRecordingCoordinator : IDisposable
{
    private readonly NativeTranscriptionClient _transcriptionClient;
    private readonly MeetingPersistenceBoundary _persistence;
    private readonly MeetingFinalizationPipeline _finalization;
    private readonly MeetingCaptureSession _capture;
    private readonly MeetingRecoveryWorkflow _recovery;
    private readonly AppLogService? _logService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _warningGate = new();
    private readonly MeetingSessionStateMachine _stateMachine = new();
    private readonly List<string> _healthWarnings = [];
    private MeetingSessionJournal? _journal;
    private LiveTranscriptionConfiguration? _liveConfiguration;
    private CancellationTokenSource? _lifecycleCancellation;
    private Task<ModelOperationResult>? _modelWarmupTask;
    private DateTime _startedAt;
    private string _meetingId = "";
    private int? _targetProcessId;
    private int _disposed;

    public MeetingRecordingCoordinator(
        NativeTranscriptionClient transcriptionClient,
        AppLogService? logService = null,
        MeetingSessionJournalStore? journalStore = null)
    {
        _transcriptionClient = transcriptionClient;
        _logService = logService;
        _persistence = new MeetingPersistenceBoundary(journalStore ?? new MeetingSessionJournalStore(
            report: warning => _logService?.Info(
                $"Meeting journal recovery notice. category={DiagnosticCategory(warning)}")));
        _finalization = new MeetingFinalizationPipeline(
            transcriptionClient,
            _persistence,
            logService: logService);
        _capture = new MeetingCaptureSession(_persistence, logService);
        _recovery = new MeetingRecoveryWorkflow(_persistence, logService);
        _capture.BindRepairHost(new MeetingCaptureRepairHost(
            IsRecording: () => IsRecording,
            State: () => State,
            Journal: () => _journal,
            SetJournal: journal => _journal = journal,
            AddWarning: AddHealthWarning,
            Transition: Transition,
            TryTransition: TryTransition,
            SaveState: SaveJournalState,
            LogDiagnostic: LogSessionDiagnostic,
            LifecycleGate: _gate,
            LifecycleToken: () => _lifecycleCancellation?.Token ?? CancellationToken.None));
        _capture.HealthChanged += (_, args) => HealthChanged?.Invoke(this, args);
        _capture.LevelChanged += (_, args) => LevelChanged?.Invoke(this, args);
        _capture.LiveTranscriptChanged += (_, args) => LiveTranscriptChanged?.Invoke(this, args);
        _capture.LiveTranscriptionFailed += (_, args) => LiveTranscriptionFailed?.Invoke(this, args);
    }

    public event EventHandler<MeetingSessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<MeetingAudioHealthChangedEventArgs>? HealthChanged;
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler<LiveTranscriptSnapshot>? LiveTranscriptChanged;
    public event EventHandler<Exception>? LiveTranscriptionFailed;

    public MeetingSessionState State => _stateMachine.State;
    public bool IsRecording => State is MeetingSessionState.Recording or MeetingSessionState.DegradedRecording;
    public bool IsBusy => State is MeetingSessionState.Preparing or MeetingSessionState.Stopping or MeetingSessionState.Finalizing;

    public IReadOnlyList<RecoverableMeetingSession> DiscoverRecoverableSessions() =>
        _recovery.Discover();

    public MeetingCaptureSnapshot CaptureSnapshot
    {
        get
        {
            lock (_warningGate)
            {
                return new MeetingCaptureSnapshot(
                    State,
                    _journal?.SystemCaptureMode ?? "idle",
                    _capture.MicrophoneAvailable,
                    _capture.SystemAvailable,
                    CurrentHealthWarnings().ToArray(),
                    State == MeetingSessionState.RecoverableInterruption,
                    _journal?.RecoveredFromInterruption ?? false);
            }
        }
    }

    public async Task<MeetingStartResult> StartAsync(
        string? microphoneName,
        string? title = null,
        bool retainRecording = true,
        int? targetProcessId = null,
        CancellationToken cancellationToken = default,
        LiveTranscriptionConfiguration? liveConfiguration = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRecording || IsBusy)
            {
                throw new InvalidOperationException("A meeting session is already active.");
            }
            ResetTerminalStateIfNeeded();
            Transition(MeetingSessionTrigger.Prepare, "meeting capture requested");

            _startedAt = DateTime.Now;
            _meetingId = $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..8]}";
            var resolvedMicrophone = string.IsNullOrWhiteSpace(microphoneName)
                ? AudioCaptureService.SystemDefaultMicrophone
                : microphoneName;
            _targetProcessId = targetProcessId is > 0 ? targetProcessId : null;
            _liveConfiguration = liveConfiguration;
            lock (_warningGate)
            {
                _healthWarnings.Clear();
            }

            _journal = _persistence.Create(
                _meetingId,
                string.IsNullOrWhiteSpace(title) ? $"Meeting {_startedAt:yyyy-MM-dd HH-mm}" : title.Trim(),
                new DateTimeOffset(_startedAt),
                resolvedMicrophone,
                _transcriptionClient.ModelId,
                retainRecording,
                _targetProcessId,
                liveConfiguration?.ModelId,
                liveConfiguration?.OwnershipMode);
            _lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _capture.Prepare(_startedAt, resolvedMicrophone, _targetProcessId, _liveConfiguration, resetLiveResult: true);

            await _capture.StartLiveAsync(_lifecycleCancellation.Token).ConfigureAwait(false);

            var start = await _capture.StartPairAsync(_journal, _lifecycleCancellation.Token).ConfigureAwait(false);
            _journal = _journal with
            {
                SystemCaptureMode = start.SystemCaptureMode?.ToString() ?? "unavailable"
            };
            if (!_capture.MicrophoneAvailable && !_capture.SystemAvailable)
            {
                Transition(MeetingSessionTrigger.Fail, "both capture channels failed to start");
                SaveJournalState(MeetingSessionState.Failed, "capture-start-failed");
                _recovery.DeleteWorkingSession(_meetingId, MeetingAudioCleanupReason.FailedStartWithoutAudio);
                throw new InvalidOperationException("Neither microphone nor system audio could start.");
            }

            Transition(
                _capture.MicrophoneAvailable && _capture.SystemAvailable
                    ? MeetingSessionTrigger.Prepared
                    : MeetingSessionTrigger.PreparedDegraded,
                _capture.MicrophoneAvailable && _capture.SystemAvailable ? "both channels started" : "one channel started");
            SaveJournalState(State);
            _capture.StartHealthLoop(_lifecycleCancellation.Token);
            BeginFinalModelWarmup();
            LogSessionDiagnostic("started");
            return new MeetingStartResult(
                _meetingId,
                State,
                start.SystemCaptureMode,
                start.Warning,
                _capture.MicrophoneAvailable,
                _capture.SystemAvailable);
        }
        catch
        {
            if (State == MeetingSessionState.Preparing && _stateMachine.CanApply(MeetingSessionTrigger.Fail))
            {
                Transition(MeetingSessionTrigger.Fail, "meeting preparation failed");
            }
            await _capture.CancelCaptureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecordedMeetingResult> StopAsync(
        string title,
        bool retainRecording,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsRecording)
            {
                throw new InvalidOperationException("Meeting recording is not running.");
            }

            if (_journal is not null && _journal.RetainRecording != retainRecording)
            {
                _journal = _journal with { RetainRecording = retainRecording };
            }
            Transition(MeetingSessionTrigger.Stop, "user or qualified auto-stop requested");
            StopHealthLoop();
            SaveJournalState(MeetingSessionState.Stopping);
            await _capture.CheckpointAllAsync().ConfigureAwait(false);
            Transition(MeetingSessionTrigger.TracksFinalized, "capture tracks checkpointed");
            SaveJournalState(MeetingSessionState.Finalizing);
            return await FinalizeJournalUnderGateAsync(title, recovered: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _recovery.PreserveRecoverable(RecoveryHost, "finalization-cancelled");
            throw;
        }
        catch (Exception exception) when (State is MeetingSessionState.Stopping or MeetingSessionState.Finalizing)
        {
            if (MeetingPersistenceBoundary.HasAudio(_journal))
            {
                _recovery.PreserveRecoverable(RecoveryHost, exception.GetType().Name);
                throw new MeetingSessionRecoverableException(
                    "Meeting finalization failed, but its local audio is retained for retry.",
                    _meetingId,
                    exception);
            }

            Transition(MeetingSessionTrigger.Fail, "finalization failed without recoverable audio");
            SaveJournalState(MeetingSessionState.Failed, exception.GetType().Name);
            throw;
        }
        finally
        {
            await _capture.DisposeCaptureAsync().ConfigureAwait(false);
            _gate.Release();
        }
    }

    public async Task<RecordedMeetingResult> FinalizeRecoverableAsync(
        RecoverableMeetingSession recovery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRecording || IsBusy)
            {
                throw new InvalidOperationException("Another meeting session is active.");
            }
            ResetTerminalStateIfNeeded();
            var hydration = MeetingRecoveryWorkflow.Hydrate(recovery);
            _journal = hydration.Journal;
            _meetingId = hydration.MeetingId;
            _startedAt = hydration.StartedAt;
            _targetProcessId = hydration.TargetProcessId;
            _capture.RestoreLiveResult(hydration.LiveResult);
            lock (_warningGate)
            {
                _healthWarnings.Clear();
                _healthWarnings.AddRange(hydration.Warnings);
            }

            Transition(MeetingSessionTrigger.RestoreInterrupted, "recoverable journal loaded");
            Transition(MeetingSessionTrigger.FinalizeInterrupted, "user requested recovery finalization");
            Transition(MeetingSessionTrigger.TracksFinalized, "recovered tracks already checkpointed");
            SaveJournalState(MeetingSessionState.Finalizing);
            return await FinalizeJournalUnderGateAsync(_journal.Title, recovered: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _recovery.PreserveRecoverable(RecoveryHost, "recovery-finalization-cancelled");
            throw;
        }
        catch (Exception exception) when (State is MeetingSessionState.Stopping or MeetingSessionState.Finalizing)
        {
            _recovery.PreserveRecoverable(RecoveryHost, exception.GetType().Name);
            throw new MeetingSessionRecoverableException(
                "Recovered meeting audio is still retained, but finalization failed.",
                _meetingId,
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SuspendAsync(CancellationToken cancellationToken = default) =>
        SuspendAsync(
            "Windows power suspend",
            "Recording paused because Windows suspended. Capture will resume when the system returns.",
            "power-suspend",
            cancellationToken);

    public async Task SuspendAsync(
        string reason,
        string warning,
        string failureCategory,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRecording)
            {
                return;
            }
            StopHealthLoop();
            await CheckpointForInterruptionUnderGateAsync(reason, warning, failureCategory).ConfigureAwait(false);
            LogSessionDiagnostic("suspended");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != MeetingSessionState.RecoverableInterruption || _journal is null)
            {
                return;
            }
            Transition(MeetingSessionTrigger.Resume, "Windows power resume");
            _lifecycleCancellation?.Dispose();
            _lifecycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _capture.Prepare(
                _startedAt,
                _journal.MicrophoneName,
                _journal.TargetProcessId,
                _liveConfiguration,
                resetLiveResult: false,
                healthAnchor: DateTimeOffset.UtcNow);
            await _capture.StartLiveAsync(_lifecycleCancellation.Token).ConfigureAwait(false);
            var start = await _capture.StartPairAsync(_journal, _lifecycleCancellation.Token).ConfigureAwait(false);
            _journal = _journal with
            {
                SystemCaptureMode = start.SystemCaptureMode?.ToString() ?? "unavailable"
            };
            if (!_capture.MicrophoneAvailable && !_capture.SystemAvailable)
            {
                Transition(MeetingSessionTrigger.Interrupt, "capture could not resume");
                SaveJournalState(MeetingSessionState.RecoverableInterruption, "resume-failed");
                return;
            }
            Transition(
                _capture.MicrophoneAvailable && _capture.SystemAvailable
                    ? MeetingSessionTrigger.Prepared
                    : MeetingSessionTrigger.PreparedDegraded,
                "capture resumed after Windows power transition");
            AddHealthWarning("Recording resumed after a Windows power interruption.");
            SaveJournalState(State);
            _capture.StartHealthLoop(_lifecycleCancellation.Token);
            LogSessionDiagnostic("resumed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PreserveForShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRecording)
            {
                return;
            }
            StopHealthLoop();
            await CheckpointForInterruptionUnderGateAsync(
                "application shutdown",
                "Recording was interrupted by application shutdown and retained for recovery.",
                "application-shutdown").ConfigureAwait(false);
            LogSessionDiagnostic("preserved-for-shutdown");
        }
        catch (Exception exception)
        {
            _logService?.Error("Meeting recording could not be fully checkpointed during shutdown.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CancelAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!StateAllowsCancellation())
            {
                return;
            }
            StopHealthLoop();
            _lifecycleCancellation?.Cancel();
            await _capture.CancelLiveAsync().ConfigureAwait(false);
            await _capture.CancelCaptureAsync().ConfigureAwait(false);
            Transition(MeetingSessionTrigger.Cancel, "user discarded meeting recording");
            SaveJournalState(MeetingSessionState.Cancelled);
            if (!string.IsNullOrWhiteSpace(_meetingId))
            {
                _recovery.TryDeleteWorkingSession(
                    _meetingId,
                    MeetingAudioCleanupReason.Cancelled,
                    "Cancelled meeting audio deletion failed.");
            }
            _journal = null;
            LogSessionDiagnostic("cancelled");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void AcknowledgePersisted(string meetingId)
    {
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            if (!meetingId.Equals(_meetingId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Meeting persistence acknowledgement does not match the active session.");
            }
            if (State == MeetingSessionState.Finalizing)
            {
                Transition(MeetingSessionTrigger.Complete, "meeting record durably persisted");
                SaveJournalState(MeetingSessionState.Completed);
                LogSessionDiagnostic("completed");
            }
            _recovery.TryDeleteWorkingSession(
                meetingId,
                MeetingAudioCleanupReason.DurableMeetingPersisted,
                "Completed meeting journal cleanup failed.");
            _journal = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void DiscardRecoverable(string meetingId) => _recovery.DiscardRecoverable(meetingId);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        StopHealthLoop();
        _lifecycleCancellation?.Cancel();
        try
        {
            _capture.AwaitHealthLoopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        var repairGateHeld = false;
        var lifecycleGateHeld = false;
        try
        {
            _capture.WaitRepairGate();
            repairGateHeld = true;
            _gate.Wait();
            lifecycleGateHeld = true;
            _capture.DisposeCaptureAsync().GetAwaiter().GetResult();
            _capture.CancelLiveAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
        finally
        {
            if (lifecycleGateHeld)
            {
                _gate.Release();
            }
            if (repairGateHeld)
            {
                _capture.ReleaseRepairGate();
            }
        }
        _lifecycleCancellation?.Dispose();
        try
        {
            _modelWarmupTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // Finalization owns user-visible ASR initialization errors when a meeting is stopped.
        }
        _finalization.DisposeDiarization();
        _capture.Dispose();
        _gate.Dispose();
    }

    private void BeginFinalModelWarmup()
    {
        if (_modelWarmupTask is { IsCompleted: false })
        {
            return;
        }

        // The final-ASR model is first needed for a real meeting. Start loading after capture is
        // healthy so recording time can hide startup, while idle launches retain no meeting model.
        _modelWarmupTask = _transcriptionClient.InitializeAsync();
        _ = ObserveFinalModelWarmupAsync(_modelWarmupTask);
    }

    private async Task ObserveFinalModelWarmupAsync(Task<ModelOperationResult> warmup)
    {
        try
        {
            await warmup.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logService?.Error("Final meeting transcription warmup failed; finalization will retry.", exception);
        }
    }

    public static List<string> CleanupHealthWarnings(
        IEnumerable<string>? warnings,
        string? transcript = null,
        bool diarizationSucceededWithSegments = false) =>
        MeetingFinalizationPipeline.CleanupHealthWarnings(warnings, transcript, diarizationSucceededWithSegments);

    internal static Task AwaitDiarizationBeforeCleanupAsync(
        Task? diarizationTask,
        bool alreadyObserved,
        Action<Exception>? reportFailure = null) =>
        MeetingFinalizationPipeline.AwaitDiarizationBeforeCleanupAsync(diarizationTask, alreadyObserved, reportFailure);

    private MeetingRecoveryHost RecoveryHost => new(
        Transition,
        _stateMachine.CanApply,
        AddHealthWarning,
        SaveJournalState,
        LogSessionDiagnostic);

    private async Task<RecordedMeetingResult> FinalizeJournalUnderGateAsync(
        string title,
        bool recovered,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            throw new InvalidOperationException("Meeting journal is unavailable.");
        }

        var outcome = await _finalization.FinalizeAsync(new MeetingFinalizationRequest(
            _journal,
            title,
            recovered,
            _startedAt,
            CurrentHealthWarnings(),
            _capture.LiveResult,
            cancellationToken)).ConfigureAwait(false);
        _journal = outcome.Journal;
        if (outcome.TerminalState == MeetingSessionState.Failed &&
            _stateMachine.CanApply(MeetingSessionTrigger.Fail))
        {
            Transition(MeetingSessionTrigger.Fail, "no transcript was produced");
        }
        LogSessionDiagnostic(outcome.TerminalState == MeetingSessionState.Completed
            ? "finalized-awaiting-persistence"
            : "failed-no-transcript");
        return outcome.Result;
    }

    private async Task CheckpointForInterruptionUnderGateAsync(
        string reason,
        string warning,
        string failureCategory)
    {
        try
        {
            await _capture.CheckpointAllAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logService?.Info(
                $"Meeting interruption checkpoint was partial. category={exception.GetType().Name}; audioRetained={MeetingPersistenceBoundary.HasAudio(_journal)}");
        }

        if (MeetingPersistenceBoundary.HasAudio(_journal))
        {
            _recovery.PreserveInterrupted(RecoveryHost, reason, warning, failureCategory);
        }
        else
        {
            _recovery.FailWithoutAudio(RecoveryHost, reason, failureCategory);
        }
        await _capture.DisposeCaptureAsync().ConfigureAwait(false);
    }

    private void StopHealthLoop() => _lifecycleCancellation?.Cancel();

    private void AddHealthWarning(string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
        {
            return;
        }
        lock (_warningGate)
        {
            var normalized = warning.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (!_healthWarnings.Contains(normalized, StringComparer.Ordinal))
            {
                _healthWarnings.Add(normalized);
            }
        }
    }

    private List<string> CurrentHealthWarnings()
    {
        lock (_warningGate)
        {
            return _healthWarnings.ToList();
        }
    }

    private void SaveJournalState(MeetingSessionState state, string? failureCategory = null)
    {
        if (_journal is null)
        {
            return;
        }
        _journal = _persistence.ApplyState(_journal, state, CurrentHealthWarnings(), failureCategory);
        _persistence.Save(_journal);
    }

    private bool StateAllowsCancellation() => State is
        MeetingSessionState.Preparing or
        MeetingSessionState.Recording or
        MeetingSessionState.DegradedRecording or
        MeetingSessionState.Stopping or
        MeetingSessionState.Finalizing or
        MeetingSessionState.RecoverableInterruption;

    private void ResetTerminalStateIfNeeded()
    {
        if (State is MeetingSessionState.Completed or MeetingSessionState.Failed or MeetingSessionState.Cancelled)
        {
            Transition(MeetingSessionTrigger.Reset, "new meeting lifecycle");
        }
    }

    private void Transition(MeetingSessionTrigger trigger, string reason)
    {
        var transition = _stateMachine.Apply(trigger, reason);
        _logService?.Info(
            $"Meeting session transition. from={transition.From}; to={transition.To}; trigger={transition.Trigger}; reasonCategory={DiagnosticCategory(reason)}");
        StateChanged?.Invoke(this, new MeetingSessionStateChangedEventArgs(transition));
    }

    private bool TryTransition(MeetingSessionTrigger trigger, string reason)
    {
        if (!_stateMachine.TryApply(trigger, out var transition, reason) || transition is null)
        {
            return false;
        }
        _logService?.Info(
            $"Meeting session transition. from={transition.From}; to={transition.To}; trigger={transition.Trigger}; reasonCategory={DiagnosticCategory(reason)}");
        StateChanged?.Invoke(this, new MeetingSessionStateChangedEventArgs(transition));
        return true;
    }

    private void LogSessionDiagnostic(string outcome)
    {
        _logService?.Info(
            $"Meeting session diagnostic. outcome={outcome}; state={State}; model={_transcriptionClient.ModelId}; captureMode={_journal?.SystemCaptureMode ?? "none"}; micParts={_journal?.MicrophoneParts.Count ?? 0}; systemParts={_journal?.SystemParts.Count ?? 0}; micRepairAttempts={_journal?.MicrophoneRepairAttempts ?? 0}; systemRepairAttempts={_journal?.SystemRepairAttempts ?? 0}; warnings={CurrentHealthWarnings().Count}; targeted={_targetProcessId is > 0}; recovered={_journal?.RecoveredFromInterruption ?? false}");
    }

    private static string DiagnosticCategory(string value) =>
        MeetingFinalizationPipeline.DiagnosticCategory(value);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed record MeetingStartResult(
    string MeetingId,
    MeetingSessionState State,
    SystemAudioCaptureMode? SystemCaptureMode,
    string? Warning,
    bool MicrophoneStarted,
    bool SystemAudioStarted);

public sealed class MeetingSessionStateChangedEventArgs(MeetingSessionTransition transition) : EventArgs
{
    public MeetingSessionTransition Transition { get; } = transition;
}

public sealed class MeetingSessionRecoverableException(
    string message,
    string sessionId,
    Exception innerException) : Exception(message, innerException)
{
    public string SessionId { get; } = sessionId;
}
