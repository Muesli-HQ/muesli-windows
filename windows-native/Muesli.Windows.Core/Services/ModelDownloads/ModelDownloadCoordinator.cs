using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>
/// The single engine for every model download, ported from the macOS ModelDownloadCoordinator.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Resumable: bytes land in <c>&lt;file&gt;.part</c>; a retry or a later attempt continues
/// with an HTTP Range request guarded by the stored ETag (If-Range), so a changed server file is
/// never stitched onto old bytes.</item>
/// <item>Fast: files at or above the segment threshold download as several parallel ranges into a
/// preallocated part file, with per-segment progress saved for resume. Up to
/// <see cref="ModelDownloadManifest.MaximumConcurrency"/> files transfer at once.</item>
/// <item>Retries: three attempts per file with 1 s and 2 s backoff plus jitter. Integrity,
/// disk-space and client errors fail fast.</item>
/// <item>Stalls: a read that makes no progress for <see cref="StallTimeout"/> is retried.</item>
/// <item>Safe: free space is checked up front, every file is verified by size and SHA-256 before
/// it replaces anything, and installs are atomic moves from the part file.</item>
/// <item>Progress: a snapshot at most every 100 ms with bytes, files, speed and time remaining.</item>
/// </list>
/// Cancelling keeps part files and resume state, so the next download continues where it left off.
/// </remarks>
public sealed class ModelDownloadCoordinator
{
    public const string StateFileName = ".muesli-download-state.json";
    public const string CompletionFileName = ".muesli-managed-model-complete.json";
    public const string PartSuffix = ".part";
    public const int MaxAttempts = 3;
    public const long DefaultSegmentThresholdBytes = 64L * 1024 * 1024;
    public const int DefaultSegmentCount = 4;
    public static readonly long DiskSpaceReserveBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private const int BufferSize = 1024 * 1024;
    private static readonly TimeSpan StateSaveInterval = TimeSpan.FromSeconds(1);
    private static readonly Lazy<ModelDownloadCoordinator> SharedInstance = new(() => new ModelDownloadCoordinator());

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<string, long?> _freeSpace;
    private readonly long _segmentThreshold;
    private readonly int _segmentCount;
    private readonly TimeSpan _stallTimeout;
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public static ModelDownloadCoordinator Shared => SharedInstance.Value;

    public ModelDownloadCoordinator(
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, long?>? freeSpace = null,
        long segmentThresholdBytes = DefaultSegmentThresholdBytes,
        int segmentCount = DefaultSegmentCount,
        TimeSpan? stallTimeout = null)
    {
        _http = httpClient ?? CreateDefaultClient();
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        _freeSpace = freeSpace ?? AvailableFreeSpace;
        _segmentThreshold = segmentThresholdBytes;
        _segmentCount = Math.Max(1, segmentCount);
        _stallTimeout = stallTimeout ?? StallTimeout;
    }

