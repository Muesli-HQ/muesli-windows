using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using SharpCompress.Archives;
using SherpaOnnx;

namespace Muesli.Windows.Services;

public sealed class NativeParakeetClient : ITranscriptionModelSession
{
    private const string ModelDisplayName = "Parakeet TDT 0.6B v3";
    private const string ModelDirectoryName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    private const string ModelArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2";
    public const string ModelArchiveSha256 = "5793D0FD397C5778D2CF2126994D58E9D56B1BE7C04D13C7A15BB1B4EAFB16BF";
    private const string LegacyModelDirectoryName = "sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8";

    // Pinned hashes for the four model files in the upstream sherpa-onnx v3 INT8 archive.
    // Verifying the extracted files avoids trusting a mutable download or a partial cache.
    private static readonly IReadOnlyDictionary<string, string> ExpectedModelHashes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["encoder.int8.onnx"] = "ACFC2B4456377E15D04F0243AF540B7FE7C992F8D898D751CF134C3A55FD2247",
            ["decoder.int8.onnx"] = "179E50C43D1A9DE79C8A24149A2F9BAC6EB5981823F2A2ED88D655B24248DB4E",
            ["joiner.int8.onnx"] = "3164C13FC2821009440D20FCB5FDC78BFF28B4DB2F8D0F0B329101719C0948B3",
            ["tokens.txt"] = "D58544679EA4BC6AC563D1F545EB7D474BD6CFA467F0A6E2C1DC1C7D37E3C35D"
        };
    private static readonly SemaphoreSlim ModelDownloadGate = new(1, 1);
    private static bool _modelVerified;
    private static long _lastVerificationMs;
    private static string _verificationSource = "not checked";
    private static string? _verificationFailure;
    private static string? _modelSetupFailure;
    private static int _modelSetupInProgress;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private string _recognizerProvider = "not initialized";
    private string _providerSelectionDiagnostic = "Provider selection has not run.";
    private long _initialModelLoadMs;
    private long _warmupMs;
    private bool _warmupCompleted;
    private bool _disposed;

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_PARAKEET_CACHE") ??
        Path.Combine(MuesliPathService.UserProfileDirectory, ".cache", "muesli", "native-parakeet");

    public static string ModelPath => Path.Combine(ModelCacheDirectory, ModelDirectoryName);

    private static string VerificationStampPath => Path.Combine(ModelPath, ".muesli-model-verification.json");

    public static string EncoderPath => Path.Combine(ModelPath, "encoder.int8.onnx");

    public static string DecoderPath => Path.Combine(ModelPath, "decoder.int8.onnx");

    public static string JoinerPath => Path.Combine(ModelPath, "joiner.int8.onnx");

    public static string TokensPath => Path.Combine(ModelPath, "tokens.txt");

    public static bool IsRuntimeAvailable
    {
        get
        {
            try
            {
                if (!NativeSherpaRuntime.IsAvailable)
                {
                    return false;
                }

                _ = typeof(OfflineRecognizer).Assembly.FullName;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsModelCached =>
        File.Exists(EncoderPath) &&
        File.Exists(DecoderPath) &&
        File.Exists(JoinerPath) &&
        File.Exists(TokensPath);

    public static bool IsModelVerified =>
        IsModelCached && (_modelVerified || TryUseVerificationStamp());

    public static string Status => !IsRuntimeAvailable
        ? "Runtime unavailable"
        : Volatile.Read(ref _modelSetupInProgress) != 0
            ? "Setup in progress"
            : _verificationFailure is not null
                ? "Verification failed"
                : _modelSetupFailure is not null
                    ? "Setup failed"
                    : IsModelVerified
                        ? "Ready (verified)"
                        : IsModelCached
                            ? "Needs verification"
                            : "Needs model";

    public static long ModelCacheSizeBytes()
    {
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(ModelCacheDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
    }

    public static void OpenModelCacheDirectory()
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = ModelCacheDirectory,
            UseShellExecute = true
        });
    }

    public static void ClearModelCache()
    {
        _modelVerified = false;
        if (Directory.Exists(ModelCacheDirectory))
        {
            Directory.Delete(ModelCacheDirectory, recursive: true);
        }
    }

    public async Task<ModelOperationResult> EnsureModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRuntimeAvailable)
        {
            return new ModelOperationResult("Parakeet native runtime is unavailable.", ModelCacheDirectory);
        }

        await EnsureModelFilesAsync(progress, cancellationToken);
        return await InitializeAsync();
    }

    public async Task<ModelOperationResult> PrepareModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsRuntimeAvailable)
        {
            return new ModelOperationResult("Parakeet native runtime is unavailable.", ModelCacheDirectory);
        }

        await EnsureModelFilesAsync(progress, cancellationToken);
        return new ModelOperationResult("Parakeet v3 is downloaded and verified.", ModelPath);
    }

    public async Task<ModelOperationResult> VerifyModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsModelCached)
        {
            throw new InvalidOperationException($"Parakeet v3 is not downloaded at '{ModelPath}'.");
        }

        await VerifyModelFilesAsync(progress, cancellationToken, force: true);
        return new ModelOperationResult("Parakeet v3 passed SHA-256 verification.", ModelPath);
    }

    public async Task DeleteModelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
            await ModelDownloadGate.WaitAsync(cancellationToken);
            FileStream? processLock = null;
            try
            {
                Directory.CreateDirectory(ModelCacheDirectory);
                processLock = await CrossProcessFileLock.AcquireAsync(
                    Path.Combine(ModelCacheDirectory, ".muesli-parakeet-model-setup.lock"),
                    TimeSpan.FromMinutes(30),
                    cancellationToken);
                _modelVerified = false;
                if (Directory.Exists(ModelPath))
                {
                    Directory.Delete(ModelPath, recursive: true);
                }
            }
            finally
            {
                processLock?.Dispose();
                ModelDownloadGate.Release();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModelOperationResult> InitializeAsync()
    {
        if (!IsRuntimeAvailable)
        {
            return new ModelOperationResult("Parakeet native runtime is unavailable.", ModelCacheDirectory);
        }

        if (!IsModelVerified)
        {
            return new ModelOperationResult("Parakeet native ASR model is not downloaded and verified.", ModelPath);
        }

        await _gate.WaitAsync();
        try
        {
            // Model construction and warmup are native, synchronous operations. Keep
            // them off the WPF dispatcher so first-run setup cannot freeze the UI.
            return await Task.Run(InitializeCore);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes)
    {
        return Task.Run(() => TranscribeStreamAsync(
            "Dictation",
            new MemoryStream(audioBytes),
            ownsStream: true,
            preprocessingStatus: "memory WAV",
            priorPreprocessingMs: 0));
    }

    /// <remarks>
    /// Parakeet decodes the whole file in one native call, so cancellation is honoured while the
    /// media is being decoded and resampled and again immediately before inference starts, but a
    /// decode already inside sherpa-onnx runs to completion. Inference therefore reports no
    /// fraction — see <see cref="TranscriptionProgress.TranscribingUnmeasured"/>.
    /// </remarks>
    public Task<TranscriptionResult> TranscribeFileAsync(
        string title,
        string filePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Media Foundation resampling and sherpa-onnx inference are synchronous.
        // Execute the complete native path away from the WPF dispatcher.
        return Task.Run(() => TranscribeFileCoreAsync(title, filePath, progress, cancellationToken), cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeFileCoreAsync(
        string title,
        string filePath,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var preprocessingStarted = Stopwatch.StartNew();
        if (IsParakeetReadyWav(filePath))
        {
            preprocessingStarted.Stop();
            await using var stream = File.OpenRead(filePath);
            return await TranscribeStreamAsync(
                title,
                stream,
                ownsStream: false,
                "existing 16 kHz mono WAV",
                preprocessingStarted.ElapsedMilliseconds,
                progress,
                cancellationToken);
        }

        var samples = ReadResampledMonoSamples(filePath, out var audioDurationMs, progress, cancellationToken);
        preprocessingStarted.Stop();
        return await TranscribeSamplesAsync(
            title,
            samples,
            sampleRate: 16000,
            audioDurationMs,
            "direct in-memory downmix/resample; no temporary WAV",
            preprocessingStarted.ElapsedMilliseconds,
            progress,
            cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeStreamAsync(
        string title,
        Stream audioStream,
        bool ownsStream,
        string preprocessingStatus,
        long priorPreprocessingMs,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRuntimeAvailable)
        {
            throw new InvalidOperationException("Native Parakeet runtime is unavailable.");
        }

        if (!IsModelVerified)
        {
            throw new InvalidOperationException("Native Parakeet model is not downloaded and verified. Prepare it from Models first.");
        }

        var sampleReadStarted = Stopwatch.StartNew();
        audioStream.Position = 0;
        var samples = ReadMonoSamples(audioStream, out var sampleRate, out var audioDurationMs);
        sampleReadStarted.Stop();
        var preprocessingMs = priorPreprocessingMs + sampleReadStarted.ElapsedMilliseconds;

        try
        {
            return await TranscribeSamplesAsync(
                title,
                samples,
                sampleRate,
                audioDurationMs,
                preprocessingStatus,
                preprocessingMs,
                progress,
                cancellationToken);
        }
        finally
        {
            if (ownsStream)
            {
                await audioStream.DisposeAsync();
            }
        }
    }

    private async Task<TranscriptionResult> TranscribeSamplesAsync(
        string title,
        float[] samples,
        int sampleRate,
        int audioDurationMs,
        string preprocessingStatus,
        long preprocessingMs,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRuntimeAvailable)
        {
            throw new InvalidOperationException("Native Parakeet runtime is unavailable.");
        }

        if (!IsModelVerified)
        {
            throw new InvalidOperationException("Native Parakeet model is not downloaded and verified. Prepare it from Models first.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Last honest cancellation point: once sherpa-onnx owns the samples the decode is a
            // single native call that cannot be interrupted.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(TranscriptionProgress.TranscribingUnmeasured());
            return await Task.Run(() => TranscribeSamplesCore(
                title,
                samples,
                sampleRate,
                audioDurationMs,
                preprocessingStatus,
                preprocessingMs), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModelOperationResult InitializeCore()
    {
        ThrowIfDisposed();
        var reused = _recognizer is not null;
        EnsureRecognizer();
        if (!_warmupCompleted)
        {
            var warmupStarted = Stopwatch.StartNew();
            using var stream = _recognizer!.CreateStream();
            stream.AcceptWaveform(16000, new float[16000]);
            _recognizer.Decode(stream);
            warmupStarted.Stop();
            _warmupMs = warmupStarted.ElapsedMilliseconds;
            _warmupCompleted = true;
        }

        var diagnostic = string.Join(
            Environment.NewLine,
            $"Model: {ModelDisplayName}",
            $"Model directory: {ModelPath}",
            $"Provider: {_recognizerProvider}",
            $"Device: {DeviceForProvider(_recognizerProvider)}",
            "Compute type: int8 ONNX transducer",
            $"Threads: {RecognizerThreadCount(_recognizerProvider)}",
            $"Model instance reused: {reused}",
            $"Model verification: {_verificationSource}",
            $"Model verification ms: {_lastVerificationMs}",
            $"Initial model load ms: {_initialModelLoadMs}",
            $"Warmup ms: {_warmupMs}",
            _providerSelectionDiagnostic);
        return new ModelOperationResult(
            $"Parakeet is ready and warm on {_recognizerProvider}.",
            diagnostic);
    }

    private TranscriptionResult TranscribeSamplesCore(
        string title,
        float[] samples,
        int sampleRate,
        int audioDurationMs,
        string preprocessingStatus,
        long preprocessingMs)
    {
        ThrowIfDisposed();
        var modelWasReused = _recognizer is not null;
        var modelLoadStarted = Stopwatch.StartNew();
        EnsureRecognizer();
        modelLoadStarted.Stop();
        var startedAt = Stopwatch.StartNew();
        var result = Decode(samples, sampleRate);
        startedAt.Stop();

        var text = result.Text?.Trim() ?? "";
        var segmentation = ParakeetTimestampSegmenter.Build(
            title,
            text,
            result.Tokens,
            result.Timestamps,
            result.Durations,
            audioDurationMs);
        var segments = segmentation.Segments;
        var inferenceMs = Math.Max(1, (int)startedAt.ElapsedMilliseconds);
        var wallMs = preprocessingMs + modelLoadStarted.ElapsedMilliseconds + inferenceMs;
        var rtf = audioDurationMs > 0 ? wallMs / (audioDurationMs * 1.0) : 0;
        var diagnostic = string.Join(
            Environment.NewLine,
            "ASR engine: native-parakeet-sherpa-onnx",
            $"Model: {ModelDisplayName}",
            $"Model directory: {ModelPath}",
            $"Backend: {_recognizerProvider}",
            $"Device: {DeviceForProvider(_recognizerProvider)}",
            "Compute type: int8 ONNX transducer",
            $"Threads: {RecognizerThreadCount(_recognizerProvider)}",
            $"Model instance reused: {modelWasReused}",
            $"Model factory initial load ms: {_initialModelLoadMs}",
            $"Model load/init duration ms: {modelLoadStarted.ElapsedMilliseconds}",
            $"Audio preprocessing: {preprocessingStatus}; decoded directly to float samples",
            $"Audio preprocessing duration ms: {preprocessingMs}",
            $"Audio duration ms: {audioDurationMs}",
            $"Inference duration ms: {inferenceMs}",
            $"Transcription wall duration ms: {wallMs}",
            $"Realtime factor: {rtf:0.000}",
            $"Segments: {segments.Count}",
            $"Timestamp segmentation: {segmentation.Mode}",
            $"Timestamped tokens: {segmentation.TimestampedTokenCount}",
            result.Timestamps is { Length: > 0 }
                ? $"Token timestamps: {result.Timestamps.First():0.00}-{result.Timestamps.Last():0.00}s"
                : "Token timestamps: none",
            $"Provider selection: {_providerSelectionDiagnostic}");

        return new TranscriptionResult(text, diagnostic, inferenceMs, segments);
    }

    private OfflineRecognizerResult Decode(float[] samples, int sampleRate)
    {
        using var stream = _recognizer!.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        _recognizer.Decode(stream);
        return stream.Result;
    }

    private static OfflineRecognizerConfig BuildRecognizerConfig(string provider)
    {
        var config = new OfflineRecognizerConfig
        {
            DecodingMethod = "greedy_search"
        };
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = EncoderPath;
        config.ModelConfig.Transducer.Decoder = DecoderPath;
        config.ModelConfig.Transducer.Joiner = JoinerPath;
        config.ModelConfig.Tokens = TokensPath;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = RecognizerThreadCount(provider);
        config.ModelConfig.Provider = provider;
        config.ModelConfig.Debug = 0;
        return config;
    }

    private void EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return;
        }

        DeleteObsoleteDownloadArtifacts();
        if (Environment.GetEnvironmentVariable("MUESLI_PARAKEET_PROVIDER")?.Trim()
                .Equals("off", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("Parakeet is disabled by MUESLI_PARAKEET_PROVIDER=off.");
        }

        const ExecutionProviderRole role = ExecutionProviderRole.OfflineTranscription;
        var candidates = ExecutionProviderService.PreferredProviders(role, NativeAsrModelKind.Parakeet);
        Exception? lastError = null;
        var attemptedProviders = new List<string>();
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            attemptedProviders.Add(candidate.Provider);
            var attemptStarted = Stopwatch.StartNew();
            try
            {
                ProviderEvidence? evidence = null;
                if (candidate.IsGpu)
                {
                    if (!ExecutionProviderService.TryGetQualifiedCudaEvidence(ModelDirectoryName, out var cachedEvidence))
                    {
                        // Prove CUDA with a real warm-up decode before accepting it for user audio. The
                        // probe recognizer is released so ONNX Runtime flushes its execution profile.
                        var probe = ExecutionProviderService.BeginWarmUp(candidate.Provider, ModelDirectoryName);
                        try
                        {
                            using (var probeRecognizer = new OfflineRecognizer(BuildRecognizerConfig(probe.ProviderString)))
                            {
                                WarmUp(probeRecognizer);
                            }

                            var probed = probe.Complete();
                            if (!probed.ConfirmedGpuInference)
                            {
                                lastError = new InvalidOperationException(probed.Detail);
                                ExecutionProviderService.RecordFailure(role, candidate.Provider, ModelDirectoryName, attemptStarted.ElapsedMilliseconds, probed.Detail);
                                if (ProviderFallbackDisabled() || ExecutionProviderService.IsProviderForcedByEnvironment(role))
                                {
                                    throw new InvalidOperationException($"Parakeet provider '{candidate.Provider}' could not be proven: {probed.Detail}");
                                }

                                continue;
                            }

                            ExecutionProviderService.StoreQualifiedCudaEvidence(ModelDirectoryName, probed);
                            evidence = probed;
                        }
                        catch (Exception probeException)
                        {
                            probe.Abandon(probeException);
                            throw;
                        }
                    }
                    else
                    {
                        evidence = cachedEvidence;
                    }
                }

                var loadStarted = Stopwatch.StartNew();
                var recognizer = new OfflineRecognizer(BuildRecognizerConfig(candidate.Provider));
                loadStarted.Stop();
                _recognizer = recognizer;
                _recognizerProvider = candidate.Provider;
                _initialModelLoadMs = loadStarted.ElapsedMilliseconds;
                ExecutionProviderService.RecordActive(role, candidate.Provider, ModelDirectoryName, _initialModelLoadMs, evidence);
                _providerSelectionDiagnostic = candidate.IsGpu
                    ? string.Join(
                        Environment.NewLine,
                        $"CUDA provider initialized and verified by warm-up inference after {string.Join(", ", attemptedProviders)}.",
                        NativeSherpaRuntime.Diagnostic)
                    : HasCudaProviderFiles()
                        ? string.Join(
                            Environment.NewLine,
                            $"CPU selected after CUDA initialization was unavailable. Attempted: {string.Join(", ", attemptedProviders)}.",
                            NativeSherpaRuntime.Diagnostic)
                        : string.Join(
                            Environment.NewLine,
                            "CPU selected because a complete Sherpa ONNX CUDA runtime was unavailable.",
                            NativeSherpaRuntime.Diagnostic);
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                ExecutionProviderService.RecordFailure(role, candidate.Provider, ModelDirectoryName, attemptStarted.ElapsedMilliseconds, exception.Message);
                if (ProviderFallbackDisabled())
                {
                    throw new InvalidOperationException(
                        $"Parakeet provider '{candidate.Provider}' could not initialize: {exception.Message}",
                        exception);
                }
            }
        }

        throw new InvalidOperationException(
            $"No Parakeet execution provider could initialize. Attempted: {string.Join(", ", attemptedProviders)}.",
            lastError);
    }

    /// <summary>
    /// A real half-second decode that forces the encoder, decoder, and joiner graphs to execute on
    /// the selected provider before the model is accepted for user audio.
    /// </summary>
    private static void WarmUp(OfflineRecognizer recognizer)
    {
        const int sampleRate = 16000;
        var samples = new float[sampleRate / 2];
        var random = new Random(20260920);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() - 0.5) * 0.002);
        }

        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        recognizer.Decode(stream);
        _ = stream.Result.Text;
    }

    private static bool HasCudaProviderFiles()
    {
        return NativeSherpaRuntime.IsCudaCapable;
    }

    private static int RecognizerThreadCount(string provider)
    {
        var configured = Environment.GetEnvironmentVariable("MUESLI_PARAKEET_THREADS");
        return RecommendedThreadCount(provider, Environment.ProcessorCount, configured);
    }

    internal static int RecommendedThreadCount(string provider, int processorCount, string? configured = null)
    {
        if (int.TryParse(configured, out var threads) && threads > 0)
        {
            return Math.Clamp(threads, 1, 32);
        }

        // The TDT decoder still performs CPU-side work with the CUDA provider.
        // On the deterministic RTX 4070 benchmark, four host threads avoided
        // oversubscription and outperformed 1, 2, and 8 threads.
        if (provider.Equals("cuda", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(1, Math.Min(processorCount, 4));
        }

        // On the 16-logical-core i7-13620H release benchmark, the former eight-thread
        // default oversubscribed Parakeet (RTF 0.256). Six threads repeatedly measured
        // 0.192-0.195, while five and seven missed or approached the 0.20 launch gate.
        return Math.Max(1, Math.Min(processorCount, 6));
    }

    private static string DeviceForProvider(string provider)
    {
        return provider.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            ? "cuda"
            : provider.Equals("directml", StringComparison.OrdinalIgnoreCase)
                ? "directml"
                : "cpu";
    }

    private static bool ProviderFallbackDisabled()
    {
        var value = Environment.GetEnvironmentVariable("MUESLI_PARAKEET_REQUIRE_GPU");
        value ??= Environment.GetEnvironmentVariable("MUESLI_PARAKEET_REQUIRE_CUDA");
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task EnsureModelFilesAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await ModelDownloadGate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _modelSetupInProgress, 1);
        FileStream? processLock = null;
        try
        {
            Directory.CreateDirectory(ModelCacheDirectory);
            processLock = await CrossProcessFileLock.AcquireAsync(
                Path.Combine(ModelCacheDirectory, ".muesli-parakeet-model-setup.lock"),
                TimeSpan.FromMinutes(30),
                cancellationToken);
            ModelSetupArtifactCleaner.Cleanup(ModelCacheDirectory, ModelDirectoryName);

            if (IsModelCached)
            {
                var verificationStarted = Stopwatch.StartNew();
                if (!_modelVerified && TryUseVerificationStamp())
                {
                    _modelVerified = true;
                    _verificationSource = "verified cache stamp (file size and timestamp unchanged)";
                    progress?.Report(new ModelDownloadProgress(
                        "Parakeet v3 verified cache unchanged",
                        ExpectedModelHashes.Count,
                        ExpectedModelHashes.Count));
                }

                try
                {
                    await VerifyModelFilesAsync(progress, cancellationToken);
                    verificationStarted.Stop();
                    _lastVerificationMs = verificationStarted.ElapsedMilliseconds;
                    _verificationFailure = null;
                    _modelSetupFailure = null;
                    DeleteLegacyModelCache();
                    return;
                }
                catch (InvalidDataException)
                {
                    _modelVerified = false;
                    Directory.Delete(ModelPath, recursive: true);
                }
            }

            var archivePath = Path.Combine(ModelCacheDirectory, $".{ModelDirectoryName}.{Guid.NewGuid():N}.download");
            var stagingPath = Path.Combine(ModelCacheDirectory, $".{ModelDirectoryName}.{Guid.NewGuid():N}.staging");
            try
            {
                await DownloadFileAsync(ModelArchiveUrl, archivePath, ModelArchiveSha256, progress, cancellationToken);
                progress?.Report(new ModelDownloadProgress("Extracting Parakeet v3 model", 0, null));
                Directory.CreateDirectory(stagingPath);
                using var archive = ArchiveFactory.OpenArchive(archivePath, null);
                SafeArchiveExtractor.ExtractSafely(archive, stagingPath, cancellationToken);

                var stagedModelPath = Path.Combine(stagingPath, ModelDirectoryName);
                await VerifyModelFilesAtPathAsync(stagedModelPath, cancellationToken);
                if (Directory.Exists(ModelPath))
                {
                    Directory.Delete(ModelPath, recursive: true);
                }
                Directory.Move(stagedModelPath, ModelPath);

                await VerifyModelFilesAsync(progress, cancellationToken, force: true);
                _verificationSource = "full SHA-256 verification after download";
                _verificationFailure = null;
                _modelSetupFailure = null;
                DeleteLegacyModelCache();
            }
            catch
            {
                _modelVerified = false;
                if (Directory.Exists(ModelPath))
                {
                    Directory.Delete(ModelPath, recursive: true);
                }

                throw;
            }
            finally
            {
                TryDelete(archivePath);
                TryDeleteDirectory(stagingPath);
            }
        }
        catch (InvalidDataException exception)
        {
            _verificationFailure = exception.Message;
            _modelSetupFailure = null;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _verificationFailure = null;
            _modelSetupFailure = exception.GetType().Name;
            throw;
        }
        finally
        {
            processLock?.Dispose();
            Interlocked.Exchange(ref _modelSetupInProgress, 0);
            ModelDownloadGate.Release();
        }
    }

    private static async Task VerifyModelFilesAtPathAsync(string modelPath, CancellationToken cancellationToken)
    {
        foreach (var expected in ExpectedModelHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(modelPath, expected.Key);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"The downloaded {ModelDisplayName} archive is missing {expected.Key}.");
            }

            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Parakeet archive content verification failed for {expected.Key}. Expected SHA-256 {expected.Value}, got {actual}.");
            }
        }
    }

    private static async Task VerifyModelFilesAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (_modelVerified && !force)
        {
            if (_verificationSource == "not checked")
            {
                _verificationSource = "verified earlier in this process";
            }
            return;
        }

        var verificationStarted = Stopwatch.StartNew();
        var verified = 0L;
        foreach (var expected in ExpectedModelHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModelDownloadProgress(
                $"Verifying Parakeet v3 model ({expected.Key})",
                verified,
                ExpectedModelHashes.Count));
            var path = Path.Combine(ModelPath, expected.Key);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"Parakeet model verification failed: {expected.Key} is missing.");
            }

            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(VerificationStampPath);
                throw new InvalidDataException(
                    $"Parakeet model verification failed for {expected.Key}. Expected SHA-256 {expected.Value}, got {actual}.");
            }

            verified++;
        }

        _modelVerified = true;
        WriteVerificationStamp();
        verificationStarted.Stop();
        _lastVerificationMs = verificationStarted.ElapsedMilliseconds;
        _verificationSource = "full SHA-256 verification";
        progress?.Report(new ModelDownloadProgress(
            "Parakeet v3 model verified",
            ExpectedModelHashes.Count,
            ExpectedModelHashes.Count));
    }

    private static bool TryUseVerificationStamp()
    {
        try
        {
            if (!File.Exists(VerificationStampPath))
            {
                return false;
            }

            var stamp = JsonSerializer.Deserialize<ModelVerificationStamp>(
                File.ReadAllText(VerificationStampPath));
            if (stamp is null ||
                stamp.SchemaVersion != 1 ||
                !stamp.ModelId.Equals(ModelDirectoryName, StringComparison.Ordinal) ||
                stamp.Files.Count != ExpectedModelHashes.Count)
            {
                return false;
            }

            foreach (var expected in ExpectedModelHashes)
            {
                var fileStamp = stamp.Files.FirstOrDefault(file =>
                    file.Name.Equals(expected.Key, StringComparison.OrdinalIgnoreCase));
                var path = Path.Combine(ModelPath, expected.Key);
                var file = new FileInfo(path);
                if (fileStamp is null ||
                    !file.Exists ||
                    !fileStamp.Sha256.Equals(expected.Value, StringComparison.OrdinalIgnoreCase) ||
                    fileStamp.Length != file.Length ||
                    fileStamp.LastWriteTimeUtcTicks != file.LastWriteTimeUtc.Ticks)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteVerificationStamp()
    {
        var stamp = new ModelVerificationStamp(
            1,
            ModelDirectoryName,
            ExpectedModelHashes.Select(expected =>
            {
                var file = new FileInfo(Path.Combine(ModelPath, expected.Key));
                return new ModelVerificationFile(
                    expected.Key,
                    expected.Value,
                    file.Length,
                    file.LastWriteTimeUtc.Ticks);
            }).ToArray());
        var temporaryPath = $"{VerificationStampPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, VerificationStampPath, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            // The model may be deployed in a read-only shared cache. Full hash
            // verification remains valid; only the next-start optimization is lost.
        }
        catch (IOException)
        {
            // A competing process can create the same stamp. Never turn a
            // successfully verified model into a transcription failure.
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void DeleteLegacyModelCache()
    {
        var legacyDirectory = Path.Combine(ModelCacheDirectory, LegacyModelDirectoryName);
        if (Directory.Exists(legacyDirectory))
        {
            Directory.Delete(legacyDirectory, recursive: true);
        }

        TryDelete(Path.Combine(ModelCacheDirectory, $"{LegacyModelDirectoryName}.tar.bz2"));
    }

    private static void DeleteObsoleteDownloadArtifacts()
    {
        DeleteLegacyModelCache();
        TryDelete(Path.Combine(ModelCacheDirectory, $"{ModelDirectoryName}.tar.bz2"));
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string expectedSha256,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Model downloads must use HTTPS.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ModelCacheDirectory);
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.partial";
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new ModelDownloadProgress("Downloading Parakeet v3 model", received, totalBytes));
                }

                output.Flush(flushToDisk: true);
            }

            await using var verificationStream = File.OpenRead(tempPath);
            var actualSha256 = Convert.ToHexString(await SHA256.HashDataAsync(verificationStream, cancellationToken));
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded Parakeet archive hash mismatch. Expected {expectedSha256}; found {actualSha256}.");
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            var root = Path.GetFullPath(ModelCacheDirectory);
            var fullPath = Path.GetFullPath(path);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Staging directories are never used as model inputs.
        }
    }

    private static float[] ReadMonoSamples(Stream audioStream, out int sampleRate, out int durationMs)
    {
        using var reader = new WaveFileReader(audioStream);
        sampleRate = reader.WaveFormat.SampleRate;
        durationMs = (int)reader.TotalTime.TotalMilliseconds;
        if (reader.WaveFormat.Channels != 1 || sampleRate != 16000)
        {
            throw new InvalidOperationException(
                $"Parakeet expects 16 kHz mono WAV input, but received {sampleRate} Hz with {reader.WaveFormat.Channels} channels.");
        }

        var sampleProvider = reader.ToSampleProvider();
        var estimatedSamples = Math.Max(1, (int)(reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign)));
        var samples = new float[estimatedSamples];
        var total = 0;
        while (total < samples.Length)
        {
            var read = sampleProvider.Read(samples, total, samples.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total == samples.Length ? samples : samples[..total];
    }

    private static float[] ReadResampledMonoSamples(
        string sourcePath,
        out int durationMs,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var reader = new AudioFileReader(sourcePath);
        var monoProvider = reader.ToMono();
        var outputFormat = new WaveFormat(16000, 16, 1);
        var waveProvider = monoProvider.ToWaveProvider16();
        using var resampler = new MediaFoundationResampler(waveProvider, outputFormat)
        {
            ResamplerQuality = 60
        };

        var expectedBytes = (long)Math.Ceiling(reader.TotalTime.TotalSeconds * outputFormat.AverageBytesPerSecond);
        // Throttled: one report per decoded buffer floods the dispatcher and blocks the Cancel button.
        var throttle = new ThrottledTranscriptionProgress(progress);
        var estimatedBytes = Math.Max(
            outputFormat.AverageBytesPerSecond,
            (int)Math.Ceiling(reader.TotalTime.TotalSeconds * outputFormat.AverageBytesPerSecond) +
            outputFormat.AverageBytesPerSecond);
        var pcmBytes = new byte[estimatedBytes];
        var totalBytes = 0;
        while (true)
        {
            // Decoding a long video is the slowest part of an import, so it has to yield to Cancel.
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes == pcmBytes.Length)
            {
                Array.Resize(ref pcmBytes, checked(pcmBytes.Length * 2));
            }

            var read = resampler.Read(pcmBytes, totalBytes, pcmBytes.Length - totalBytes);
            if (read <= 0)
            {
                break;
            }

            totalBytes += read;
            if (expectedBytes > 0)
            {
                throttle.Report(TranscriptionStage.Decoding, totalBytes / (double)expectedBytes);
            }
        }

        var sampleCount = totalBytes / sizeof(short);
        durationMs = (int)Math.Round(sampleCount * 1000.0 / 16000);
        if (sampleCount < 160 || durationMs <= 0)
        {
            throw new InvalidDataException(
                $"Windows could not decode an audio track from '{Path.GetFileName(sourcePath)}'. " +
                "Install the required Windows media codec or convert the file to WAV, MP3, or M4A and try again.");
        }

        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = BitConverter.ToInt16(pcmBytes, index * sizeof(short)) / 32768f;
        }

        return samples;
    }

    private static bool IsParakeetReadyWav(string sourcePath)
    {
        try
        {
            using var reader = new WaveFileReader(sourcePath);
            return reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm &&
                   reader.WaveFormat.SampleRate == 16000 &&
                   reader.WaveFormat.BitsPerSample == 16 &&
                   reader.WaveFormat.Channels == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary normalized audio cleanup is best effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recognizer?.Dispose();
        _recognizer = null;
        _gate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ModelVerificationStamp(
        int SchemaVersion,
        string ModelId,
        IReadOnlyList<ModelVerificationFile> Files);

    private sealed record ModelVerificationFile(
        string Name,
        string Sha256,
        long Length,
        long LastWriteTimeUtcTicks);

}
