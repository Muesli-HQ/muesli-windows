using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using SharpCompress.Archives;
using SherpaOnnx;

namespace Muesli.Windows.Services;

public enum LiveTranscriptOwnershipMode
{
    PreviewOnly,
    UnifiedLiveAndFinal
}

public sealed record StreamingModelDefinition(
    string Id,
    string DisplayName,
    string Summary,
    string Languages,
    string SizeLabel,
    string DirectoryName,
    string ArchiveUrl,
    string ArchiveSha256,
    IReadOnlyDictionary<string, string> RequiredFileSha256,
    string VadUrl,
    string VadSha256)
{
    public string ModelPath => Path.Combine(StreamingModelCatalog.ModelCacheDirectory, DirectoryName);
    public string VadPath => Path.Combine(ModelPath, "silero_vad.onnx");
    public IReadOnlyList<string> RequiredFiles => RequiredFileSha256.Keys.ToList();
    public bool IsCached => RequiredFiles.All(file => File.Exists(Path.Combine(ModelPath, file))) && File.Exists(VadPath);
    public string PickerLabel => $"{DisplayName} · {SizeLabel}";
}

public static class StreamingModelCatalog
{
    public const string Nemotron35Id = "nemotron-3.5-streaming-560ms-int8";
    public static string ModelCacheDirectory => Path.Combine(
        MuesliPathService.UserProfileDirectory,
        ".cache",
        "muesli",
        "streaming-models");

    public static IReadOnlyList<StreamingModelDefinition> Models { get; } =
    [
        new(
            Nemotron35Id,
            "Nemotron 3.5 Streaming",
            "Native sherpa-onnx transducer. 560 ms inference chunks; speech is committed only at VAD boundaries.",
            "19 transcription-ready locales plus 13 broad-coverage locales; automatic language detection",
            "453 MB download · about 650 MB installed",
            "sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2",
            "C6BF5E0DF765F9D5B43BC9E0536D4B4B3E7D40BDF5ECF13E45F134C51C05AE3A",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["encoder.int8.onnx"] = "012E9321373AF99021415E0B0EB3EC827B4BE3153BE6F30D9B448FE65E896E68",
                ["decoder.int8.onnx"] = "19F9C98FC6D0A2C33A65A43B36FDB2E914C26C0AA9764BE3AEBC502A1E982FB0",
                ["joiner.int8.onnx"] = "4101C7C679A0BC30483794B27A059E34E79232AA2068D78D51231A22C8B0D7CE",
                ["tokens.txt"] = "729CC103155BAFA785F9CD45746CD41CABE97EAB7182FC04D594129587958F8A"
            },
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx",
            "9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6")
    ];

    public static StreamingModelDefinition? Get(string? id) =>
        Models.FirstOrDefault(model => model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static StreamingModelDefinition GetRequired(string id) => Get(id) ??
        throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown Windows streaming model.");
}

internal sealed record StreamingVerificationStamp(
    string ArchiveSha256,
    string VadSha256,
    List<StreamingVerificationFile> Files);
internal sealed record StreamingVerificationFile(string Name, string Sha256, long Length, long LastWriteTimeUtcTicks);

