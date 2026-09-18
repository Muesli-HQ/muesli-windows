namespace Muesli.Windows.Services;

internal sealed record CapturePairStartResult(
    SystemAudioCaptureMode? SystemCaptureMode,
    string? Warning,
    Exception? MicrophoneFailure,
    Exception? SystemFailure);

internal sealed record MeetingCaptureRepairHost(
    Func<bool> IsRecording,
    Func<MeetingSessionState> State,
    Func<MeetingSessionJournal?> Journal,
    Action<MeetingSessionJournal> SetJournal,
    Action<string> AddWarning,
    Action<MeetingSessionTrigger, string> Transition,
    Func<MeetingSessionTrigger, string, bool> TryTransition,
    Action<MeetingSessionState, string?> SaveState,
    Action<string> LogDiagnostic,
    SemaphoreSlim LifecycleGate,
    Func<CancellationToken> LifecycleToken);

/// <summary>
/// Microphone and system capture, live-stream ownership, and route health. Callbacks enqueue bounded
/// work on thread-pool tasks and never dispatch to the WPF UI thread.
/// </summary>
internal sealed class MeetingCaptureSession : IDisposable
{
    private readonly MeetingPersistenceBoundary _persistence;
    private readonly AppLogService? _logService;
    private readonly object _liveCheckpointGate = new();
    private readonly SemaphoreSlim _repairGate = new(1, 1);
    private IReadOnlyList<LiveTranscriptSegment>? _pendingLiveCheckpoint;
    private int _liveCheckpointScheduled;
    private long? _micPartStartedAtMs;
    private long? _systemPartStartedAtMs;
    private AudioCaptureService? _micCapture;
    private SystemAudioCaptureService? _systemCapture;
    private MeetingAudioHealthMonitor? _healthMonitor;
    private MeetingLiveTranscriptionSession? _liveSession;
    private MeetingLiveTranscriptionResult? _liveResult;
    private LiveTranscriptionConfiguration? _liveConfiguration;
    private Task? _healthLoop;
    private DateTime _startedAt;
    private string? _microphoneName;
    private int? _targetProcessId;
    private bool _micAvailable;
    private bool _systemAvailable;
    private int _disposed;
    private MeetingCaptureRepairHost? _repairHost;

    public MeetingCaptureSession(MeetingPersistenceBoundary persistence, AppLogService? logService = null)
    {
        _persistence = persistence;
        _logService = logService;
    }

    public event EventHandler<MeetingAudioHealthChangedEventArgs>? HealthChanged;
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;
    public event EventHandler<LiveTranscriptSnapshot>? LiveTranscriptChanged;
    public event EventHandler<Exception>? LiveTranscriptionFailed;

    public bool MicrophoneAvailable => _micAvailable;
    public bool SystemAvailable => _systemAvailable;
    public MeetingLiveTranscriptionResult? LiveResult => _liveResult;

    public void BindRepairHost(MeetingCaptureRepairHost host) => _repairHost = host;

    public void Prepare(
        DateTime startedAt,
        string? microphoneName,
        int? targetProcessId,
        LiveTranscriptionConfiguration? liveConfiguration,
        bool resetLiveResult = true,
        DateTimeOffset? healthAnchor = null)
    {
        _startedAt = startedAt;
        _microphoneName = string.IsNullOrWhiteSpace(microphoneName)
            ? AudioCaptureService.SystemDefaultMicrophone
            : microphoneName;
        _targetProcessId = targetProcessId is > 0 ? targetProcessId : null;
        _liveConfiguration = liveConfiguration;
        if (resetLiveResult)
        {
            _liveResult = null;
        }
        _healthMonitor = new MeetingAudioHealthMonitor(healthAnchor ?? new DateTimeOffset(startedAt));
        _micAvailable = false;
        _systemAvailable = false;
        _micPartStartedAtMs = null;
        _systemPartStartedAtMs = null;
    }

    public void RestoreLiveResult(MeetingLiveTranscriptionResult? liveResult) => _liveResult = liveResult;