    private static HttpClient CreateDefaultClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };
        // Stalls are detected per read; a whole-transfer timeout would cut off slow, healthy links.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Downloads every manifest file into <paramref name="destinationDirectory"/>, resuming any
    /// earlier partial attempt. Throws <see cref="ModelDownloadException"/> on failure and
    /// <see cref="OperationCanceledException"/> when cancelled (partial bytes are kept).
    /// </summary>
    public async Task DownloadAsync(
        ModelDownloadManifest manifest,
        string destinationDirectory,
        IProgress<ModelDownloadSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Files.Count == 0) throw new ArgumentException("A model download needs at least one file.", nameof(manifest));

        var root = Path.GetFullPath(destinationDirectory);
        if (!_inFlight.TryAdd(root, 0))
        {
            throw new InvalidOperationException($"A download into {root} is already running.");
        }

        var tracker = new ProgressTracker(manifest, progress);
        DownloadState? state = null;
        try
        {
            var targets = manifest.Files.Select(file => new Target(file, ResolveInside(root, file.RelativePath))).ToList();
            Directory.CreateDirectory(root);
            state = DownloadState.Load(root, manifest);

            // Adopt what is already on disk: a verified final file counts as done; a part file is
            // resumable only with the ETag it was downloaded against.
            tracker.SetPhase(ModelDownloadPhase.Verifying, "Checking existing files");
            var pending = new List<Target>();
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(target.FinalPath))
                {
                    if (await IsValidAsync(target.FinalPath, target.File, cancellationToken))
                    {
                        tracker.MarkPresent(target.File.RelativePath, new FileInfo(target.FinalPath).Length, completed: true);
                        continue;
                    }

                    File.Delete(target.FinalPath);
                }

                tracker.MarkPresent(target.File.RelativePath, ResumableBytes(target, state), completed: false);
                pending.Add(target);
            }

            EnsureDiskSpace(root, targets, tracker);

            if (pending.Count > 0)
            {
                tracker.SetPhase(ModelDownloadPhase.Downloading, "Starting download");
                await DownloadPendingAsync(manifest, pending, state, tracker, cancellationToken);
            }

            tracker.SetPhase(ModelDownloadPhase.Verifying, "Verifying files");
            foreach (var target in pending)
            {
                if (!await IsValidAsync(target.FinalPath, target.File, cancellationToken))
                {
                    TryDelete(target.FinalPath);
                    throw new ModelDownloadException(
                        ModelDownloadFailure.ChecksumMismatch,
                        $"{target.File.RelativePath} failed verification after download.");
                }
            }

            WriteCompletionStamp(root, manifest, targets);
            state.Delete();
            tracker.SetPhase(ModelDownloadPhase.Ready, "Model ready", force: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state?.Save();
            tracker.SetPhase(ModelDownloadPhase.Paused, "Paused", force: true);
            throw;
        }
        catch (ModelDownloadException exception)
        {
            state?.Save();
            tracker.SetPhase(ModelDownloadPhase.Failed, exception.Message, force: true);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(root, out _);
        }
    }

    /// <summary>
    /// True when the completion stamp written by a successful download matches this manifest and
    /// every file is present at its recorded size. No hashing, so it is cheap enough for launch.
    /// </summary>
    public static bool IsComplete(ModelDownloadManifest manifest, string destinationDirectory)
    {
        try
        {
            var root = Path.GetFullPath(destinationDirectory);
            var stampPath = Path.Combine(root, CompletionFileName);
            if (!File.Exists(stampPath)) return false;
            var stamp = JsonSerializer.Deserialize<CompletionStamp>(File.ReadAllText(stampPath));
            if (stamp is null ||
                !string.Equals(stamp.ModelId, manifest.ModelId, StringComparison.Ordinal) ||
                !string.Equals(stamp.Version, manifest.Version, StringComparison.Ordinal))
            {
                return false;
            }

            var sizes = stamp.Files.ToDictionary(file => file.Path, file => file.Bytes, StringComparer.Ordinal);
            return manifest.Files.All(file =>
            {
                var path = Path.Combine(root, file.RelativePath);
                return sizes.TryGetValue(file.RelativePath, out var bytes) &&
                       File.Exists(path) &&
                       new FileInfo(path).Length == bytes;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // ─── Scheduling ──────────────────────────────────────────────────────────────────────────

    private async Task DownloadPendingAsync(
        ModelDownloadManifest manifest,
        IReadOnlyList<Target> pending,
        DownloadState state,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(Math.Max(1, manifest.MaximumConcurrency));
        ModelDownloadException? firstFailure = null;

        var tasks = pending.Select(async target =>
        {
            await slots.WaitAsync(failFast.Token);
            try
            {
                await DownloadWithRetriesAsync(target, state, tracker, failFast.Token);
            }
            catch (ModelDownloadException exception)
            {
                Interlocked.CompareExchange(ref firstFailure, exception, null);
                failFast.Cancel();
            }
            finally
            {
                slots.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (firstFailure is not null && !cancellationToken.IsCancellationRequested)
        {
        }

        if (firstFailure is not null) throw firstFailure;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task DownloadWithRetriesAsync(Target target, DownloadState state, ProgressTracker tracker, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                tracker.Retry(target.File.RelativePath, attempt - 1);
                var backoff = TimeSpan.FromSeconds(attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
                await _delay(backoff, cancellationToken);
            }

            try
            {
                try
                {
                    tracker.Begin(target.File.RelativePath);
                    if (UseSegments(target.File))
                    {
                        await DownloadSegmentedAsync(target, state, tracker, cancellationToken);
                    }
                    else
                    {
                        await DownloadStreamAsync(target, state, tracker, cancellationToken);
                    }

                    File.Move(target.PartPath, target.FinalPath, overwrite: true);
                }
                catch (IOException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw MapIo(exception);
                }

                state.Forget(target.File.RelativePath);
                tracker.Complete(target.File.RelativePath);
                return;
            }
            catch (ModelDownloadException exception) when (exception.IsRetryable && attempt < MaxAttempts)
            {
            }
            catch (ModelDownloadException exception) when (exception.IsRetryable)
            {
                throw new ModelDownloadException(
                    exception.Failure,
                    $"Could not download {target.File.RelativePath} after three attempts: {exception.Message}",
                    exception,
                    exception.StatusCode);
            }
        }
    }

    private bool UseSegments(ModelDownloadFile file) =>
        _segmentCount > 1 && file.ExpectedBytes is { } size && size >= _segmentThreshold;

    // ─── Single-stream transfer ──────────────────────────────────────────────────────────────

    private async Task DownloadStreamAsync(Target target, DownloadState state, ProgressTracker tracker, CancellationToken cancellationToken)
    {
        var path = target.File.RelativePath;
        var expected = target.File.ExpectedBytes;
        if (state.SegmentsFor(path) is not null)
        {
            // A segmented part file is preallocated to full size; as a stream resume point it would
            // look complete while holding unwritten zeros.
            state.Forget(path);
            TryDelete(target.PartPath);
        }

        var retried416 = false;

        while (true)
        {
            var existing = ResumableBytes(target, state);
            if (existing == 0) TryDelete(target.PartPath);
            if (expected is { } full && existing == full) return;

            using var request = new HttpRequestMessage(HttpMethod.Get, target.File.Source);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(Quote(state.ETagFor(path)!)));
            }

            using var response = await SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && !retried416)
            {
                // The part file no longer matches the server; start that file over once, for free.
                retried416 = true;
                TryDelete(target.PartPath);
                state.Forget(path);
                tracker.MarkPresent(path, 0, completed: false);
                continue;
            }

            EnsureSuccess(response, path);
            var resuming = existing > 0 &&
                           response.StatusCode == HttpStatusCode.PartialContent &&
                           response.Content.Headers.ContentRange?.From == existing;
            if (!resuming) existing = 0;

            var etag = response.Headers.ETag?.Tag;
            if (!string.IsNullOrWhiteSpace(etag)) state.SetETag(path, Unquote(etag));
            state.Save();

            var contentLength = response.Content.Headers.ContentLength;
            if (expected is { } want && contentLength is { } length && length != want - existing)
            {
                throw new ModelDownloadException(
                    ModelDownloadFailure.InvalidContentLength,
                    $"{path}: the server sent {length} bytes where {want - existing} were expected.");
            }

            tracker.MarkPresent(path, existing, completed: false);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                target.PartPath, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            var written = await CopyWithStallDetectionAsync(input, (buffer, count, token) =>
                output.WriteAsync(buffer.AsMemory(0, count), token), path, tracker, cancellationToken);
            await output.FlushAsync(cancellationToken);

            var total = existing + written;
            var targetLength = expected ?? contentLength + existing;
            if (targetLength is { } fullLength && total < fullLength)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network, $"{path}: the connection closed early.");
            }

            return;
        }
    }

    // ─── Segmented transfer ──────────────────────────────────────────────────────────────────

    private async Task DownloadSegmentedAsync(Target target, DownloadState state, ProgressTracker tracker, CancellationToken cancellationToken)
    {
        var path = target.File.RelativePath;
        var size = target.File.ExpectedBytes!.Value;

        var probe = await ProbeAsync(target.File.Source, cancellationToken);
        if (probe is not { AcceptsRanges: true, ETag: not null } || probe.Length != size)
        {
            // No resumable ranges here (or a size we cannot trust); one stream still works.
            await DownloadStreamAsync(target, state, tracker, cancellationToken);
            return;
        }

        var segments = state.SegmentsFor(path);
        if (segments is null || !string.Equals(state.ETagFor(path), probe.ETag, StringComparison.Ordinal) || !File.Exists(target.PartPath))
        {
            TryDelete(target.PartPath);
            segments = PlanSegments(size, _segmentCount);
            state.SetETag(path, probe.ETag);
            state.SetSegments(path, segments);
            state.Save();
        }

        using (var allocate = new FileStream(target.PartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (allocate.Length != size) allocate.SetLength(size);
        }

        tracker.MarkPresent(path, segments.Sum(segment => segment.Done), completed: false);
        using var handle = File.OpenHandle(target.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous);
        var saveClock = Stopwatch.StartNew();
        var saveGate = new object();

        void MaybeSave(bool force)
        {
            lock (saveGate)
            {
                if (!force && saveClock.Elapsed < StateSaveInterval) return;
                saveClock.Restart();
                state.Save();
            }
        }

        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstError = new StrongBox<Exception?>();
        try
        {
            await Task.WhenAll(segments.Where(segment => segment.Remaining > 0).Select(segment =>
                DownloadSegmentAsync(target, probe.ETag, segment, handle, tracker, () => MaybeSave(false), failFast, firstError)));
        }
        catch (Exception) when (firstError.Value is not null && !cancellationToken.IsCancellationRequested)
        {
            // Siblings were cancelled by the first failure; report that failure, not their cancellation.
        }
        finally
        {
            MaybeSave(true);
        }

        if (firstError.Value is RangeNotHonouredException)
        {
            // The server ignored a range: fall back to one stream, starting the file over.
            handle.Dispose();
            await DownloadStreamAsync(target, state, tracker, cancellationToken);
            return;
        }

        if (firstError.Value is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        cancellationToken.ThrowIfCancellationRequested();

        if (segments.Any(segment => segment.Remaining > 0))
        {
            throw new ModelDownloadException(ModelDownloadFailure.Network, $"{path}: a range ended before it was complete.");
        }
    }

    private async Task DownloadSegmentAsync(
        Target target,
        string etag,
        SegmentState segment,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        ProgressTracker tracker,
        Action saveProgress,
        CancellationTokenSource failFast,
        StrongBox<Exception?> firstError)
    {
        var path = target.File.RelativePath;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.File.Source);
            request.Headers.Range = new RangeHeaderValue(segment.Start + segment.Done, segment.End);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(Quote(etag)));
            using var response = await SendAsync(request, failFast.Token);
            EnsureSuccess(response, path);
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                response.Content.Headers.ContentRange?.From != segment.Start + segment.Done)
            {
                // The server ignored the range (or the file changed): resume state is unusable.
                throw new RangeNotHonouredException();
            }

            await using var input = await response.Content.ReadAsStreamAsync(failFast.Token);
            await CopyWithStallDetectionAsync(input, async (buffer, count, token) =>
            {
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, count), segment.Start + segment.Done, token);
                segment.Done += count;
                saveProgress();
            }, path, tracker, failFast.Token);
        }
        catch (Exception exception) when (!failFast.IsCancellationRequested)
        {
            Interlocked.CompareExchange(ref firstError.Value, MapIo(exception), null);
            failFast.Cancel();
            throw;
        }
    }

    private sealed class RangeNotHonouredException : Exception;

    /// <summary>Disk errors surface as typed failures: a full disk fails fast, other I/O is retried.</summary>
    private static Exception MapIo(Exception exception) => exception switch
    {
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) =>
            new ModelDownloadException(ModelDownloadFailure.InsufficientDiskSpace, "Not enough disk space to finish the download.", io),
        IOException io => new ModelDownloadException(ModelDownloadFailure.Network, io.Message, io),
        _ => exception
    };

    internal static List<SegmentState> PlanSegments(long size, int count)
    {
        var segments = new List<SegmentState>();
        var length = size / count;
        for (var index = 0; index < count; index++)
        {
            var start = index * length;
            var end = index == count - 1 ? size - 1 : start + length - 1;
            segments.Add(new SegmentState { Start = start, End = end, Done = 0 });
        }

        return segments;
    }

    private async Task<Probe?> ProbeAsync(Uri source, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, source);
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var acceptsRanges = response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase);
            var etag = response.Headers.ETag?.Tag;
            return new Probe(acceptsRanges, string.IsNullOrWhiteSpace(etag) ? null : Unquote(etag), response.Content.Headers.ContentLength);
        }
        catch (ModelDownloadException)
        {
            return null;
        }
    }

    // ─── Transfer helpers ────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Stalled, "The server stopped responding.");
        }
        catch (HttpRequestException exception)
        {
            throw new ModelDownloadException(ModelDownloadFailure.Network, exception.Message, exception);
        }
    }

    private async Task<long> CopyWithStallDetectionAsync(
        Stream input,
        Func<byte[], int, CancellationToken, ValueTask> write,
        string path,
        ProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long written = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(_stallTimeout);
            int read;
            try
            {
                read = await input.ReadAsync(buffer, stall.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Stalled, $"{path}: the download stalled.");
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            {
                throw new ModelDownloadException(ModelDownloadFailure.Network, $"{path}: {exception.Message}", exception);
            }

            if (read == 0) return written;
            await write(buffer, read, cancellationToken);
            written += read;
            tracker.Add(path, read);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        throw new ModelDownloadException(
            ModelDownloadFailure.HttpStatus,
            $"{path}: the server returned {code} {response.ReasonPhrase}.",
            statusCode: code);
    }

    private static long ResumableBytes(Target target, DownloadState state)
    {
        if (!File.Exists(target.PartPath) || state.ETagFor(target.File.RelativePath) is null) return 0;
        if (state.SegmentsFor(target.File.RelativePath) is { } segments) return segments.Sum(segment => segment.Done);
        var length = new FileInfo(target.PartPath).Length;
        return target.File.ExpectedBytes is { } expected && length > expected ? 0 : length;
    }

    // ─── Integrity and disk space ────────────────────────────────────────────────────────────

    private static async Task<bool> IsValidAsync(string path, ModelDownloadFile file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return false;
        if (file.ExpectedBytes is { } size && info.Length != size) return false;
        if (string.IsNullOrWhiteSpace(file.Sha256)) return true;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDiskSpace(string root, IReadOnlyList<Target> targets, ProgressTracker tracker)
    {
        if (targets.Any(target => target.File.ExpectedBytes is null)) return;
        var remaining = Math.Max(0, targets.Sum(target => target.File.ExpectedBytes!.Value) - tracker.PresentBytes);
        if (remaining == 0) return;
        if (_freeSpace(root) is not { } free) return;
        var required = remaining + DiskSpaceReserveBytes;
        if (free < required)
        {
            throw new ModelDownloadException(
                ModelDownloadFailure.InsufficientDiskSpace,
                $"Not enough disk space: {ModelDownloadFormatting.Bytes(required)} needed, {ModelDownloadFormatting.Bytes(free)} free.");
        }
    }

    private static long? AvailableFreeSpace(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            while (directory is { Exists: false }) directory = directory.Parent;
            var drive = directory?.Root.FullName;
            return drive is null ? null : new DriveInfo(drive).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void WriteCompletionStamp(string root, ModelDownloadManifest manifest, IReadOnlyList<Target> targets)
    {
        var stamp = new CompletionStamp(
            manifest.ModelId,
            manifest.Version,
            targets.Select(target => new CompletionFile(target.File.RelativePath, new FileInfo(target.FinalPath).Length)).ToList());
        WriteAtomically(Path.Combine(root, CompletionFileName), JsonSerializer.Serialize(stamp));
    }

    internal static void WriteAtomically(string path, string contents)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    internal static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
        {
            throw new ModelDownloadException(ModelDownloadFailure.InvalidPath, $"Model file path '{relativePath}' is not a safe relative path.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelDownloadException(ModelDownloadFailure.InvalidPath, $"Model file path '{relativePath}' escapes the model directory.");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Unquote(string etag) => etag.Trim().Trim('"');

    private static string Quote(string etag) => $"\"{etag}\"";

    private sealed record Target(ModelDownloadFile File, string FinalPath)
    {
        public string PartPath => FinalPath + PartSuffix;
    }

    private sealed record Probe(bool AcceptsRanges, string? ETag, long? Length);

    private sealed record CompletionStamp(string ModelId, string Version, List<CompletionFile> Files);

    private sealed record CompletionFile(string Path, long Bytes);

    // ─── Resume state ────────────────────────────────────────────────────────────────────────

    internal sealed class SegmentState
    {
        public long Start { get; set; }
        public long End { get; set; }
        public long Done { get; set; }
        [JsonIgnore]
        public long Remaining => End - Start + 1 - Done;
    }

    /// <summary>
    /// ETags and segment offsets for one model, discarded when the manifest changes so a resume
    /// never mixes bytes from different file sets.
    /// </summary>
    internal sealed class DownloadState
    {
        private readonly object _gate = new();
        private string _path = "";

        public string ModelId { get; set; } = "";
        public string Version { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public Dictionary<string, string> ETags { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<SegmentState>> Segments { get; set; } = new(StringComparer.Ordinal);

        public static DownloadState Load(string root, ModelDownloadManifest manifest)
        {
            var path = Path.Combine(root, StateFileName);
            var fingerprint = FingerprintOf(manifest);
            DownloadState? state = null;
            try
            {
                if (File.Exists(path)) state = JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }

            if (state is null || state.Fingerprint != fingerprint)
            {
                state = new DownloadState { ModelId = manifest.ModelId, Version = manifest.Version, Fingerprint = fingerprint };
            }

            state.ETags = new Dictionary<string, string>(state.ETags ?? [], StringComparer.Ordinal);
            state.Segments = new Dictionary<string, List<SegmentState>>(state.Segments ?? [], StringComparer.Ordinal);
            state._path = path;
            return state;
        }

        public string? ETagFor(string file)
        {
            lock (_gate) return ETags.TryGetValue(file, out var etag) ? etag : null;
        }

        public void SetETag(string file, string etag)
        {
            lock (_gate) ETags[file] = etag;
        }

        public List<SegmentState>? SegmentsFor(string file)
        {
            lock (_gate) return Segments.TryGetValue(file, out var segments) ? segments : null;
        }

        public void SetSegments(string file, List<SegmentState> segments)
        {
            lock (_gate) Segments[file] = segments;
        }

        public void ClearSegments(string file)
        {
            lock (_gate) Segments.Remove(file);
        }

        public void Forget(string file)
        {
            lock (_gate)
            {
                ETags.Remove(file);
                Segments.Remove(file);
            }
        }

        public void Save()
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(this);
            try
            {
                WriteAtomically(_path, json);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        public void Delete() => TryDelete(_path);

        internal static string FingerprintOf(ModelDownloadManifest manifest)
        {
            var builder = new StringBuilder().Append(manifest.ModelId).Append('\n').Append(manifest.Version);
            foreach (var file in manifest.Files)
            {
                builder.Append('\n').Append(file.RelativePath).Append('|').Append(file.Source.AbsoluteUri)
                    .Append('|').Append(file.ExpectedBytes).Append('|').Append(file.Sha256?.ToUpperInvariant());
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }

    // ─── Progress ────────────────────────────────────────────────────────────────────────────

    private sealed class ProgressTracker(ModelDownloadManifest manifest, IProgress<ModelDownloadSnapshot>? sink)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
        private readonly Stopwatch _clock = new();
        private long _startBytes;
        private long _lastEmit = long.MinValue;
        private string? _current;
        private int _retries;
        private ModelDownloadPhase _phase = ModelDownloadPhase.Downloading;
        private string _message = "";

        public long PresentBytes
        {
            get
            {
                lock (_gate) return _bytes.Values.Sum();
            }
        }

        public void MarkPresent(string file, long bytes, bool completed)
        {
            lock (_gate)
            {
                _bytes[file] = bytes;
                if (completed) _completed.Add(file);
            }
        }

        public void SetPhase(ModelDownloadPhase phase, string message, bool force = true)
        {
            lock (_gate)
            {
                if (phase == ModelDownloadPhase.Downloading && !_clock.IsRunning)
                {
                    _startBytes = _bytes.Values.Sum();
                    _clock.Start();
                }

                _phase = phase;
                _message = message;
            }

            Emit(force);
        }

        public void Begin(string file)
        {
            lock (_gate) _current ??= file;
        }

        public void Add(string file, long bytes)
        {
            lock (_gate)
            {
                _bytes[file] = _bytes.GetValueOrDefault(file) + bytes;
                _current ??= file;
            }

            Emit(force: false);
        }

        public void Retry(string file, int retry)
        {
            lock (_gate)
            {
                _retries++;
                _message = $"Retrying {file} ({retry} of {MaxAttempts - 1})";
            }

            Emit(force: true);
        }

        public void Complete(string file)
        {
            lock (_gate)
            {
                _completed.Add(file);
                // Keep the featured file stable while others run; move on only when it finishes.
                if (string.Equals(_current, file, StringComparison.Ordinal))
                {
                    _current = manifest.Files.Select(item => item.RelativePath).FirstOrDefault(path => !_completed.Contains(path));
                }
            }

            Emit(force: true);
        }

        private void Emit(bool force)
        {
            if (sink is null) return;
            ModelDownloadSnapshot snapshot;
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (!force && _lastEmit != long.MinValue && Stopwatch.GetElapsedTime(_lastEmit, now) < ProgressInterval) return;
                _lastEmit = now;

                var completed = _bytes.Values.Sum();
                long? total = manifest.Files.All(file => file.ExpectedBytes is not null)
                    ? manifest.Files.Sum(file => file.ExpectedBytes!.Value)
                    : null;
                var elapsed = _clock.Elapsed.TotalSeconds;
                var speed = elapsed > 0.5 ? Math.Max(0, completed - _startBytes) / elapsed : 0;
                TimeSpan? remaining = speed > 0 && total is { } all && all > completed
                    ? TimeSpan.FromSeconds((all - completed) / speed)
                    : null;
                snapshot = new ModelDownloadSnapshot(
                    manifest.ModelId, _phase, _current, completed, total, _completed.Count, manifest.Files.Count,
                    speed, remaining, _retries, _message);
            }

            sink.Report(snapshot);
        }
    }
}