public sealed class StreamingModelInstaller
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private readonly StreamingModelDefinition _model;
    private string VerificationPath => Path.Combine(_model.ModelPath, ".muesli-verified.json");

    public StreamingModelInstaller(StreamingModelDefinition model) => _model = model;

    public bool IsVerified => HasValidVerificationStamp(_model);

    public static bool HasValidVerificationStamp(StreamingModelDefinition model)
    {
        try
        {
            var stampPath = Path.Combine(model.ModelPath, ".muesli-verified.json");
            if (!File.Exists(stampPath)) return false;
            var stamp = JsonSerializer.Deserialize<StreamingVerificationStamp>(File.ReadAllText(stampPath));
            var expected = new Dictionary<string, string>(model.RequiredFileSha256, StringComparer.OrdinalIgnoreCase)
            {
                ["silero_vad.onnx"] = model.VadSha256
            };
            return stamp is not null &&
                   stamp.ArchiveSha256.Equals(model.ArchiveSha256, StringComparison.OrdinalIgnoreCase) &&
                   stamp.VadSha256.Equals(model.VadSha256, StringComparison.OrdinalIgnoreCase) &&
                   stamp.Files.Count == expected.Count &&
                   stamp.Files.All(file =>
                   {
                       var info = new FileInfo(Path.Combine(model.ModelPath, file.Name));
                       return expected.TryGetValue(file.Name, out var hash) &&
                              hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) &&
                              info.Exists && info.Length == file.Length &&
                              info.LastWriteTimeUtc.Ticks == file.LastWriteTimeUtcTicks;
                   });
        }
        catch
        {
            return false;
        }
    }

    public async Task PrepareAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await SetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(StreamingModelCatalog.ModelCacheDirectory);
            ModelSetupArtifactCleaner.Cleanup(StreamingModelCatalog.ModelCacheDirectory, _model.DirectoryName);
            if (IsVerified)
            {
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                return;
            }

            if (_model.IsCached)
            {
                try
                {
                    await VerifyAndStampAsync(progress, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (InvalidDataException)
                {
                    Directory.Delete(_model.ModelPath, true);
                }
            }

            var archivePath = Path.Combine(StreamingModelCatalog.ModelCacheDirectory, $".{_model.DirectoryName}.{Guid.NewGuid():N}.download");
            var vadPath = Path.Combine(StreamingModelCatalog.ModelCacheDirectory, $".{_model.DirectoryName}.{Guid.NewGuid():N}.vad.download");
            var stagingPath = Path.Combine(StreamingModelCatalog.ModelCacheDirectory, $".{_model.DirectoryName}.{Guid.NewGuid():N}.staging");
            try
            {
                await DownloadAsync(_model.ArchiveUrl, archivePath, _model.ArchiveSha256, $"Downloading {_model.DisplayName}", progress, cancellationToken).ConfigureAwait(false);
                await DownloadAsync(_model.VadUrl, vadPath, _model.VadSha256, "Downloading Silero VAD", progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelDownloadProgress($"Extracting {_model.DisplayName}", 0, null));
                Directory.CreateDirectory(stagingPath);
                using (var archive = ArchiveFactory.OpenArchive(archivePath, null))
                {
                    SafeArchiveExtractor.ExtractSafely(archive, stagingPath, cancellationToken);
                }
                var stagedModelPath = Path.Combine(stagingPath, _model.DirectoryName);
                if (!Directory.Exists(stagedModelPath)) throw new InvalidDataException("The streaming archive has an unexpected root directory.");
                File.Move(vadPath, Path.Combine(stagedModelPath, "silero_vad.onnx"));
                await VerifyFilesAsync(stagedModelPath, progress, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(_model.ModelPath)) Directory.Delete(_model.ModelPath, true);
                Directory.Move(stagedModelPath, _model.ModelPath);
                await WriteStampAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
            }
            finally
            {
                TryDeleteFile(archivePath);
                TryDeleteFile(vadPath);
                TryDeleteDirectory(stagingPath);
            }
        }
        finally
        {
            SetupGate.Release();
        }
    }

    public async Task VerifyAndStampAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_model.IsCached) throw new InvalidOperationException($"{_model.DisplayName} is not downloaded.");
        await VerifyFilesAsync(_model.ModelPath, progress, cancellationToken).ConfigureAwait(false);
        await WriteStampAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Func<CancellationToken, Task> release, CancellationToken cancellationToken)
    {
        await release(cancellationToken).ConfigureAwait(false);
        await SetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_model.ModelPath)) Directory.Delete(_model.ModelPath, true);
        }
        finally
        {
            SetupGate.Release();
        }
    }

    public long DiskSizeBytes() => Directory.Exists(_model.ModelPath)
        ? Directory.EnumerateFiles(_model.ModelPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
        : 0;

    private async Task VerifyFilesAsync(string root, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var expected = new Dictionary<string, string>(_model.RequiredFileSha256, StringComparer.OrdinalIgnoreCase)
        {
            ["silero_vad.onnx"] = _model.VadSha256
        };
        var index = 0;
        foreach (var file in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ModelDownloadProgress($"Verifying {_model.DisplayName} ({file.Key})", index++, expected.Count));
            var path = Path.Combine(root, file.Key);
            if (!File.Exists(path)) throw new InvalidDataException($"The streaming model is missing {file.Key}.");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Streaming model checksum failed for {file.Key}. Expected {file.Value}; found {actual}.");
        }
    }

    private async Task WriteStampAsync(CancellationToken cancellationToken)
    {
        var expected = _model.RequiredFiles.Append("silero_vad.onnx");
        var files = new List<StreamingVerificationFile>();
        foreach (var name in expected)
        {
            var info = new FileInfo(Path.Combine(_model.ModelPath, name));
            await using var stream = info.OpenRead();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            files.Add(new(name, hash, info.Length, info.LastWriteTimeUtc.Ticks));
        }
        await File.WriteAllTextAsync(VerificationPath, JsonSerializer.Serialize(
            new StreamingVerificationStamp(_model.ArchiveSha256, _model.VadSha256, files),
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadAsync(string url, string destination, string expectedHash, string label, IProgress<ModelDownloadProgress>? progress, CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Streaming model downloads require HTTPS.");
        using var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                progress?.Report(new ModelDownloadProgress(label, received, total));
            }
        }
        await using var verify = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, token));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} checksum failed. Expected {expectedHash}; found {actual}.");
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}

