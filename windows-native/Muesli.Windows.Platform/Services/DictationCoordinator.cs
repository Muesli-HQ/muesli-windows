using System.Diagnostics;
using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Services;

public sealed class DictationCoordinator : IDisposable
{
    // Cohere peaked at 3.4 GiB in the local rolling benchmark; leave room for the shell and capture.
    private const ulong CohereMinimumFreeMemoryBytes = 4UL * 1024 * 1024 * 1024;
    // The recognizer stays loaded once warm. Releasing it after idle made the next dictation a cold
    // start (sub-6 s Parakeet dictations measured a 3.5 s median; first chunks after a release took
    // 4-9 s). Under memory pressure Windows pages the idle weights out instead, and the hotkey-down
    // refresh in NativeParakeetClient pulls them back while the user is still speaking.

    // Long enough that loading the model does not compete with sign-in and app start-up.
    private static readonly TimeSpan PreloadDelay = TimeSpan.FromSeconds(20);
    private readonly NativeTranscriptionClient _transcriptionClient;
    private readonly AppLogService _log = new();
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private int _cancelRequested;
    private DictationSessionKind _sessionKind;
    private Task<TranscriptionResult>? _activeTranscriptionTask;
    private Task<ModelOperationResult>? _modelWarmupTask;
    private CancellationTokenSource? _sessionCancellation;
    private DictationRollingTranscriber? _rollingTranscriber;
    private IHostedDictationSession? _hostedSession;
    private string? _microphoneName;
    private volatile bool _prepareEnabled;
    private int _preloadScheduled;
    private IDisposable? _performanceBoost;
    // Available physical memory read when the current dictation started; null when the read failed.
    private ulong? _startAvailableMemory;
    private Task<ILiveVad>? _pauseDetectorTask;
    private readonly object _pauseDetectorGate = new();
    // Completes when the dictation that last used the pause detector has stopped touching it.
    // A cancelled stop leaves its finish running after the gate is released, so the next
    // dictation must not reset the detector underneath it.
    private Task _pauseDetectorLease = Task.CompletedTask;