    public async Task StartLiveAsync(CancellationToken cancellationToken)
    {
        if (_liveConfiguration is null || _liveSession is not null)
        {
            return;
        }
        try
        {
            var model = StreamingModelCatalog.GetRequired(_liveConfiguration.ModelId);
            _liveSession = await Task.Run(
                () => new MeetingLiveTranscriptionSession(model, _liveConfiguration.OwnershipMode),
                cancellationToken).ConfigureAwait(false);
            _liveSession.SnapshotChanged += OnLiveTranscriptSnapshot;
            _liveSession.Failed += OnLiveTranscriptionFailed;
        }
        catch (Exception exception)
        {
            if (_liveConfiguration.OwnershipMode == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal)
            {
                _liveResult = new MeetingLiveTranscriptionResult(
                    [],
                    [
                        new LiveTranscriptGap(LiveTranscriptChannel.Microphone, 0, long.MaxValue, "streaming-engine-unavailable"),
                        new LiveTranscriptGap(LiveTranscriptChannel.System, 0, long.MaxValue, "streaming-engine-unavailable")
                    ],
                    0,
                    _liveConfiguration.ModelId,
                    LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
            }
            _repairHost?.AddWarning("Live transcription could not start; retained audio will still be finalized locally.");
            _logService?.Info($"Live transcription unavailable. model={_liveConfiguration.ModelId}; category={exception.GetType().Name}");
            LiveTranscriptionFailed?.Invoke(this, exception);
        }
    }

    public async Task<CapturePairStartResult> StartPairAsync(
        MeetingSessionJournal journal,
        CancellationToken cancellationToken)
    {
        var sessionDirectory = _persistence.GetSessionDirectory(journal.SessionId);
        _micCapture = new AudioCaptureService(
            sessionDirectory,
            MeetingSessionJournalStore.MicrophoneCapturePrefix,
            cleanupInterruptedDictation: false);
        _systemCapture = new SystemAudioCaptureService(
            sessionDirectory,
            MeetingSessionJournalStore.SystemCapturePrefix);
        SubscribeCaptureEvents();

        Exception? micFailure = null;
        Exception? systemFailure = null;
        SystemAudioCaptureStartResult? systemStart = null;
        var micTask = Task.Run(async () =>
        {
            try
            {
                await _micCapture.StartAsync(_microphoneName).ConfigureAwait(false);
                _micPartStartedAtMs = ElapsedMeetingMs();
                _micAvailable = true;
            }
            catch (Exception exception)
            {
                micFailure = exception;
                _micAvailable = false;
            }
        }, cancellationToken);
        var systemTask = Task.Run(async () =>
        {
            try
            {
                systemStart = await _systemCapture.StartAsync(_targetProcessId, cancellationToken).ConfigureAwait(false);
                _systemPartStartedAtMs = ElapsedMeetingMs();
                _systemAvailable = true;
            }
            catch (Exception exception)
            {
                systemFailure = exception;
                _systemAvailable = false;
            }
        }, cancellationToken);
        await Task.WhenAll(micTask, systemTask).ConfigureAwait(false);

        if (micFailure is not null)
        {
            _repairHost?.AddWarning("Microphone audio could not start; your side may be missing.");
        }
        if (systemFailure is not null)
        {
            _repairHost?.AddWarning(MeetingFinalizationPipeline.SystemAudioMissingWarning);
        }
        if (!string.IsNullOrWhiteSpace(systemStart?.Warning))
        {
            _repairHost?.AddWarning(systemStart.Warning);
        }

        return new CapturePairStartResult(
            systemStart?.Mode,
            systemStart?.Warning,
            micFailure,
            systemFailure);
    }

    public async Task CheckpointAllAsync()
    {
        await CheckpointChannelAsync(MeetingAudioChannel.Microphone).ConfigureAwait(false);
        await CheckpointChannelAsync(MeetingAudioChannel.System).ConfigureAwait(false);
        await FinishLiveAsync().ConfigureAwait(false);
        if (!MeetingPersistenceBoundary.HasAudio(_repairHost?.Journal()))
        {
            throw new InvalidOperationException("Meeting capture produced no recoverable audio track.");
        }
    }

    public async Task CheckpointChannelAsync(MeetingAudioChannel channel)
    {
        var journal = _repairHost?.Journal();
        if (journal is null)
        {
            return;
        }
        CapturedAudio? audio = null;
        try
        {
            audio = channel == MeetingAudioChannel.Microphone
                ? _micCapture is null
                    ? null
                    : await _micCapture.StopAsync(keepLatestDictationAlias: false).ConfigureAwait(false)
                : _systemCapture is null
                    ? null
                    : await _systemCapture.StopAsync().ConfigureAwait(false);
            if (audio is not null && audio.ByteLength > 44)
            {
                var anchor = channel == MeetingAudioChannel.Microphone ? _micPartStartedAtMs : _systemPartStartedAtMs;
                journal = _persistence.AppendPart(journal, channel, audio.TranscriptionPath, anchor);
                _repairHost?.SetJournal(journal);
            }
        }
        catch (Exception exception)
        {
            _repairHost?.AddWarning(channel == MeetingAudioChannel.Microphone
                ? "Microphone audio could not be finalized; your side may be incomplete."
                : MeetingFinalizationPipeline.SystemAudioMissingWarning);
            _logService?.Info($"Meeting channel checkpoint failed. channel={channel}; category={exception.GetType().Name}");
        }
        finally
        {
            audio?.Dispose();
            if (channel == MeetingAudioChannel.Microphone)
            {
                _micAvailable = false;
                _micPartStartedAtMs = null;
            }
            else
            {
                _systemAvailable = false;
                _systemPartStartedAtMs = null;
            }
        }
    }

    public void StartHealthLoop(CancellationToken token)
    {
        _healthLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    var snapshot = _healthMonitor?.Evaluate(DateTimeOffset.UtcNow);
                    if (snapshot is null)
                    {
                        continue;
                    }
                    foreach (var warning in snapshot.Warnings)
                    {
                        _repairHost?.AddWarning(warning);
                    }
                    HealthChanged?.Invoke(this, new MeetingAudioHealthChangedEventArgs(snapshot));
                    var host = _repairHost;
                    if (snapshot.IsDegraded && host?.State() == MeetingSessionState.Recording)
                    {
                        host.TryTransition(MeetingSessionTrigger.Degrade, "audio health warning");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    public Task AwaitHealthLoopAsync() => _healthLoop ?? Task.CompletedTask;

    public async Task CancelCaptureAsync()
    {
        try
        {
            if (_micCapture is not null)
            {
                await _micCapture.CancelAsync().ConfigureAwait(false);
            }
        }
        catch
        {
        }
        try
        {
            if (_systemCapture is not null)
            {
                await _systemCapture.CancelAsync().ConfigureAwait(false);
            }
        }
        catch
        {
        }
        await DisposeCaptureAsync().ConfigureAwait(false);
    }

    public async Task CancelLiveAsync()
    {
        var session = Interlocked.Exchange(ref _liveSession, null);
        if (session is null)
        {
            return;
        }
        session.SnapshotChanged -= OnLiveTranscriptSnapshot;
        session.Failed -= OnLiveTranscriptionFailed;
        session.Cancel();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    public Task DisposeCaptureAsync()
    {
        UnsubscribeCaptureEvents();
        _micCapture?.Dispose();
        _micCapture = null;
        _systemCapture?.Dispose();
        _systemCapture = null;
        return Task.CompletedTask;
    }

    public async Task RepairChannelAsync(MeetingAudioChannel channel, Exception? failure)
    {
        if (Volatile.Read(ref _disposed) != 0 || _repairHost is null)
        {
            return;
        }
        await _repairGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            for (var completedAttempts = 0; ; completedAttempts++)
            {
                var peerAvailable = channel == MeetingAudioChannel.Microphone ? _systemAvailable : _micAvailable;
                var decision = MeetingCaptureRepairPolicy.Decide(completedAttempts, peerAvailable);
                if (decision.Action == MeetingCaptureRepairAction.ContinueDegraded)
                {
                    _repairHost.AddWarning($"{ChannelLabel(channel)} could not restart after {completedAttempts} attempts; the other channel is still recording.");
                    _repairHost.SaveState(MeetingSessionState.DegradedRecording, $"{channel}-repair-exhausted");
                    return;
                }
                if (decision.Action == MeetingCaptureRepairAction.PreserveForRecovery)
                {
                    await _repairHost.LifecycleGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_repairHost.IsRecording())
                        {
                            try
                            {
                                await CheckpointAllAsync().ConfigureAwait(false);
                            }
                            catch when (MeetingPersistenceBoundary.HasAudio(_repairHost.Journal()))
                            {
                            }
                            if (MeetingPersistenceBoundary.HasAudio(_repairHost.Journal()))
                            {
                                _repairHost.Transition(MeetingSessionTrigger.Interrupt, "both capture channels unavailable");
                                _repairHost.SaveState(MeetingSessionState.RecoverableInterruption, "capture-repair-exhausted");
                            }
                            else
                            {
                                _repairHost.Transition(MeetingSessionTrigger.Fail, "capture repair exhausted without audio");
                                _repairHost.SaveState(MeetingSessionState.Failed, "capture-repair-no-audio");
                            }
                        }
                    }
                    finally
                    {
                        _repairHost.LifecycleGate.Release();
                    }
                    return;
                }

                if (decision.Delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(decision.Delay, _repairHost.LifecycleToken()).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                await _repairHost.LifecycleGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!_repairHost.IsRecording() || _repairHost.Journal() is null)
                    {
                        return;
                    }
                    MarkChannelUnavailable(channel);
                    if (_repairHost.State() == MeetingSessionState.Recording)
                    {
                        _repairHost.Transition(MeetingSessionTrigger.Degrade, $"{channel} capture fault");
                    }
                    _repairHost.AddWarning($"{ChannelLabel(channel)} capture was interrupted; Muesli is attempting a loss-bounded restart.");
                    await CheckpointChannelAsync(channel).ConfigureAwait(false);
                    try
                    {
                        if (channel == MeetingAudioChannel.Microphone && _micCapture is not null)
                        {
                            await _micCapture.StartAsync(_microphoneName).ConfigureAwait(false);
                            _micPartStartedAtMs = ElapsedMeetingMs();
                            _micAvailable = true;
                        }
                        else if (channel == MeetingAudioChannel.System && _systemCapture is not null)
                        {
                            var result = await _systemCapture.StartAsync(_targetProcessId).ConfigureAwait(false);
                            _systemPartStartedAtMs = ElapsedMeetingMs();
                            _systemAvailable = true;
                            if (!string.IsNullOrWhiteSpace(result.Warning))
                            {
                                _repairHost.AddWarning(result.Warning);
                            }
                        }
                        UpdateRepairAttempts(channel, decision.Attempt);
                        TryRestoreHealthyState($"{channel} capture restarted");
                        _repairHost.SaveState(_repairHost.State(), null);
                        _repairHost.LogDiagnostic("capture-repaired");
                        return;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        UpdateRepairAttempts(channel, decision.Attempt);
                        _logService?.Info(
                            $"Meeting capture repair failed. channel={channel}; attempt={decision.Attempt}; category={exception.GetType().Name}");
                    }
                }
                finally
                {
                    _repairHost.LifecycleGate.Release();
                }
            }
        }
        finally
        {
            _repairGate.Release();
        }
    }