public sealed record StreamingModelSnapshot(
    StreamingModelDefinition Model,
    TranscriptionModelStatus Status,
    string StatusText,
    long DiskSizeBytes,
    string DiskSize,
    string Diagnostics,
    bool IsBusy,
    bool CanPrepare,
    bool CanCancel,
    bool CanRetry,
    bool CanVerify,
    bool CanDelete);

public sealed class StreamingModelLifecycleService : IDisposable
{
    private sealed class State
    {
        public readonly object Gate = new();
        public CancellationTokenSource? Cancellation;
        public TranscriptionModelStatus? Status;
        public string? Detail;
        public Func<CancellationToken, Task>? Retry;
    }
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _selected;
    private readonly Func<CancellationToken, Task> _release;
    private bool _disposed;

    public StreamingModelLifecycleService(Func<string?> selected, Func<CancellationToken, Task> release)
    {
        _selected = selected;
        _release = release;
    }
    public event EventHandler<string>? ModelChanged;

    public StreamingModelSnapshot Snapshot(string id)
    {
        var model = StreamingModelCatalog.GetRequired(id);
        var state = _states.GetOrAdd(id, _ => new());
        TranscriptionModelStatus? active;
        string? detail;
        bool retry;
        lock (state.Gate) { active = state.Status; detail = state.Detail; retry = state.Retry is not null; }
        var installer = new StreamingModelInstaller(model);
        var selected = id.Equals(_selected(), StringComparison.OrdinalIgnoreCase);
        var status = active ?? (!NativeParakeetClient.IsRuntimeAvailable ? TranscriptionModelStatus.RuntimeUnavailable : installer.IsVerified ? selected ? TranscriptionModelStatus.Selected : TranscriptionModelStatus.Ready : TranscriptionModelStatus.Missing);
        var text = detail ?? status switch
        {
            TranscriptionModelStatus.Missing => selected ? "Missing · selected for live meetings" : "Missing",
            TranscriptionModelStatus.Ready => "Ready · checksums verified",
            TranscriptionModelStatus.Selected => "Selected · ready and verified",
            TranscriptionModelStatus.RuntimeUnavailable => "Runtime unavailable",
            TranscriptionModelStatus.DeletionFailed => "Deletion failed",
            _ => status.ToString()
        };
        var bytes = installer.DiskSizeBytes();
        var busy = status is TranscriptionModelStatus.Downloading or TranscriptionModelStatus.Verifying;
        var diagnostics = string.Join(Environment.NewLine,
            $"Model: {model.DisplayName}", $"Model ID: {model.Id}", $"Status: {status} ({text})",
            $"Selected for live meetings: {selected}", $"Installed bytes: {bytes:N0}", $"Directory: {model.ModelPath}",
            $"Archive SHA-256: {model.ArchiveSha256}", $"Silero SHA-256: {model.VadSha256}",
            $"Pinned ASR files: {model.RequiredFiles.Count}", "Runtime: packaged sherpa-onnx online transducer + Silero VAD");
        return new(model, status, text, bytes, TranscriptionModelLifecycleService.FormatBytes(bytes), diagnostics, busy,
            !busy && status != TranscriptionModelStatus.RuntimeUnavailable, busy, retry && !busy,
            !busy && model.IsCached, !busy && bytes > 0);
    }

