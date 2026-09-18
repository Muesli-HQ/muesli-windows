using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using SharpCompress.Archives;
using SherpaOnnx;

namespace Muesli.Windows.Services;

public sealed class NativeDiarizationClient : IDisposable
{
    private const string SegmentationArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2";

    private const string EmbeddingModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/nemo_en_titanet_small.onnx";

    private const string SegmentationDirectoryName = "sherpa-onnx-pyannote-segmentation-3-0";
    private const string EmbeddingModelFileName = "nemo_en_titanet_small.onnx";
    public const string SegmentationArchiveSha256 = "24615EE884C897D9D2BA09BB4D30DA6BB1B15E685065962DB5B02E76E4996488";
    public const string SegmentationModelSha256 = "D582F4B4C6B48205DE7E0643C57DF0DF5615A3C176189BE3FC461E9D18827B5D";
    public const string EmbeddingModelSha256 = "AD4A1802485D8B34C722D2A9D04249662F2ECE5D28A7A039063CA22F515A789E";

    private static readonly SemaphoreSlim ModelSetupGate = new(1, 1);
    private static string? _verificationFailure;
    private static string? _modelSetupFailure;
    private static int _modelSetupInProgress;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineSpeakerDiarization? _diarizer;
    private string _provider = "not initialized";
    private long _initialModelLoadMs;
    private bool _disposed;

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_DIARIZATION_CACHE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "muesli", "native-diarization");

    public static string SegmentationModelPath =>
        Path.Combine(ModelCacheDirectory, SegmentationDirectoryName, "model.int8.onnx");

    public static string EmbeddingModelPath =>
        Path.Combine(ModelCacheDirectory, EmbeddingModelFileName);

    private static string VerificationStampPath => Path.Combine(ModelCacheDirectory, "verified-models-v1.json");

    public static bool IsReady => HasValidVerificationStamp();

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

                _ = typeof(OfflineSpeakerDiarization).Assembly.FullName;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Status => !IsRuntimeAvailable
        ? "Runtime unavailable"
        : Volatile.Read(ref _modelSetupInProgress) != 0
            ? "Setup in progress"
        : _verificationFailure is not null
            ? "Verification failed"
            : _modelSetupFailure is not null
                ? "Setup failed"
            : IsReady
                ? "Ready (verified)"
                : File.Exists(SegmentationModelPath) || File.Exists(EmbeddingModelPath)
                    ? "Needs verification"
                    : "Needs model";

    public static bool ShouldOverlapWithAsr
    {
        get
        {
            var asrProvider = Environment.GetEnvironmentVariable("MUESLI_PARAKEET_PROVIDER");
            var diarizationProvider = Environment.GetEnvironmentVariable("MUESLI_DIARIZATION_PROVIDER");
            return NativeSherpaRuntime.IsCudaCapable &&
                   !string.Equals(asrProvider, "cpu", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(diarizationProvider, "cpu", StringComparison.OrdinalIgnoreCase);
        }
    }

    public async Task<DiarizationResult> DiarizeFileAsync(string filePath)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureModelsAsync();
            // Model construction and diarization are synchronous native work.
            // Keep them off the WPF dispatcher and reuse one initialized model.
            return await Task.Run(() => DiarizeFileCore(filePath));
        }
        finally
        {
            _gate.Release();
        }
    }

    private DiarizationResult DiarizeFileCore(string filePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var samples = ReadMonoSamples(filePath, out var sampleRate);
        var modelWasReused = _diarizer is not null;
        EnsureDiarizer();
        if (_diarizer!.SampleRate != sampleRate)
        {
            throw new InvalidOperationException($"Native diarization expected {_diarizer.SampleRate} Hz audio, got {sampleRate} Hz.");
        }

        var processing = System.Diagnostics.Stopwatch.StartNew();
        var segments = _diarizer.Process(samples)
            .Select(segment => new DiarizedSegment(
                $"speaker_{segment.Speaker}",
                (int)(segment.Start * 1000),
                (int)(segment.End * 1000)))
            .ToList();
        processing.Stop();

        return new DiarizationResult(
            "",
            "en",
            samples.Length * 1000 / sampleRate,
            segments,
            new List<string>
            {
                "ASR engine: native-sherpa-onnx-diarization",
                $"Diarization segments: {segments.Count}",
                $"Diarization provider: {_provider}",
                $"Diarization model reused: {modelWasReused}",
                $"Diarization model load ms: {_initialModelLoadMs}",
                $"Diarization processing ms: {processing.ElapsedMilliseconds}"
            })
        {
            Provider = _provider,
            ModelLoadMs = _initialModelLoadMs,
            ModelReused = modelWasReused,
            ProcessingMs = processing.ElapsedMilliseconds
        };
    }

    private void EnsureDiarizer()
    {
        if (_diarizer is not null)
        {
            return;
        }

        Exception? lastError = null;
        foreach (var provider in PreferredProviders())
        {
            try
            {
                var load = System.Diagnostics.Stopwatch.StartNew();
                _diarizer = new OfflineSpeakerDiarization(BuildConfig(provider));
                load.Stop();
                _provider = provider;
                _initialModelLoadMs = load.ElapsedMilliseconds;
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                _diarizer?.Dispose();
                _diarizer = null;
                if (ProviderWasExplicitlyConfigured())
                {
                    throw new InvalidOperationException(
                        $"Diarization provider '{provider}' could not initialize: {exception.Message}",
                        exception);
                }
            }
        }

        throw new InvalidOperationException("No native diarization provider could initialize.", lastError);
    }

    private static OfflineSpeakerDiarizationConfig BuildConfig(string provider)
    {
        var config = new OfflineSpeakerDiarizationConfig
        {
            MinDurationOn = 0.30f,
            MinDurationOff = 0.50f
        };
        config.Segmentation.Pyannote.Model = SegmentationModelPath;
        config.Segmentation.NumThreads = ThreadCount(provider);
        config.Segmentation.Provider = provider;
        config.Embedding.Model = EmbeddingModelPath;
        config.Embedding.NumThreads = ThreadCount(provider);
        config.Embedding.Provider = provider;
        config.Clustering.Threshold = 0.50f;
        return config;
    }

    private static IEnumerable<string> PreferredProviders()
    {
        var configured = Environment.GetEnvironmentVariable("MUESLI_DIARIZATION_PROVIDER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured.Trim().ToLowerInvariant() switch
            {
                "gpu" => "cuda",
                var value => value
            };
            yield break;
        }

        if (NativeSherpaRuntime.IsCudaCapable)
        {
            yield return "cuda";
        }

        yield return "cpu";
    }

    private static bool ProviderWasExplicitlyConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUESLI_DIARIZATION_PROVIDER"));

    private static int ThreadCount(string provider)
    {
        var configured = Environment.GetEnvironmentVariable("MUESLI_DIARIZATION_THREADS");
        if (int.TryParse(configured, out var count) && count > 0)
        {
            return Math.Clamp(count, 1, 32);
        }

        return provider.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
            : Math.Max(1, Math.Min(Environment.ProcessorCount, 8));
    }

    public static async Task EnsureModelsAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ModelSetupGate.WaitAsync(cancellationToken);
        Interlocked.Exchange(ref _modelSetupInProgress, 1);
        FileStream? processLock = null;
        try
        {
            Directory.CreateDirectory(ModelCacheDirectory);
            processLock = await CrossProcessFileLock.AcquireAsync(
                Path.Combine(ModelCacheDirectory, ".muesli-diarization-model-setup.lock"),
                TimeSpan.FromMinutes(10),
                cancellationToken);
            ModelSetupArtifactCleaner.Cleanup(ModelCacheDirectory, SegmentationDirectoryName);
            ModelSetupArtifactCleaner.Cleanup(ModelCacheDirectory, EmbeddingModelFileName);
            if (HasValidVerificationStamp())
            {
                _verificationFailure = null;
                _modelSetupFailure = null;
                return;
            }

            if (!VerifySha256(SegmentationModelPath, SegmentationModelSha256).Matches)
            {
                await DownloadAndExtractSegmentationAsync(progress, cancellationToken);
            }
            if (!VerifySha256(EmbeddingModelPath, EmbeddingModelSha256).Matches)
            {
                TryDelete(EmbeddingModelPath);
                await DownloadFileAsync(
                    EmbeddingModelUrl,
                    EmbeddingModelPath,
                    EmbeddingModelSha256,
                    "Downloading verified Titanet speaker model",
                    progress,
                    cancellationToken);
            }

            var segmentation = VerifySha256(SegmentationModelPath, SegmentationModelSha256);
            var embedding = VerifySha256(EmbeddingModelPath, EmbeddingModelSha256);
            if (!segmentation.Matches || !embedding.Matches)
            {
                _verificationFailure = !segmentation.Matches
                    ? $"Segmentation model SHA-256 mismatch: {segmentation.ActualHash ?? "missing"}."
                    : $"Embedding model SHA-256 mismatch: {embedding.ActualHash ?? "missing"}.";
                throw new InvalidDataException(_verificationFailure);
            }

            WriteVerificationStamp(segmentation, embedding);
            _verificationFailure = null;
            _modelSetupFailure = null;
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
            ModelSetupGate.Release();
        }
    }

    public static void OpenModelCacheDirectory()
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ModelCacheDirectory,
            UseShellExecute = true
        });
    }

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

    public static void ClearModelCache()
    {
        if (Directory.Exists(ModelCacheDirectory))
        {
            Directory.Delete(ModelCacheDirectory, recursive: true);
        }
    }

    public static ModelHashVerification VerifySha256(string path, string expectedHash)
    {
        if (!File.Exists(path))
        {
            return new ModelHashVerification(path, expectedHash, null, false, 0, DateTimeOffset.MinValue);
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        var info = new FileInfo(path);
        return new ModelHashVerification(
            path,
            expectedHash,
            actual,
            actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase),
            info.Length,
            info.LastWriteTimeUtc);
    }

    private static async Task DownloadAndExtractSegmentationAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = Path.Combine(ModelCacheDirectory, $".{SegmentationDirectoryName}.{Guid.NewGuid():N}.download");
        var stagingPath = Path.Combine(ModelCacheDirectory, $".{SegmentationDirectoryName}.{Guid.NewGuid():N}.staging");
        try
        {
            await DownloadFileAsync(
                SegmentationArchiveUrl,
                archivePath,
                SegmentationArchiveSha256,
                "Downloading verified pyannote segmentation model",
                progress,
                cancellationToken);
            progress?.Report(new ModelDownloadProgress("Extracting verified diarization model", 0, null));
            Directory.CreateDirectory(stagingPath);
            using (var archive = ArchiveFactory.OpenArchive(archivePath, null))
            {
                SafeArchiveExtractor.ExtractSafely(archive, stagingPath, cancellationToken);
            }

            var stagedModelPath = Path.Combine(stagingPath, SegmentationDirectoryName, "model.int8.onnx");
            var verification = VerifySha256(stagedModelPath, SegmentationModelSha256);
            if (!verification.Matches)
            {
                throw new InvalidDataException(
                    $"Diarization segmentation model hash mismatch. Expected {SegmentationModelSha256}; found {verification.ActualHash ?? "missing"}.");
            }

            var destinationDirectory = Path.Combine(ModelCacheDirectory, SegmentationDirectoryName);
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }
            Directory.Move(Path.Combine(stagingPath, SegmentationDirectoryName), destinationDirectory);
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(stagingPath);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string expectedHash,
        string displayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Model downloads must use HTTPS.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ModelCacheDirectory);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ?? ModelCacheDirectory;
        var destinationName = Path.GetFileName(destinationPath).TrimStart('.');
        var tempPath = Path.Combine(destinationDirectory, $".{destinationName}.{Guid.NewGuid():N}.partial");
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new ModelDownloadProgress(displayName, received, total));
                }
                output.Flush(flushToDisk: true);
            }

            var verification = VerifySha256(tempPath, expectedHash);
            if (!verification.Matches)
            {
                throw new InvalidDataException(
                    $"Downloaded model hash mismatch. Expected {expectedHash}; found {verification.ActualHash ?? "missing"}.");
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool HasValidVerificationStamp()
    {
        if (!File.Exists(SegmentationModelPath) || !File.Exists(EmbeddingModelPath) || !File.Exists(VerificationStampPath))
        {
            return false;
        }

        try
        {
            var stamp = JsonSerializer.Deserialize<DiarizationVerificationStamp>(File.ReadAllText(VerificationStampPath));
            var segmentation = new FileInfo(SegmentationModelPath);
            var embedding = new FileInfo(EmbeddingModelPath);
            return stamp is not null &&
                   stamp.SchemaVersion == 1 &&
                   stamp.SegmentationSha256.Equals(SegmentationModelSha256, StringComparison.OrdinalIgnoreCase) &&
                   stamp.EmbeddingSha256.Equals(EmbeddingModelSha256, StringComparison.OrdinalIgnoreCase) &&
                   stamp.SegmentationLength == segmentation.Length &&
                   stamp.EmbeddingLength == embedding.Length &&
                   stamp.SegmentationLastWriteUtc == segmentation.LastWriteTimeUtc &&
                   stamp.EmbeddingLastWriteUtc == embedding.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteVerificationStamp(ModelHashVerification segmentation, ModelHashVerification embedding)
    {
        var stamp = new DiarizationVerificationStamp(
            1,
            SegmentationModelSha256,
            EmbeddingModelSha256,
            segmentation.Length,
            embedding.Length,
            segmentation.LastWriteUtc.UtcDateTime,
            embedding.LastWriteUtc.UtcDateTime);
        var temporaryPath = $"{VerificationStampPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, VerificationStampPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a locked artifact can be retried on the next setup attempt.
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
            // Partial staging data is never loaded and can be cleaned on the next setup attempt.
        }
    }

    private static float[] ReadMonoSamples(string filePath, out int sampleRate)
    {
        using var reader = new AudioFileReader(filePath);
        if (reader.WaveFormat.Channels != 1)
        {
            throw new InvalidOperationException("Native diarization requires mono audio.");
        }

        sampleRate = reader.WaveFormat.SampleRate;
        var samples = new List<float>((int)(reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign)));
        var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.Take(read));
        }

        return samples.ToArray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _diarizer?.Dispose();
        _diarizer = null;
        _gate.Dispose();
    }
}

public sealed record ModelHashVerification(
    string Path,
    string ExpectedHash,
    string? ActualHash,
    bool Matches,
    long Length,
    DateTimeOffset LastWriteUtc);

public sealed record DiarizationVerificationStamp(
    int SchemaVersion,
    string SegmentationSha256,
    string EmbeddingSha256,
    long SegmentationLength,
    long EmbeddingLength,
    DateTime SegmentationLastWriteUtc,
    DateTime EmbeddingLastWriteUtc);
