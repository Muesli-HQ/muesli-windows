using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using SharpCompress.Archives;
using SherpaOnnx;

namespace Muesli.Windows.Services;

/// <summary>
/// Runs the non-Parakeet models in <see cref="TranscriptionModelCatalog"/> through
/// the same native sherpa-onnx runtime used by the rest of the Windows app.
/// </summary>
public sealed class NativeOfflineAsrClient : ITranscriptionModelSession
{
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private readonly TranscriptionModelDefinition _model;
    private readonly SemaphoreSlim _recognizerGate = new(1, 1);
    private OfflineRecognizer? _recognizer;
    private string _provider = "not initialized";
    private long _modelLoadMs;
    private bool _disposed;

    public NativeOfflineAsrClient(TranscriptionModelDefinition model)
    {
        if (model.Kind == NativeAsrModelKind.Parakeet)
        {
            throw new ArgumentException("The legacy Parakeet v3 entry uses NativeParakeetClient.", nameof(model));
        }

        _model = model;
    }

    public bool IsModelVerified => _model.IsCached && TryUseVerificationStamp(VerificationPath);
    private string VerificationPath => Path.Combine(_model.ModelPath, ".muesli-verified.json");

    public async Task<ModelOperationResult> PrepareModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!NativeParakeetClient.IsRuntimeAvailable)
        {
            return new ModelOperationResult("The native sherpa-onnx runtime is unavailable.", _model.ModelPath);
        }

        await EnsureModelFilesAsync(progress, cancellationToken);
        return new ModelOperationResult($"{_model.DisplayName} is downloaded and verified.", _model.ModelPath);
    }

    public async Task<ModelOperationResult> VerifyModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_model.IsCached)
        {
            throw new InvalidOperationException($"{_model.DisplayName} is not downloaded at '{_model.ModelPath}'.");
        }

        await VerifyRequiredFilesAsync(_model.ModelPath, progress, cancellationToken);
        await WriteVerificationStampAsync(VerificationPath, cancellationToken);
        return new ModelOperationResult($"{_model.DisplayName} passed SHA-256 verification.", _model.ModelPath);
    }

    public async Task DeleteModelAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _recognizerGate.WaitAsync(cancellationToken);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
            await DownloadGate.WaitAsync(cancellationToken);
            FileStream? processLock = null;
            try
            {
                Directory.CreateDirectory(TranscriptionModelCatalog.ModelCacheDirectory);
                processLock = await CrossProcessFileLock.AcquireAsync(
                    Path.Combine(TranscriptionModelCatalog.ModelCacheDirectory, $".muesli-{_model.Id}-setup.lock"),
                    TimeSpan.FromMinutes(30),
                    cancellationToken);
                if (Directory.Exists(_model.ModelPath))
                {
                    Directory.Delete(_model.ModelPath, recursive: true);
                }
            }
            finally
            {
                processLock?.Dispose();
                DownloadGate.Release();
            }
        }
        finally
        {
            _recognizerGate.Release();
        }
    }

    public async Task<ModelOperationResult> EnsureModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!NativeParakeetClient.IsRuntimeAvailable)
        {
            return new ModelOperationResult("The native sherpa-onnx runtime is unavailable.", _model.ModelPath);
        }

        await EnsureModelFilesAsync(progress, cancellationToken);
        return await InitializeAsync();
    }

    public async Task<ModelOperationResult> InitializeAsync()
    {
        if (!IsModelVerified)
        {
            return new ModelOperationResult($"{_model.DisplayName} is not downloaded and verified.", _model.ModelPath);
        }

        await _recognizerGate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                ThrowIfDisposed();
                var reused = _recognizer is not null;
                EnsureRecognizer();
                return new ModelOperationResult(
                    $"{_model.DisplayName} is ready on {_provider}.",
                    string.Join(
                        Environment.NewLine,
                        $"Model: {_model.DisplayName}",
                        $"Model ID: {_model.Id}",
                        $"Model directory: {_model.ModelPath}",
                        $"Provider: {_provider}",
                        $"Threads: {ThreadCount(_provider)}",
                        $"Model instance reused: {reused}",
                        $"Initial model load ms: {_modelLoadMs}"));
            });
        }
        finally
        {
            _recognizerGate.Release();
        }
    }

    public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes)
    {
        return Task.Run(async () =>
        {
            await using var stream = new MemoryStream(audioBytes);
            var samples = ReadMonoWavSamples(stream, out var durationMs);
            return await TranscribeSamplesAsync("Dictation", samples, durationMs, "memory WAV");
        });
    }

    public Task<TranscriptionResult> TranscribeFileAsync(
        string title,
        string filePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            var preprocessing = Stopwatch.StartNew();
            var samples = ReadResampledMonoSamples(filePath, out var durationMs, progress, cancellationToken);
            preprocessing.Stop();
            return await TranscribeSamplesAsync(
                title,
                samples,
                durationMs,
                $"in-memory 16 kHz mono conversion ({preprocessing.ElapsedMilliseconds} ms)",
                progress,
                cancellationToken);
        }, cancellationToken);
    }

    private async Task<TranscriptionResult> TranscribeSamplesAsync(
        string title,
        float[] samples,
        int durationMs,
        string preprocessing,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsModelVerified)
        {
            throw new InvalidOperationException($"{_model.DisplayName} is not downloaded and verified. Prepare it from Models first.");
        }

        await _recognizerGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                ThrowIfDisposed();
                var reused = _recognizer is not null;
                EnsureRecognizer();
                var inference = Stopwatch.StartNew();
                const int sampleRate = 16000;
                const int chunkSamples = sampleRate * 25;
                var texts = new List<string>();
                var segments = new List<TranscriptSegment>();
                var timestampModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var chunkCount = 0;
                var throttle = new ThrottledTranscriptionProgress(progress);
                for (var offset = 0; offset < samples.Length; offset += chunkSamples)
                {
                    // Decoding is chunked, so cancellation lands within one chunk rather than at the
                    // end of the file. Partial work is discarded: the caller gets no transcript at all.
                    cancellationToken.ThrowIfCancellationRequested();
                    throttle.Report(TranscriptionStage.Transcribing, offset / (double)samples.Length);
                    var length = Math.Min(chunkSamples, samples.Length - offset);
                    var chunk = samples.AsSpan(offset, length).ToArray();
                    using var stream = _recognizer!.CreateStream();
                    stream.AcceptWaveform(sampleRate, chunk);
                    _recognizer.Decode(stream);
                    var result = stream.Result;
                    var chunkText = result.Text?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        texts.Add(chunkText);
                    }

                    var chunkDurationMs = (int)Math.Round(length * 1000.0 / sampleRate);
                    var chunkStartMs = (int)Math.Round(offset * 1000.0 / sampleRate);
                    var chunkSegmentation = ParakeetTimestampSegmenter.Build(
                        title,
                        chunkText,
                        result.Tokens,
                        result.Timestamps,
                        result.Durations,
                        chunkDurationMs);
                    timestampModes.Add(chunkSegmentation.Mode);
                    foreach (var segment in chunkSegmentation.Segments)
                    {
                        segments.Add(segment with
                        {
                            Id = $"{_model.Id}_{chunkCount}_{segment.Id}",
                            StartMs = Math.Clamp(segment.StartMs + chunkStartMs, 0, durationMs),
                            EndMs = Math.Clamp(segment.EndMs + chunkStartMs, 0, durationMs)
                        });
                    }

                    chunkCount++;
                }
                inference.Stop();

                var text = string.Join(" ", texts).Trim();
                var inferenceMs = Math.Max(1, (int)inference.ElapsedMilliseconds);
                return new TranscriptionResult(
                    text,
                    string.Join(
                        Environment.NewLine,
                        "ASR engine: native-sherpa-onnx",
                        $"Model: {_model.DisplayName}",
                        $"Model ID: {_model.Id}",
                        $"Model directory: {_model.ModelPath}",
                        $"Backend: {_provider}",
                        $"Threads: {ThreadCount(_provider)}",
                        $"Language mode: {_model.Language}",
                        $"Model instance reused: {reused}",
                        $"Audio preprocessing: {preprocessing}",
                        $"Audio duration ms: {durationMs}",
                        $"Inference duration ms: {inferenceMs}",
                        $"Decode chunks: {chunkCount} (25 second maximum)",
                        $"Segments: {segments.Count}",
                        $"Timestamp segmentation: {string.Join(", ", timestampModes)}"),
                    inferenceMs,
                    segments);
            });
        }
        finally
        {
            _recognizerGate.Release();
        }
    }

    private void EnsureRecognizer()
    {
        if (_recognizer is not null)
        {
            return;
        }

        const ExecutionProviderRole role = ExecutionProviderRole.OfflineTranscription;
        var candidates = ExecutionProviderService.PreferredProviders(role, _model.Kind);
        Exception? lastError = null;
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var started = Stopwatch.StartNew();
            try
            {
                ProviderEvidence? evidence = null;
                if (candidate.IsGpu)
                {
                    if (!ExecutionProviderService.TryGetQualifiedCudaEvidence(_model.Id, out var cachedEvidence))
                    {
                        // The first CUDA use on this machine runs a real warm-up decode with ONNX
                        // Runtime profiling enabled. The profile proves which execution provider
                        // actually executed the graph; a request alone is never accepted.
                        var probe = ExecutionProviderService.BeginWarmUp(candidate.Provider, _model.Id);
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
                                ExecutionProviderService.RecordFailure(role, candidate.Provider, _model.Id, started.ElapsedMilliseconds, probed.Detail);
                                continue;
                            }

                            ExecutionProviderService.StoreQualifiedCudaEvidence(_model.Id, probed);
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
                _provider = candidate.Provider;
                _modelLoadMs = loadStarted.ElapsedMilliseconds;
                ExecutionProviderService.RecordActive(role, candidate.Provider, _model.Id, _modelLoadMs, evidence);
                if (candidate.IsGpu && index > 0)
                {
                    ExecutionProviderService.RecordFallback(role, _model.Id, "cuda", "CUDA bundle was not loadable.");
                }

                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                ExecutionProviderService.RecordFailure(role, candidate.Provider, _model.Id, started.ElapsedMilliseconds, exception.Message);
                if (ExecutionProviderService.Preference == ExecutionProviderPreference.Cuda && index + 1 < candidates.Count)
                {
                    ExecutionProviderService.RecordFallback(role, _model.Id, candidate.Provider, exception.Message);
                }
            }
        }

        throw new InvalidOperationException($"{_model.DisplayName} could not initialize on an available execution provider.", lastError);
    }

    /// <summary>
    /// A real half-second decode that forces the graph to execute on the selected provider before
    /// the model is accepted for user audio. Quiet noise keeps the decoder active without
    /// producing a meaningful hypothesis.
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

    private OfflineRecognizerConfig BuildRecognizerConfig(string provider)
    {
        string FilePath(string relativePath) => Path.Combine(_model.ModelPath, relativePath);

        var config = new OfflineRecognizerConfig { DecodingMethod = "greedy_search" };
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.NumThreads = ThreadCount(provider);
        config.ModelConfig.Provider = provider;
        config.ModelConfig.Debug = 0;

        switch (_model.Kind)
        {
            case NativeAsrModelKind.ParakeetTransducer:
                config.ModelConfig.Transducer.Encoder = FilePath("encoder.int8.onnx");
                config.ModelConfig.Transducer.Decoder = FilePath("decoder.int8.onnx");
                config.ModelConfig.Transducer.Joiner = FilePath("joiner.int8.onnx");
                config.ModelConfig.Tokens = FilePath("tokens.txt");
                config.ModelConfig.ModelType = "nemo_transducer";
                break;
            case NativeAsrModelKind.Whisper:
                config.ModelConfig.Whisper.Encoder = FilePath(_model.RequiredFiles[0]);
                config.ModelConfig.Whisper.Decoder = FilePath(_model.RequiredFiles[1]);
                config.ModelConfig.Whisper.Language = TranscriptionLanguageSelection.Resolve(_model);
                config.ModelConfig.Whisper.Task = "transcribe";
                // The official INT8 release archives do not include Whisper's
                // cross-attention outputs, so sherpa cannot produce aligned token
                // timestamps. Muesli still creates bounded, proportional segments.
                config.ModelConfig.Whisper.EnableTokenTimestamps = 0;
                config.ModelConfig.Whisper.EnableSegmentTimestamps = 0;
                config.ModelConfig.Tokens = FilePath(_model.RequiredFiles[2]);
                break;
            case NativeAsrModelKind.SenseVoice:
                config.ModelConfig.SenseVoice.Model = FilePath("model.int8.onnx");
                config.ModelConfig.SenseVoice.Language = "auto";
                config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                config.ModelConfig.Tokens = FilePath("tokens.txt");
                break;
            case NativeAsrModelKind.Qwen3Asr:
                config.ModelConfig.Qwen3Asr.ConvFrontend = FilePath("conv_frontend.onnx");
                config.ModelConfig.Qwen3Asr.Encoder = FilePath("encoder.int8.onnx");
                config.ModelConfig.Qwen3Asr.Decoder = FilePath("decoder.int8.onnx");
                config.ModelConfig.Qwen3Asr.Tokenizer = FilePath("tokenizer");
                config.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
                config.ModelConfig.Qwen3Asr.MaxNewTokens = 512;
                config.ModelConfig.Qwen3Asr.Temperature = 0.000001f;
                config.ModelConfig.Qwen3Asr.TopP = 0.8f;
                config.ModelConfig.Qwen3Asr.Seed = 42;
                break;
            case NativeAsrModelKind.CohereTranscribe:
                config.ModelConfig.CohereTranscribe.Encoder = FilePath("encoder.int8.onnx");
                config.ModelConfig.CohereTranscribe.Decoder = FilePath("decoder.int8.onnx");
                config.ModelConfig.CohereTranscribe.Language = TranscriptionLanguageSelection.Resolve(_model);
                config.ModelConfig.CohereTranscribe.UsePunct = 1;
                config.ModelConfig.CohereTranscribe.UseItn = 1;
                config.ModelConfig.Tokens = FilePath("tokens.txt");
                break;
            default:
                throw new InvalidOperationException($"Unsupported native model kind: {_model.Kind}.");
        }

        return config;
    }

    private async Task EnsureModelFilesAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_model.ArchiveSha256.Length != 64)
        {
            throw new InvalidOperationException($"{_model.DisplayName} does not have a pinned archive checksum.");
        }

        await DownloadGate.WaitAsync(cancellationToken);
        FileStream? processLock = null;
        try
        {
            Directory.CreateDirectory(TranscriptionModelCatalog.ModelCacheDirectory);
            processLock = await CrossProcessFileLock.AcquireAsync(
                Path.Combine(TranscriptionModelCatalog.ModelCacheDirectory, $".muesli-{_model.Id}-setup.lock"),
                TimeSpan.FromMinutes(30),
                cancellationToken);
            ModelSetupArtifactCleaner.Cleanup(TranscriptionModelCatalog.ModelCacheDirectory, _model.DirectoryName);
            var verificationPath = VerificationPath;
            PruneUnusedWhisperWeights(_model.ModelPath);
            if (_model.IsCached && TryUseVerificationStamp(verificationPath))
            {
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                return;
            }

            // Existing caches from earlier Windows builds are adopted only after
            // every required file matches the newly pinned content hash.
            if (_model.IsCached)
            {
                try
                {
                    await VerifyRequiredFilesAsync(_model.ModelPath, progress, cancellationToken);
                    await WriteVerificationStampAsync(verificationPath, cancellationToken);
                    progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                    return;
                }
                catch (InvalidDataException)
                {
                    Directory.Delete(_model.ModelPath, recursive: true);
                }
            }

            if (Directory.Exists(_model.ModelPath))
            {
                Directory.Delete(_model.ModelPath, recursive: true);
            }

            var archivePath = Path.Combine(
                TranscriptionModelCatalog.ModelCacheDirectory,
                $".{_model.DirectoryName}.{Guid.NewGuid():N}.download");
            var stagingPath = Path.Combine(
                TranscriptionModelCatalog.ModelCacheDirectory,
                $".{_model.DirectoryName}.{Guid.NewGuid():N}.staging");
            try
            {
                await DownloadArchiveAsync(archivePath, progress, cancellationToken);
                progress?.Report(new ModelDownloadProgress($"Extracting {_model.DisplayName}", 0, null));
                Directory.CreateDirectory(stagingPath);
                using var archive = ArchiveFactory.OpenArchive(archivePath, null);
                SafeArchiveExtractor.ExtractSafely(archive, stagingPath, cancellationToken);

                var stagedModelPath = Path.Combine(stagingPath, _model.DirectoryName);
                await VerifyRequiredFilesAsync(stagedModelPath, progress, cancellationToken);
                PruneUnusedWhisperWeights(stagedModelPath);
                Directory.Move(stagedModelPath, _model.ModelPath);
                await WriteVerificationStampAsync(verificationPath, cancellationToken);
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteDirectory(stagingPath);
            }
        }
        finally
        {
            processLock?.Dispose();
            DownloadGate.Release();
        }
    }

    private async Task DownloadArchiveAsync(
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_model.ArchiveUrl, UriKind.Absolute);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Model downloads must use HTTPS.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
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
                progress?.Report(new ModelDownloadProgress($"Downloading {_model.DisplayName}", received, totalBytes));
            }
        }

        await using var stream = File.OpenRead(destinationPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(_model.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{_model.DisplayName} archive verification failed. Expected {_model.ArchiveSha256}; found {actual}.");
        }
    }

    private async Task<bool> TryAdoptVerifiedArchiveCacheAsync(
        string verificationPath,
        CancellationToken cancellationToken)
    {
        var archiveName = Path.GetFileName(new Uri(_model.ArchiveUrl, UriKind.Absolute).LocalPath);
        var archivePath = Path.Combine(
            MuesliPathService.UserProfileDirectory,
            ".cache",
            "muesli",
            "model-archives",
            archiveName);
        if (!File.Exists(archivePath))
        {
            return false;
        }

        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(_model.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ValidateRequiredFiles(_model.ModelPath);
        await WriteVerificationStampAsync(verificationPath, cancellationToken);
        return true;
    }

    private void ValidateRequiredFiles(string modelPath)
    {
        foreach (var relativePath in _model.RequiredFiles)
        {
            if (!File.Exists(Path.Combine(modelPath, relativePath)))
            {
                throw new InvalidDataException($"The {_model.DisplayName} archive is missing {relativePath}.");
            }
        }
    }

    private async Task VerifyRequiredFilesAsync(
        string modelPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var verified = 0L;
        foreach (var expected in _model.RequiredFileSha256)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModelDownloadProgress(
                $"Verifying {_model.DisplayName} ({expected.Key})",
                verified,
                _model.RequiredFileSha256.Count));
            var path = Path.Combine(modelPath, expected.Key);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"The {_model.DisplayName} archive is missing {expected.Key}.");
            }

            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(expected.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{_model.DisplayName} verification failed for {expected.Key}. Expected SHA-256 {expected.Value}; found {actual}.");
            }

            verified++;
        }
    }

    private void PruneUnusedWhisperWeights(string modelPath)
    {
        if (_model.Kind != NativeAsrModelKind.Whisper || !Directory.Exists(modelPath))
        {
            return;
        }

        var unusedFullPrecisionFiles = _model.RequiredFiles
            .Where(file => file.EndsWith(".int8.onnx", StringComparison.OrdinalIgnoreCase))
            .Select(file => file[..^".int8.onnx".Length] + ".onnx");
        foreach (var fileName in unusedFullPrecisionFiles)
        {
            var path = Path.Combine(modelPath, fileName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Pruning is a disk-space optimization, not a readiness requirement.
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only shared caches can keep the unused full-precision files.
            }
        }
    }

    private bool TryUseVerificationStamp(string path)
        => HasValidVerificationStamp(_model, path);

    internal static bool HasValidVerificationStamp(TranscriptionModelDefinition model)
        => HasValidVerificationStamp(model, Path.Combine(model.ModelPath, ".muesli-verified.json"));

    private static bool HasValidVerificationStamp(TranscriptionModelDefinition model, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var stamp = JsonSerializer.Deserialize<ModelVerificationStamp>(File.ReadAllText(path));
            return stamp is not null &&
                   stamp.ArchiveSha256.Equals(model.ArchiveSha256, StringComparison.OrdinalIgnoreCase) &&
                   stamp.Files.Count == model.RequiredFiles.Count &&
                   stamp.Files.All(file =>
                   {
                       var info = new FileInfo(Path.Combine(model.ModelPath, file.Name));
                       return model.RequiredFileSha256.TryGetValue(file.Name, out var expectedHash) &&
                              file.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) &&
                              info.Exists && info.Length == file.Length && info.LastWriteTimeUtc.Ticks == file.LastWriteTimeUtcTicks;
                   });
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteVerificationStampAsync(string path, CancellationToken cancellationToken)
    {
        var files = new List<ModelVerificationFile>();
        foreach (var relativePath in _model.RequiredFiles)
        {
            var info = new FileInfo(Path.Combine(_model.ModelPath, relativePath));
            await using var stream = info.OpenRead();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            files.Add(new ModelVerificationFile(relativePath, hash, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        File.WriteAllText(path, JsonSerializer.Serialize(
            new ModelVerificationStamp(_model.ArchiveSha256, files),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ThreadCount(string provider) => provider.Equals("cuda", StringComparison.OrdinalIgnoreCase)
        ? Math.Clamp(Environment.ProcessorCount, 1, 4)
        : Math.Clamp(Environment.ProcessorCount, 1, 8);

    private static float[] ReadMonoWavSamples(Stream audioStream, out int durationMs)
    {
        using var reader = new WaveFileReader(audioStream);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException("Native transcription expects 16 kHz mono WAV input for live dictation.");
        }

        durationMs = (int)reader.TotalTime.TotalMilliseconds;
        var provider = reader.ToSampleProvider();
        var samples = new float[Math.Max(1, (int)(reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign)))];
        var total = 0;
        while (total < samples.Length)
        {
            var read = provider.Read(samples, total, samples.Length - total);
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
        var outputFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader.ToMono().ToWaveProvider16(), outputFormat)
        {
            ResamplerQuality = 60
        };

        var expectedBytes = (long)Math.Ceiling(reader.TotalTime.TotalSeconds * outputFormat.AverageBytesPerSecond);
        // Throttled: one report per decoded buffer floods the dispatcher and blocks the Cancel button.
        var throttle = new ThrottledTranscriptionProgress(progress);
        var bytes = new byte[Math.Max(
            outputFormat.AverageBytesPerSecond,
            (int)Math.Ceiling(reader.TotalTime.TotalSeconds * outputFormat.AverageBytesPerSecond) + outputFormat.AverageBytesPerSecond)];
        var totalBytes = 0;
        while (true)
        {
            // Decoding a long video is the slowest part of an import, so it has to yield to Cancel.
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes == bytes.Length)
            {
                Array.Resize(ref bytes, checked(bytes.Length * 2));
            }

            var read = resampler.Read(bytes, totalBytes, bytes.Length - totalBytes);
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
        if (sampleCount < 160)
        {
            throw new InvalidDataException($"Windows could not decode an audio track from '{Path.GetFileName(sourcePath)}'.");
        }

        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * sizeof(short)) / 32768f;
        }

        return samples;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        _recognizerGate.Dispose();
    }

    private sealed record ModelVerificationStamp(string ArchiveSha256, List<ModelVerificationFile> Files);
    private sealed record ModelVerificationFile(string Name, string Sha256, long Length, long LastWriteTimeUtcTicks);
}