    public Task PrepareAsync(string id, IProgress<ModelDownloadProgress>? progress = null, CancellationToken token = default) =>
        RunAsync(id, TranscriptionModelStatus.Downloading, "Preparing live model…", (installer, ct) => installer.PrepareAsync(
            new InlineProgress<ModelDownloadProgress>(value => UpdateProgress(id, value,
                value.Stage.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase) ? TranscriptionModelStatus.Verifying : TranscriptionModelStatus.Downloading,
                progress)), ct), false, token);
    public Task VerifyAsync(string id, IProgress<ModelDownloadProgress>? progress = null, CancellationToken token = default) =>
        RunAsync(id, TranscriptionModelStatus.Verifying, "Verifying pinned checksums…", (installer, ct) => installer.VerifyAndStampAsync(
            new InlineProgress<ModelDownloadProgress>(value => UpdateProgress(id, value, TranscriptionModelStatus.Verifying, progress)), ct), false, token);
    public Task DeleteAsync(string id, CancellationToken token = default) =>
        RunAsync(id, TranscriptionModelStatus.Verifying, "Releasing live recognizer and deleting files…", (installer, ct) => installer.DeleteAsync(_release, ct), true, token);
    public Task RetryAsync(string id, CancellationToken token = default)
    {
        var state = _states.GetOrAdd(id, _ => new());
        Func<CancellationToken, Task>? retry;
        lock (state.Gate) retry = state.Retry;
        return retry?.Invoke(token) ?? Task.FromException(new InvalidOperationException("There is no failed live-model operation to retry."));
    }
    public void Cancel(string id) { if (_states.TryGetValue(id, out var state)) lock (state.Gate) state.Cancellation?.Cancel(); }

    private async Task RunAsync(string id, TranscriptionModelStatus status, string detail, Func<StreamingModelInstaller, CancellationToken, Task> operation, bool deletion, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = StreamingModelCatalog.GetRequired(id);
        var state = _states.GetOrAdd(id, _ => new());
        CancellationTokenSource linked;
        lock (state.Gate)
        {
            if (state.Cancellation is not null) throw new InvalidOperationException("A live-model operation is already running.");
            linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            state.Cancellation = linked; state.Status = status; state.Detail = detail;
            state.Retry = retryToken => RunAsync(id, status, detail, operation, deletion, retryToken);
        }
        ModelChanged?.Invoke(this, id);
        try
        {
            await Task.Run(() => operation(new StreamingModelInstaller(model), linked.Token), linked.Token).ConfigureAwait(false);
            lock (state.Gate) { state.Status = null; state.Detail = null; state.Retry = null; }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lock (state.Gate) { state.Status = null; state.Detail = "Cancelled; partial artifacts will be cleaned on retry."; }
            throw;
        }
        catch (Exception ex)
        {
            lock (state.Gate) { state.Status = deletion ? TranscriptionModelStatus.DeletionFailed : TranscriptionModelStatus.Failed; state.Detail = ex.Message; }
            throw;
        }
        finally
        {
            lock (state.Gate) { state.Cancellation?.Dispose(); state.Cancellation = null; }
            ModelChanged?.Invoke(this, id);
        }
    }

    private void UpdateProgress(string id, ModelDownloadProgress value, TranscriptionModelStatus status, IProgress<ModelDownloadProgress>? external)
    {
        var state = _states.GetOrAdd(id, _ => new());
        lock (state.Gate)
        {
            if (state.Cancellation is null) return;
            state.Status = status;
            state.Detail = value.DisplayText;
        }
        external?.Report(value);
        ModelChanged?.Invoke(this, id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _states.Values) lock (state.Gate) state.Cancellation?.Cancel();
    }
}