    public void WaitRepairGate() => _repairGate.Wait();
    public void ReleaseRepairGate() => _repairGate.Release();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _repairGate.Dispose();
    }

    internal static MeetingLiveTranscriptionResult MergeLiveResults(
        MeetingLiveTranscriptionResult? prior,
        MeetingLiveTranscriptionResult current)
    {
        if (prior is null)
        {
            return current;
        }
        var finiteGapEnds = prior.Gaps.Where(gap => gap.EndSample != long.MaxValue).Select(gap => gap.EndSample);
        var offset = prior.Committed.Select(segment => segment.EndSample).Concat(finiteGapEnds).DefaultIfEmpty(0).Max();
        var shiftedSegments = current.Committed.Select(segment => segment with
        {
            Id = $"resume_{prior.Committed.Count}_{segment.Id}",
            StartSample = segment.StartSample + offset,
            EndSample = segment.EndSample + offset
        });
        var shiftedGaps = current.Gaps.Select(gap => gap with
        {
            StartSample = gap.StartSample + offset,
            EndSample = gap.EndSample == long.MaxValue ? long.MaxValue : gap.EndSample + offset
        });
        return current with
        {
            Committed = prior.Committed.Concat(shiftedSegments).ToList(),
            Gaps = MeetingLiveTranscriptionSession.CoalesceGaps(prior.Gaps.Concat(shiftedGaps)),
            DroppedPackets = prior.DroppedPackets + current.DroppedPackets
        };
    }

    private async Task FinishLiveAsync()
    {
        var session = Interlocked.Exchange(ref _liveSession, null);
        if (session is null)
        {
            return;
        }
        session.SnapshotChanged -= OnLiveTranscriptSnapshot;
        session.Failed -= OnLiveTranscriptionFailed;
        try
        {
            var current = await session.FinishAsync(CancellationToken.None).ConfigureAwait(false);
            _liveResult = MergeLiveResults(_liveResult, current);
            var journal = _repairHost?.Journal();
            if (journal is not null)
            {
                journal = journal with
                {
                    LiveDroppedPacketCount = _liveResult.DroppedPackets,
                    LiveTranscriptGaps = _liveResult.Gaps.ToList(),
                    LiveTranscriptSegments = _liveResult.Committed.ToList()
                };
                _persistence.Save(journal);
                _repairHost?.SetJournal(journal);
            }
            _logService?.Info($"Live transcription finalized. model={_liveResult.ModelId}; ownership={_liveResult.OwnershipMode}; segments={_liveResult.Committed.Count}; gaps={_liveResult.Gaps.Count}; dropped={_liveResult.DroppedPackets}; transcriptLogged=false");
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SubscribeCaptureEvents()
    {
        if (_micCapture is not null)
        {
            _micCapture.RouteChanged += OnMicrophoneRouteChanged;
            _micCapture.MetricsAvailable += OnAudioMetrics;
            _micCapture.CaptureFaulted += OnCaptureFaulted;
            _micCapture.LevelChanged += OnMicrophoneLevelChanged;
            _micCapture.PcmSamplesAvailable += OnLivePcmSamples;
        }
        if (_systemCapture is not null)
        {
            _systemCapture.RouteChanged += OnSystemRouteChanged;
            _systemCapture.MetricsAvailable += OnAudioMetrics;
            _systemCapture.CaptureFaulted += OnCaptureFaulted;
            _systemCapture.PcmSamplesAvailable += OnLivePcmSamples;
        }
    }

    private void UnsubscribeCaptureEvents()
    {
        if (_micCapture is not null)
        {
            _micCapture.RouteChanged -= OnMicrophoneRouteChanged;
            _micCapture.MetricsAvailable -= OnAudioMetrics;
            _micCapture.CaptureFaulted -= OnCaptureFaulted;
            _micCapture.LevelChanged -= OnMicrophoneLevelChanged;
            _micCapture.PcmSamplesAvailable -= OnLivePcmSamples;
        }
        if (_systemCapture is not null)
        {
            _systemCapture.RouteChanged -= OnSystemRouteChanged;
            _systemCapture.MetricsAvailable -= OnAudioMetrics;
            _systemCapture.CaptureFaulted -= OnCaptureFaulted;
            _systemCapture.PcmSamplesAvailable -= OnLivePcmSamples;
        }
    }

    private long ElapsedMeetingMs() => Math.Max(0, (long)(DateTime.Now - _startedAt).TotalMilliseconds);

    private void OnMicrophoneLevelChanged(object? sender, AudioLevelEventArgs e) =>
        LevelChanged?.Invoke(this, e);

    private void OnLivePcmSamples(object? sender, LivePcmSamplesEventArgs e) => _liveSession?.TryEnqueue(e);

    private void OnLiveTranscriptSnapshot(object? sender, LiveTranscriptSnapshot snapshot)
    {
        lock (_liveCheckpointGate)
        {
            _pendingLiveCheckpoint = snapshot.Committed;
        }
        if (Interlocked.Exchange(ref _liveCheckpointScheduled, 1) == 0)
        {
            _ = Task.Run(FlushLiveCheckpointAsync);
        }
        LiveTranscriptChanged?.Invoke(this, snapshot);
    }

    private async Task FlushLiveCheckpointAsync()
    {
        var host = _repairHost;
        if (host is null)
        {
            return;
        }
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            await host.LifecycleGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            Interlocked.Exchange(ref _liveCheckpointScheduled, 0);
            IReadOnlyList<LiveTranscriptSegment>? pending;
            lock (_liveCheckpointGate)
            {
                pending = _pendingLiveCheckpoint;
                _pendingLiveCheckpoint = null;
            }
            var journal = host.Journal();
            if (pending is null || journal is null || pending.Count <= journal.LiveTranscriptSegments.Count)
            {
                return;
            }
            journal = journal with { LiveTranscriptSegments = pending.ToList() };
            _persistence.Save(journal);
            host.SetJournal(journal);
        }
        catch (Exception exception)
        {
            _logService?.Info($"Live transcript checkpoint failed. category={exception.GetType().Name}; transcriptLogged=false");
        }
        finally
        {
            host.LifecycleGate.Release();
        }
    }

    private void OnLiveTranscriptionFailed(object? sender, Exception exception)
    {
        _repairHost?.AddWarning("Live transcription stopped; retained audio remains available for final transcription and measured gap recovery.");
        _logService?.Info($"Live transcription stopped. category={exception.GetType().Name}; transcriptLogged=false");
        LiveTranscriptionFailed?.Invoke(this, exception);
    }

    private void OnAudioMetrics(object? sender, MeetingAudioMetrics metrics) =>
        _healthMonitor?.Note(metrics);

    private void OnMicrophoneRouteChanged(object? sender, AudioRouteChangedEventArgs args)
    {
        if (args.Kind == AudioRouteChangeKind.Failed)
        {
            _ = RepairChannelAsync(MeetingAudioChannel.Microphone, args.Exception);
            return;
        }
        _repairHost?.AddWarning(args.Message);
        if (args.Kind == AudioRouteChangeKind.Recovered)
        {
            _micAvailable = true;
            TryRestoreHealthyState("microphone route recovered");
        }
    }

    private void OnSystemRouteChanged(object? sender, AudioRouteChangedEventArgs args)
    {
        if (args.Kind == AudioRouteChangeKind.Failed)
        {
            _ = RepairChannelAsync(MeetingAudioChannel.System, args.Exception);
            return;
        }
        _repairHost?.AddWarning(args.Message);
        if (args.Kind == AudioRouteChangeKind.Recovered)
        {
            _systemAvailable = true;
            TryRestoreHealthyState("system route recovered");
        }
    }

    private void OnCaptureFaulted(object? sender, CaptureFaultedEventArgs args) =>
        _ = RepairChannelAsync(args.Channel, args.Exception);

    private void MarkChannelUnavailable(MeetingAudioChannel channel)
    {
        if (channel == MeetingAudioChannel.Microphone)
        {
            _micAvailable = false;
        }
        else
        {
            _systemAvailable = false;
        }
    }

    private void UpdateRepairAttempts(MeetingAudioChannel channel, int attempts)
    {
        var journal = _repairHost?.Journal();
        if (journal is null)
        {
            return;
        }
        journal = channel == MeetingAudioChannel.Microphone
            ? journal with { MicrophoneRepairAttempts = attempts }
            : journal with { SystemRepairAttempts = attempts };
        _repairHost?.SetJournal(journal);
    }

    private void TryRestoreHealthyState(string reason)
    {
        var host = _repairHost;
        if (_micAvailable && _systemAvailable && host?.State() == MeetingSessionState.DegradedRecording)
        {
            host.TryTransition(MeetingSessionTrigger.Recover, reason);
        }
    }

    private static string ChannelLabel(MeetingAudioChannel channel) =>
        channel == MeetingAudioChannel.Microphone ? "Microphone" : "System audio";
}