    public bool IsRecording { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsTranscribing { get; private set; }
    public string EngineId => _transcriptionClient.EngineId;
    public string ModelId => _transcriptionClient.ModelId;
    public string ModelDisplayName => _transcriptionClient.ModelDisplayName;
    public bool IsModelReady => _transcriptionClient.IsSelectedModelReady;
    public CaptureCleanupResult StartupCaptureCleanup => _audioCaptureService.StartupCleanupResult;
    public SoundFeedbackService SoundFeedback { get; }

    public DictationCoordinator(
        NativeTranscriptionClient? transcriptionClient = null,
        SoundFeedbackService? soundFeedback = null)
    {
        _transcriptionClient = transcriptionClient ?? new NativeTranscriptionClient();
        SoundFeedback = soundFeedback ?? new SoundFeedbackService(
            new DictationCuePlayer(
                new WindowsSystemSoundPlayer(report: (message, exception) => _log.Error(message, exception)),
                (message, exception) => _log.Error(message, exception)),
            report: _log.Info);
        // Load the cue files now; the first cue otherwise reads its file on the key-down path.
        _ = Task.Run(SoundFeedback.Prewarm);
        _audioCaptureService.DeviceListChanged += OnDeviceListChanged;
        _audioCaptureService.RouteChanged += OnRouteChanged;
        _audioCaptureService.LevelChanged += OnLevelChanged;
    }

    public event EventHandler? DeviceListChanged;
    public event EventHandler<AudioRouteChangedEventArgs>? RouteChanged;
    public event EventHandler<AudioLevelEventArgs>? LevelChanged;

    public IReadOnlyList<string> ListMicrophones() => _audioCaptureService.ListCaptureDevices();
    public string PickPreferredMicrophone() => _audioCaptureService.PickPreferredDeviceName();

    /// <summary>
    /// Readies the microphone client for the next dictation without starting capture, and keeps
    /// re-preparing it after each dictation. Callers must only opt in once the user has finished
    /// onboarding; until then the microphone is opened strictly on demand.
    /// </summary>
    public void PrepareMicrophone(string? microphoneName)
    {
        _prepareEnabled = true;
        PrepareInBackground(microphoneName);
        PreloadModelOnce();
    }

    /// <summary>
    /// Loads the dictation model shortly after launch so the first dictation is not a cold start.
    /// Runs once, from the same post-onboarding opt-in that readies the microphone.
    /// </summary>
    private void PreloadModelOnce()
    {
        if (Interlocked.Exchange(ref _preloadScheduled, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PreloadDelay).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) != 0 || !IsModelReady || IsRecording || IsBusy) return;
                BeginModelWarmup();
                await _modelWarmupTask!.ConfigureAwait(false);
                _log.Info($"Dictation model preloaded. model={ModelId}");
                // So the first low-memory or hands-free dictation already cuts at pauses.
                ReadyPauseDetector();
            }
            catch (Exception exception) when (Volatile.Read(ref _disposed) == 0)
            {
                // The next dictation loads the model on demand and owns user-visible errors.
                _log.Error("Dictation model preload failed; it will load on first use.", exception);
            }
            catch { }
        });
    }

    /// <summary>
    /// Readies the microphone client when the dictation key goes down, so the hold threshold is
    /// spent initializing the device instead of waiting. Nothing is captured until
    /// <see cref="StartAsync(string?)"/>, and an already prepared client makes this a no-op.
    /// </summary>
    public void PrimeMicrophone(string? microphoneName)
    {
        if (!IsRecording && !IsBusy) PrepareInBackground(microphoneName);
    }

    private void RePrepareMicrophone()
    {
        if (_prepareEnabled) PrepareInBackground(_microphoneName);
    }

    private void PrepareInBackground(string? microphoneName)
    {
        _microphoneName = microphoneName;
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _audioCaptureService.PrepareAsync(microphoneName).ConfigureAwait(false);
            }
            catch (Exception exception) when (Volatile.Read(ref _disposed) == 0)
            {
                // Start opens the microphone on demand and owns user-visible errors.
                _log.Error("Microphone prepare failed; dictation will open it on demand.", exception);
            }
            catch { }
        });
    }

    public Task StartAsync(string? microphoneName) =>
        StartAsync(microphoneName, DictationSessionKind.Interactive);

    /// <param name="decodeDuringCapture">
    /// Decode chunks while the microphone is still open. Off by default: a held dictation is
    /// transcribed once, from the finished recording, as on macOS. Hands-free sessions opt in
    /// because they can run for minutes and would otherwise decode it all after the stop.
    /// </param>
    public async Task StartAsync(
        string? microphoneName,
        DictationSessionKind sessionKind,
        bool decodeDuringCapture = false)
    {
        _microphoneName = microphoneName;
        // The global hotkey outlives this coordinator during shutdown. Without this guard every
        // keypress awaits a disposed semaphore and logs an ObjectDisposedException.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        var startTimer = Stopwatch.StartNew();
        await _gate.WaitAsync();
        var gateMs = startTimer.ElapsedMilliseconds;
        try
        {
            if (IsRecording)
            {
                return;
            }

            IsBusy = true;
            Interlocked.Exchange(ref _cancelRequested, 0);
            _sessionCancellation?.Dispose();
            _sessionCancellation = new CancellationTokenSource();
            _sessionKind = sessionKind;
            if (Volatile.Read(ref _cancelRequested) != 0 || _sessionCancellation.IsCancellationRequested)
            {
                return;
            }

            var availableMemory = AvailablePhysicalMemory();
            _startAvailableMemory = availableMemory;
            // An unreadable value never triggers the fallback, as before.
            var freeMemory = availableMemory ?? ulong.MaxValue;
            if (ShouldUseParakeetFallback(_transcriptionClient.SelectedModel.Kind,
                TranscriptionLanguageSelection.Resolve(_transcriptionClient.SelectedModel), freeMemory,
                TranscriptionModelReadiness.IsVerified(TranscriptionModelCatalog.GetRequired("parakeet-v3"))))
            {
                await _transcriptionClient.SwitchModelAsync("parakeet-v3");
                new AppLogService().Info(
                    $"Dictation used Parakeet fallback for low memory. availableMiB={freeMemory / 1048576}; requested=cohere-transcribe-int8-en");
            }

            // Held from capture start so the warm-up and any chunk decodes are boosted too.
            _performanceBoost ??= TranscriptionPerformance.Boost();
            // With little memory free the idle model is likely paged out. Decoding while the user
            // speaks spreads the cost of paging it back in over the recording instead of leaving
            // it all for after release.
            var decodeReason = decodeDuringCapture ? "requested" : "default";
            // A hosted provider transcribes its own dictation; nothing is decoded locally for it.
            if (!decodeDuringCapture && sessionKind == DictationSessionKind.Interactive &&
                Volatile.Read(ref _hostedSession) is null &&
                DictationMemoryPolicy.ShouldDecodeDuringCapture(availableMemory))
            {
                decodeDuringCapture = true;
                decodeReason = "low-memory";
            }
            if (decodeDuringCapture)
            {
                // Never waited for: a detector that is not ready yet means this dictation is
                // cut only at the length limit, and the next one gets pause cuts.
                // Nor is it taken while an earlier dictation still holds it.
                var detector = _pauseDetectorLease.IsCompleted ? ReadyPauseDetector() : null;
                _rollingTranscriber = new DictationRollingTranscriber(_transcriptionClient, detector);
                if (detector is not null) _pauseDetectorLease = _rollingTranscriber.DetectorReleased;
                _audioCaptureService.PcmSamplesAvailable += _rollingTranscriber.Feed;
            }
            var preCaptureMs = startTimer.ElapsedMilliseconds;
            try
            {
                await _audioCaptureService.StartAsync(microphoneName);
                _log.Info($"Dictation capture opened. gateMs={gateMs}; preCaptureMs={preCaptureMs - gateMs}; " +
                    $"captureOpenMs={startTimer.ElapsedMilliseconds - preCaptureMs}; {_audioCaptureService.LastStartTiming}; " +
                    $"availableMiB={DictationMemoryPolicy.Describe(availableMemory)}; " +
                    $"lowMemory={DictationMemoryPolicy.IsLow(availableMemory)}; " +
                    $"decode={(decodeDuringCapture ? "during-capture" : "batch")}; decodeReason={decodeReason}; " +
                    $"cuts={(_rollingTranscriber is null ? "none" : _rollingTranscriber.CutsAtPauses ? "pauses" : "limit-only")}");
            }
            catch
            {
                // Drained, not just detached, so the pause detector is handed back.
                if (DetachRollingTranscriber() is { } unstarted) await unstarted.DrainAsync();
                throw;
            }
            if (Volatile.Read(ref _cancelRequested) != 0 || _sessionCancellation.IsCancellationRequested)
            {
                await _audioCaptureService.CancelAsync();
                if (DetachRollingTranscriber() is { } cancelledAtStart) await cancelledAtStart.DrainAsync();
                return;
            }

            IsRecording = true;
            // Only once the microphone is open, so the cue means "speak now".
            SoundFeedback.PlayDictationStart(sessionKind);
            BeginModelWarmup();
        }
        finally
        {
            if (!IsRecording) EndPerformanceBoost();
            IsBusy = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// The speech detector that places chunk cuts at pauses, if it has finished loading. It is
    /// the same Silero model the meeting preview uses, shared in the model cache; preparing it
    /// downloads the file once if it is missing. A failed attempt is retried on a later dictation.
    /// </summary>
    private ILiveVad? ReadyPauseDetector()
    {
        // The preload and the first dictation can both arrive here; only one load may start.
        lock (_pauseDetectorGate)
        {
            return ReadyPauseDetectorLocked();
        }
    }

    private ILiveVad? ReadyPauseDetectorLocked()
    {
        var pending = _pauseDetectorTask;
        if (pending is { IsCompletedSuccessfully: true }) return pending.Result;
        if (pending is null || pending.IsCompleted)
        {
            _pauseDetectorTask = Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var path = await StreamingModelInstaller.PrepareMeetingVadAsync(timeout.Token).ConfigureAwait(false);
                ILiveVad detector = new NativeSileroVad(path);
                _log.Info("Dictation pause detector ready.");
                return detector;
            });
            _ = _pauseDetectorTask.ContinueWith(
                failed => _log.Info($"Dictation pause detector unavailable; chunks are cut at the length limit only. category={failed.Exception?.GetBaseException().GetType().Name}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return null;
    }

    private void EndPerformanceBoost()
    {
        _performanceBoost?.Dispose();
        _performanceBoost = null;
    }

    public async Task<TranscriptionResult> StopAsync()
    {
        var stopResult = await StopAsync(
            $"aux-{Guid.NewGuid().ToString("N")[..8]}");
        return stopResult.Transcription;
    }

    /// <summary>Non-retaining setup path: transcribes a temporary capture without updating the benchmark alias.</summary>
    public async Task<TranscriptionResult> StopForOnboardingTestAsync(CancellationToken cancellationToken)
    {
        var stopResult = await StopAsync($"setup-{Guid.NewGuid().ToString("N")[..8]}", cancellationToken, keepLatestDictationAlias: false);
        return stopResult.Transcription;
    }

    public async Task<DictationStopResult> StopAsync(string traceId)
    {
        return await StopAsync(traceId, CancellationToken.None);
    }

    public async Task<DictationStopResult> StopAsync(string traceId, CancellationToken cancellationToken)
    {
        return await StopAsync(traceId, cancellationToken, keepLatestDictationAlias: true);
    }

    /// <summary>
    /// Stops a dictation. With <paramref name="keepLatestDictationAlias"/> false (privacy mode, setup
    /// tests) the bounded latest-dictation benchmark copy of the audio is not kept.
    /// </summary>
    public async Task<DictationStopResult> StopAsync(string traceId, CancellationToken cancellationToken, bool keepLatestDictationAlias)
    {
        await _gate.WaitAsync();
        CapturedAudio? capturedAudio = null;
        Task<TranscriptionResult>? transcriptionTask = null;
        var disposeCapturedAudio = true;
        try
        {
            if (!IsRecording)
            {
                return new DictationStopResult(
                    new TranscriptionResult("", "Dictation was not recording."),
                    new DictationLatencyMetrics(traceId, 0, 0, 0, 0, "not captured", 0, 0));
            }

            if (Volatile.Read(ref _cancelRequested) != 0)
            {
                IsRecording = false;
                await _audioCaptureService.CancelAsync();
                if (DetachRollingTranscriber() is { } cancelledRolling) await cancelledRolling.DrainAsync();
                return new DictationStopResult(
                    new TranscriptionResult("", "Dictation cancelled."),
                    new DictationLatencyMetrics(traceId, 0, 0, 0, 0, "cancelled", 0, 0));
            }

            IsBusy = true;
            IsRecording = false;
            SoundFeedback.PlayDictationInsert(_sessionKind);
            var coordinatorStarted = Stopwatch.StartNew();
            var stopStarted = Stopwatch.StartNew();
            capturedAudio = await _audioCaptureService.StopAsync(keepLatestDictationAlias);
            var rolling = DetachRollingTranscriber();
            stopStarted.Stop();
            _log.Info($"Dictation audio finalized. trace={traceId}; heldMs={capturedAudio.HeldMs}; " +
                $"bytes={capturedAudio.ByteLength}; audio={capturedAudio.TranscriptionPath}");
            try
            {
                if (DictationAudioQualityPolicy.IsShortDiscard(capturedAudio))
                {
                    if (rolling is not null) await rolling.DrainAsync();
                    coordinatorStarted.Stop();
                    return new DictationStopResult(
                        new TranscriptionResult(
                            "",
                            $"Short discard: heldMs={capturedAudio.HeldMs}; bytes={capturedAudio.ByteLength}",
                            DurationMs: capturedAudio.HeldMs),
                        new DictationLatencyMetrics(
                            traceId,
                            stopStarted.ElapsedMilliseconds,
                            capturedAudio.StopDisposeMs,
                            capturedAudio.FlushWaitMs,
                            capturedAudio.PreparationMs,
                            capturedAudio.Preparation,
                            0,
                            coordinatorStarted.ElapsedMilliseconds));
                }

                if (DictationAudioQualityPolicy.IsNoSpeech(capturedAudio))
                {
                    if (rolling is not null) await rolling.DrainAsync();
                    coordinatorStarted.Stop();
                    return new DictationStopResult(
                        new TranscriptionResult(
                            "",
                            $"No speech preflight: heldMs={capturedAudio.HeldMs}; bytes={capturedAudio.ByteLength}; rms={capturedAudio.Rms:F6}; peak={capturedAudio.Peak:F6}",
                            DurationMs: capturedAudio.HeldMs),
                        new DictationLatencyMetrics(
                            traceId,
                            stopStarted.ElapsedMilliseconds,
                            capturedAudio.StopDisposeMs,
                            capturedAudio.FlushWaitMs,
                            capturedAudio.PreparationMs,
                            capturedAudio.Preparation,
                            0,
                            coordinatorStarted.ElapsedMilliseconds));
                }

                var transcriptionStarted = Stopwatch.StartNew();
                IsTranscribing = true;
                var warmupPending = _modelWarmupTask is { IsCompleted: false };
                _log.Info($"Dictation recognition started. trace={traceId}; model={ModelId}; " +
                    $"warmupPending={warmupPending}");
                transcriptionTask = DetachHostedSession() is { } hosted
                    ? TranscribeHostedAsync(hosted, capturedAudio.TranscriptionPath)
                    : rolling is null
                    ? _transcriptionClient.TranscribeFileAsync("Dictation", capturedAudio.TranscriptionPath)
                    : rolling.FinishAsync(capturedAudio.TranscriptionPath);
                _activeTranscriptionTask = transcriptionTask;
                TranscriptionResult result;
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _sessionCancellation?.Token ?? CancellationToken.None);
                try
                {
                    result = await transcriptionTask.WaitAsync(linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    if (!transcriptionTask.IsCompleted)
                    {
                        disposeCapturedAudio = false;
                        var audioToDispose = capturedAudio;
                        _ = transcriptionTask.ContinueWith(
                            completed =>
                            {
                                _ = completed.Exception;
                                audioToDispose.Dispose();
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    throw;
                }
                transcriptionStarted.Stop();
                coordinatorStarted.Stop();
                var diagnostic = string.Join(
                    Environment.NewLine,
                    result.Diagnostic,
                    $"Latency trace: {traceId}",
                    $"Capture total stop ms: {stopStarted.ElapsedMilliseconds}",
                    $"Capture stop/dispose ms: {capturedAudio.StopDisposeMs}",
                    $"Capture flush wait ms: {capturedAudio.FlushWaitMs}",
                    $"Capture audio preparation ms: {capturedAudio.PreparationMs}",
                    $"Capture audio preparation: {capturedAudio.Preparation}",
                    $"Coordinator transcription wall ms: {transcriptionStarted.ElapsedMilliseconds}",
                    $"Coordinator total stop/transcribe ms: {coordinatorStarted.ElapsedMilliseconds}",
                    $"Native capture device: {capturedAudio.DeviceName}",
                    $"Native capture device id: {capturedAudio.DeviceId}",
                    $"Capture route segments: {capturedAudio.RouteSegmentCount}",
                    $"Captured audio bytes: {capturedAudio.ByteLength}",
                    $"Native capture held ms: {capturedAudio.HeldMs}",
                    $"Native input RMS: {capturedAudio.Rms:F6}",
                    $"Native input peak: {capturedAudio.Peak:F6}");

                return new DictationStopResult(
                    result with { Diagnostic = diagnostic, DurationMs = capturedAudio.HeldMs },
                    new DictationLatencyMetrics(
                        traceId,
                        stopStarted.ElapsedMilliseconds,
                        capturedAudio.StopDisposeMs,
                        capturedAudio.FlushWaitMs,
                        capturedAudio.PreparationMs,
                        capturedAudio.Preparation,
                        transcriptionStarted.ElapsedMilliseconds,
                        coordinatorStarted.ElapsedMilliseconds,
                        warmupPending,
                        _startAvailableMemory));
            }
            finally
            {
                // Normal dictation may retain the bounded benchmark alias; setup tests always dispose temporary audio.
                IsTranscribing = false;
                if (transcriptionTask?.IsCompleted == true && ReferenceEquals(_activeTranscriptionTask, transcriptionTask))
                {
                    _activeTranscriptionTask = null;
                }
                if (disposeCapturedAudio)
                {
                    capturedAudio.Dispose();
                }
            }
        }
        finally
        {
            if (DetachRollingTranscriber() is { } abandonedRolling) await abandonedRolling.DrainAsync();
            EndPerformanceBoost();
            IsBusy = false;
            _gate.Release();
            RePrepareMicrophone();
        }
    }

    public async Task CancelAsync()
    {
        Interlocked.Exchange(ref _cancelRequested, 1);
        _sessionCancellation?.Cancel();
        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!IsRecording)
            {
                return;
            }

            IsBusy = true;
            IsRecording = false;
            await _audioCaptureService.CancelAsync();
            if (DetachRollingTranscriber() is { } rolling) await rolling.DrainAsync();
        }
        finally
        {
            EndPerformanceBoost();
            IsBusy = false;
            _gate.Release();
            RePrepareMicrophone();
        }
    }

    public Task SwitchModelAsync(string modelId, CancellationToken cancellationToken = default) =>
        _transcriptionClient.SwitchModelAsync(modelId, cancellationToken);

    public async Task<ModelOperationResult> InitializeModelAsync()
    {
        var result = await _transcriptionClient.InitializeAsync();
        if (IsModelReady)
        {
            SoundFeedback.PlayModelReady();
        }

        return result;
    }

    public Task ReleaseModelAsync(string modelId, CancellationToken cancellationToken = default) =>
        _transcriptionClient.ReleaseModelAsync(modelId, cancellationToken);

    private void BeginModelWarmup()
    {
        if (_modelWarmupTask is { IsCompleted: false })
        {
            return;
        }

        // Also runs on every hotkey press: if the model is loaded this is cheap, and the session
        // refreshes weights Windows paged out while idle (see NativeParakeetClient.InitializeCore).
        // Task.Run: native session setup runs synchronously before its first await and measured
        // ~800 ms on the start path, delaying "Listening" after the microphone was already open.
        _modelWarmupTask = Task.Run(_transcriptionClient.InitializeAsync);
        _ = ObserveWarmupAsync(_modelWarmupTask);
    }

    /// <summary>
    /// Gives the next dictation to a hosted provider. Call before <see cref="StartAsync(string?)"/>;
    /// the session hears the microphone while it records and is asked for the transcript at stop,
    /// with the selected local model as the fallback. The caller owns and disposes the session.
    /// </summary>
    public void AttachHostedSession(IHostedDictationSession session)
    {
        DetachHostedSession();
        _hostedSession = session;
        if (session.AcceptsLiveAudio) _audioCaptureService.PcmSamplesAvailable += FeedHostedSession;
    }

    /// <summary>Stops feeding the hosted session and returns it, or null when none is attached.</summary>
    public IHostedDictationSession? DetachHostedSession()
    {
        _audioCaptureService.PcmSamplesAvailable -= FeedHostedSession;
        return Interlocked.Exchange(ref _hostedSession, null);
    }

    private void FeedHostedSession(object? sender, LivePcmSamplesEventArgs packet) =>
        Volatile.Read(ref _hostedSession)?.Append(packet.Samples);

    private Task<TranscriptionResult> TranscribeHostedAsync(IHostedDictationSession hosted, string wavPath) =>
        HostedDictationTranscriber.TranscribeAsync(
            hosted,
            wavPath,
            IsModelReady ? () => _transcriptionClient.TranscribeFileAsync("Dictation", wavPath) : null,
            _log.Info,
            _sessionCancellation?.Token ?? CancellationToken.None);

    private DictationRollingTranscriber? DetachRollingTranscriber()
    {
        var rolling = _rollingTranscriber;
        if (rolling is not null)
        {
            _audioCaptureService.PcmSamplesAvailable -= rolling.Feed;
            _rollingTranscriber = null;
        }
        return rolling;
    }

    private static async Task ObserveWarmupAsync(Task<ModelOperationResult> warmup)
    {
        try
        {
            await warmup.ConfigureAwait(false);
        }
        catch
        {
            // The subsequent transcription retries initialization and owns user-visible errors.
        }
    }

    /// <summary>Null when Windows would not report it; callers must not invent a value.</summary>
    private static ulong? AvailablePhysicalMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.AvailablePhysical : null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // ponytail: English Cohere only; qualify other languages/models before extending this fallback.
    internal static bool ShouldUseParakeetFallback(
        NativeAsrModelKind kind, string language, ulong freeMemoryBytes, bool parakeetReady) =>
        kind == NativeAsrModelKind.CohereTranscribe && language.Equals("en", StringComparison.OrdinalIgnoreCase) &&
        freeMemoryBytes < CohereMinimumFreeMemoryBytes && parakeetReady;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        EndPerformanceBoost();
        _audioCaptureService.DeviceListChanged -= OnDeviceListChanged;
        _audioCaptureService.RouteChanged -= OnRouteChanged;
        _audioCaptureService.LevelChanged -= OnLevelChanged;
        DetachRollingTranscriber()?.DrainAsync().GetAwaiter().GetResult();
        _audioCaptureService.Dispose();
        try
        {
            _activeTranscriptionTask?.GetAwaiter().GetResult();
            _modelWarmupTask?.GetAwaiter().GetResult();
        }
        catch
        {
            // Shutdown still disposes the native recognizer after an in-flight failure.
        }
        _transcriptionClient.Dispose();
        if (_pauseDetectorTask is { IsCompletedSuccessfully: true } detector) detector.Result.Dispose();
        _gate.Dispose();
    }

    private void OnDeviceListChanged(object? sender, EventArgs e) => DeviceListChanged?.Invoke(this, e);
    private void OnRouteChanged(object? sender, AudioRouteChangedEventArgs e) => RouteChanged?.Invoke(this, e);
    private void OnLevelChanged(object? sender, AudioLevelEventArgs e) => LevelChanged?.Invoke(this, e);

}

public static class DictationAudioQualityPolicy
{
    public static bool IsShortDiscard(CapturedAudio audio) =>
        audio.HeldMs < HotkeyTriggerTiming.ShortDiscardMilliseconds;

    public static bool IsNoSpeech(CapturedAudio audio) =>
        IsShortDiscard(audio) ||
        audio.ByteLength <= 44 ||
        (audio.Rms < 0.00008 && audio.Peak < 0.0005);
}

public sealed record DictationStopResult(
    TranscriptionResult Transcription,
    DictationLatencyMetrics Latency);

public sealed record DictationLatencyMetrics(
    string TraceId,
    long CaptureTotalMs,
    long CaptureStopDisposeMs,
    long CaptureFlushWaitMs,
    long CapturePreparationMs,
    string CapturePreparation,
    long TranscriptionWallMs,
    long CoordinatorTotalMs,
    // True when recognition began while the model was still loading.
    bool ModelWarmupPending = false,
    // Available physical memory when the dictation started; null when it was not read.
    ulong? StartAvailableMemoryBytes = null);
