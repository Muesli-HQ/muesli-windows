using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class ModelDownloadCoordinatorTests
{
    private const string Host = "https://models.test";

    [Fact]
    public async Task Downloads_verifies_and_stamps_every_file()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(3_000_000, 1)).Add("/tokens.txt", Data(5_000, 2));
        var manifest = server.Manifest("model", "v1");
        var snapshots = new ConcurrentQueue<ModelDownloadSnapshot>();

        await Coordinator(server).DownloadAsync(manifest, directory.Path, new SyncProgress(snapshots.Enqueue), CancellationToken.None);

        Assert.Equal(server.Bytes("/a.bin"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "a.bin")));
        Assert.Equal(server.Bytes("/tokens.txt"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "tokens.txt")));
        Assert.True(ModelDownloadCoordinator.IsComplete(manifest, directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
        Assert.False(File.Exists(Path.Combine(directory.Path, ModelDownloadCoordinator.StateFileName)));
        var last = snapshots.Last();
        Assert.Equal(ModelDownloadPhase.Ready, last.Phase);
        Assert.Equal(2, last.CompletedFiles);
        Assert.Equal(last.TotalBytes, last.CompletedBytes);
    }

    [Fact]
    public async Task A_paused_download_resumes_with_a_range_request_instead_of_starting_over()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(4_000_000, 3));
        var manifest = server.Manifest("model", "v1");
        using var pause = new CancellationTokenSource();
        server.StallAfter = request => request.Method == HttpMethod.Get ? 1_000_000 : null;
        server.OnStall = pause.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Coordinator(server).DownloadAsync(manifest, directory.Path, null, pause.Token));

        var partial = new FileInfo(Path.Combine(directory.Path, "a.bin.part")).Length;
        Assert.Equal(1_000_000, partial);
        server.StallAfter = null;
        server.ClearLog();

        await Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None);

        var resumed = Assert.Single(server.Log, entry => entry.Method == HttpMethod.Get);
        Assert.Equal(partial, resumed.RangeFrom);
        Assert.Equal(server.ETag("/a.bin"), resumed.IfRange);
        Assert.Equal(server.Bytes("/a.bin"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "a.bin")));
    }

    [Fact]
    public async Task Transient_server_errors_are_retried_and_counted()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 4));
        var failures = 0;
        server.Intercept = request => request.Method == HttpMethod.Get && Interlocked.Increment(ref failures) <= 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : null;
        var snapshots = new ConcurrentQueue<ModelDownloadSnapshot>();

        await Coordinator(server).DownloadAsync(server.Manifest("model", "v1"), directory.Path, new SyncProgress(snapshots.Enqueue), CancellationToken.None);

        Assert.Equal(3, server.Log.Count(entry => entry.Method == HttpMethod.Get));
        Assert.Contains(snapshots, snapshot => snapshot.RetryCount == 2);
        Assert.Equal(server.Bytes("/a.bin"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "a.bin")));
    }

    [Fact]
    public async Task Exhausted_retries_report_three_attempts()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 5));
        server.Intercept = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            Coordinator(server).DownloadAsync(server.Manifest("model", "v1"), directory.Path, null, CancellationToken.None));

        Assert.Contains("after three attempts", error.Message);
        Assert.Equal(3, server.Log.Count);
    }

    [Fact]
    public async Task Client_errors_fail_fast_without_retrying()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 6));
        server.Intercept = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            Coordinator(server).DownloadAsync(server.Manifest("model", "v1"), directory.Path, null, CancellationToken.None));

        Assert.Equal(ModelDownloadFailure.HttpStatus, error.Failure);
        Assert.Equal(404, error.StatusCode);
        Assert.Single(server.Log);
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_and_leaves_no_file()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 7));
        var manifest = new ModelDownloadManifest("model", "v1",
            [new ModelDownloadFile("a.bin", new Uri(Host + "/a.bin"), 20_000, new string('A', 64))]);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None));

        Assert.Equal(ModelDownloadFailure.ChecksumMismatch, error.Failure);
        Assert.False(File.Exists(Path.Combine(directory.Path, "a.bin")));
        Assert.False(ModelDownloadCoordinator.IsComplete(manifest, directory.Path));
    }

    [Fact]
    public async Task A_file_changed_on_the_server_restarts_instead_of_stitching_old_bytes()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(3_000_000, 8));
        var manifestWithoutHash = new ModelDownloadManifest("model", "v1",
            [new ModelDownloadFile("a.bin", new Uri(Host + "/a.bin"), 3_000_000)]);
        using var pause = new CancellationTokenSource();
        server.StallAfter = request => request.Method == HttpMethod.Get ? 1_000_000 : null;
        server.OnStall = pause.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator(server).DownloadAsync(
            manifestWithoutHash, directory.Path, null, pause.Token));
        Assert.True(File.Exists(Path.Combine(directory.Path, "a.bin.part")));

        server.StallAfter = null;
        server.Add("/a.bin", Data(3_000_000, 9)); // same size, new content and ETag
        await Coordinator(server).DownloadAsync(manifestWithoutHash, directory.Path, null, CancellationToken.None);

        Assert.Equal(server.Bytes("/a.bin"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "a.bin")));
    }

    [Fact]
    public async Task Large_files_download_as_parallel_ranges_and_resume_per_segment()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/big.onnx", Data(8_000_000, 10));
        var manifest = server.Manifest("model", "v1");
        using var pause = new CancellationTokenSource();
        var coordinator = Coordinator(server, segmentThreshold: 1_000_000);
        var stalled = 0;
        server.StallAfter = request => request.Method == HttpMethod.Get ? 1_000_000 : null;
        server.OnStall = () =>
        {
            if (Interlocked.Increment(ref stalled) == 4) pause.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.DownloadAsync(manifest, directory.Path, null, pause.Token));
        Assert.Equal(4, server.Log.Count(entry => entry.Method == HttpMethod.Get && entry.RangeFrom is not null));
        server.StallAfter = null;
        server.ClearLog();

        await coordinator.DownloadAsync(manifest, directory.Path, null, CancellationToken.None);

        var resumed = server.Log.Where(entry => entry.Method == HttpMethod.Get).ToList();
        Assert.Equal(4, resumed.Count);
        // Each 2 MB segment had 1 MB saved, so resume fetches exactly the missing half of each.
        Assert.Equal(4_000_000, resumed.Sum(entry => entry.RangeLength));
        Assert.Equal(server.Bytes("/big.onnx"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "big.onnx")));
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_falls_back_to_one_stream()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer { HonourRanges = false }.Add("/big.onnx", Data(3_000_000, 11));

        await Coordinator(server, segmentThreshold: 1_000_000)
            .DownloadAsync(server.Manifest("model", "v1"), directory.Path, null, CancellationToken.None);

        Assert.Equal(server.Bytes("/big.onnx"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "big.onnx")));
    }

    [Fact]
    public async Task A_stalled_transfer_is_retried()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(2_000_000, 12));
        var gets = 0;
        server.StallAfter = request => request.Method == HttpMethod.Get && Interlocked.Increment(ref gets) == 1 ? 1_000_000 : null;

        await Coordinator(server, stall: TimeSpan.FromMilliseconds(300))
            .DownloadAsync(server.Manifest("model", "v1"), directory.Path, null, CancellationToken.None);

        Assert.Equal(2, gets);
        Assert.Equal(server.Bytes("/a.bin"), await File.ReadAllBytesAsync(Path.Combine(directory.Path, "a.bin")));
    }

    [Fact]
    public async Task Missing_disk_space_fails_before_any_request()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 13));
        var coordinator = new ModelDownloadCoordinator(new HttpClient(server), (_, _) => Task.CompletedTask, _ => 1_000);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            coordinator.DownloadAsync(server.Manifest("model", "v1"), directory.Path, null, CancellationToken.None));

        Assert.Equal(ModelDownloadFailure.InsufficientDiskSpace, error.Failure);
        Assert.Empty(server.Log);
    }

    [Fact]
    public async Task Already_downloaded_verified_files_are_not_fetched_again()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer().Add("/a.bin", Data(20_000, 14)).Add("/b.bin", Data(20_000, 15));
        var manifest = server.Manifest("model", "v1");
        await Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None);
        File.Delete(Path.Combine(directory.Path, "b.bin"));
        server.ClearLog();

        await Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None);

        var get = Assert.Single(server.Log);
        Assert.Equal("/b.bin", get.Path);
    }

    [Theory]
    [InlineData("../escape.bin")]
    [InlineData("nested/../../escape.bin")]
    [InlineData("C:\\absolute.bin")]
    public async Task Unsafe_paths_are_rejected(string relativePath)
    {
        using var directory = new TestDirectory();
        var manifest = new ModelDownloadManifest("model", "v1", [new ModelDownloadFile(relativePath, new Uri(Host + "/x"), 1)]);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            Coordinator(new FakeServer()).DownloadAsync(manifest, Path.Combine(directory.Path, "model"), null, CancellationToken.None));

        Assert.Equal(ModelDownloadFailure.InvalidPath, error.Failure);
    }

    [Fact]
    public void Segments_cover_the_whole_file_exactly_once()
    {
        var segments = ModelDownloadCoordinator.PlanSegments(10_000_003, 4);
        Assert.Equal(0, segments[0].Start);
        Assert.Equal(10_000_002, segments[^1].End);
        for (var index = 1; index < segments.Count; index++) Assert.Equal(segments[index - 1].End + 1, segments[index].Start);
        Assert.Equal(10_000_003, segments.Sum(segment => segment.Remaining));
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(1_500, "1.5 KB")]
    [InlineData(640_000_000, "640.0 MB")]
    [InlineData(1_200_000_000, "1.2 GB")]
    public void Bytes_use_decimal_units_with_one_decimal(long bytes, string expected) =>
        Assert.Equal(expected, ModelDownloadFormatting.Bytes(bytes));

    [Theory]
    [InlineData(26, "26s")]
    [InlineData(245, "4m 05s")]
    [InlineData(4_020, "1h 07m")]
    public void Remaining_time_matches_macos(double seconds, string expected) =>
        Assert.Equal(expected, ModelDownloadFormatting.Remaining(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Detail_line_matches_the_macos_card()
    {
        var snapshot = new ModelDownloadSnapshot("parakeet-v3", ModelDownloadPhase.Downloading, "encoder.int8.onnx",
            312_000_000, 640_000_000, 3, 12, 12_400_000, TimeSpan.FromSeconds(26), 1, "");

        Assert.Equal(
            "3 of 12 files · 312.0 MB / 640.0 MB · 328.0 MB left · 12.4 MB/s · 26s left · retry 1/2",
            ModelDownloadFormatting.DetailLine(snapshot));
        Assert.Equal("Downloading Parakeet v3 · 49% · ~26s left", ModelDownloadFormatting.Compact("Parakeet v3", snapshot));
    }

    [Fact]
    public async Task Two_callers_of_the_same_url_both_succeed()
    {
        using var directory = new TestDirectory();
        var data = Data(2_000_000, 21);
        var server = new FakeServer().Add("/vad.onnx", data);
        var coordinator = Coordinator(server);
        var hash = Convert.ToHexString(SHA256.HashData(data));

        await Task.WhenAll(
            ModelFileDownloads.DownloadFileAsync(Host + "/vad.onnx", Path.Combine(directory.Path, "one.download"), hash, "VAD", null, CancellationToken.None, coordinator),
            ModelFileDownloads.DownloadFileAsync(Host + "/vad.onnx", Path.Combine(directory.Path, "two.download"), hash, "VAD", null, CancellationToken.None, coordinator));

        Assert.Equal(data, File.ReadAllBytes(Path.Combine(directory.Path, "one.download")));
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(directory.Path, "two.download")));
    }

    [Fact]
    public async Task A_fetched_file_is_reused_until_released()
    {
        using var directory = new TestDirectory();
        var data = Data(1_500_000, 22);
        var server = new FakeServer().Add("/archive.tar.bz2", data);
        var coordinator = Coordinator(server);
        var hash = Convert.ToHexString(SHA256.HashData(data));

        var first = await ModelFileDownloads.FetchAsync(Host + "/archive.tar.bz2", directory.Path, hash, "Archive", null, CancellationToken.None, coordinator);
        // A later install step failed; the next attempt must not download the archive again.
        server.ClearLog();
        var again = await ModelFileDownloads.FetchAsync(Host + "/archive.tar.bz2", directory.Path, hash, "Archive", null, CancellationToken.None, coordinator);

        Assert.Equal(first.Path, again.Path);
        Assert.Equal(data, File.ReadAllBytes(again.Path));
        Assert.DoesNotContain(server.Log, entry => entry.Method == HttpMethod.Get);

        again.Release();
        Assert.False(Directory.Exists(Path.GetDirectoryName(again.Path)));
    }

    [Fact]
    public async Task Weak_etags_are_not_used_to_resume()
    {
        using var directory = new TestDirectory();
        var server = new FakeServer { WeakETags = true }.Add("/a.bin", Data(3_000_000, 23));
        var manifest = server.Manifest("model", "v1");
        using var pause = new CancellationTokenSource();
        server.StallAfter = _ => 1_000_000;
        server.OnStall = pause.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Coordinator(server).DownloadAsync(manifest, directory.Path, null, pause.Token));
        server.StallAfter = null;
        server.ClearLog();
        await Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None);

        var get = Assert.Single(server.Log, entry => entry.Method == HttpMethod.Get);
        Assert.Null(get.RangeFrom);
        Assert.Null(get.IfRange);
    }

    [Fact]
    public async Task A_failed_fresh_response_does_not_leave_old_bytes_to_stitch()
    {
        using var directory = new TestDirectory();
        var oldData = Data(3_000_000, 24);
        var newData = Data(3_000_000, 25);
        var server = new FakeServer().Add("/a.bin", oldData);
        var manifest = new ModelDownloadManifest("model", "v1",
            [new ModelDownloadFile("a.bin", new Uri(Host + "/a.bin"), newData.Length, Convert.ToHexString(SHA256.HashData(newData)))]);
        using var pause = new CancellationTokenSource();
        server.StallAfter = _ => 1_000_000;
        server.OnStall = pause.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Coordinator(server).DownloadAsync(manifest, directory.Path, null, pause.Token));

        // The server file changes, and the first fresh response is malformed.
        server.StallAfter = null;
        server.Add("/a.bin", newData);
        server.Intercept = request =>
        {
            if (request.Method != HttpMethod.Get) return null;
            server.Intercept = null;
            var bad = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(newData) };
            bad.Headers.ETag = new EntityTagHeaderValue($"\"{server.ETag("/a.bin")}\"");
            bad.Content.Headers.ContentLength = 123;
            return bad;
        };
        var failure = await Assert.ThrowsAsync<ModelDownloadException>(() =>
            Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None));
        Assert.Equal(ModelDownloadFailure.InvalidContentLength, failure.Failure);

        // The next attempt must start over, not append new bytes to the old part.
        await Coordinator(server).DownloadAsync(manifest, directory.Path, null, CancellationToken.None);
        Assert.Equal(newData, File.ReadAllBytes(Path.Combine(directory.Path, "a.bin")));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static ModelDownloadCoordinator Coordinator(FakeServer server, long segmentThreshold = long.MaxValue, TimeSpan? stall = null) =>
        new(new HttpClient(server), (_, _) => Task.CompletedTask, _ => long.MaxValue, segmentThreshold, 4, stall);

    private static byte[] Data(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class SyncProgress(Action<ModelDownloadSnapshot> report) : IProgress<ModelDownloadSnapshot>
    {
        public void Report(ModelDownloadSnapshot value) => report(value);
    }

    private sealed record LogEntry(HttpMethod Method, string Path, long? RangeFrom, long? RangeLength, string? IfRange);

    /// <summary>An HTTP file server with ETags, Range/If-Range and fault injection.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, (byte[] Data, string ETag)> _files = new();

        private ConcurrentQueue<LogEntry> _log = new();

        public List<LogEntry> Log => [.. _log];
        public void ClearLog() => _log = new ConcurrentQueue<LogEntry>();
        public bool HonourRanges { get; init; } = true;
        public bool WeakETags { get; init; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }
        public Func<HttpRequestMessage, long?>? StallAfter { get; set; }

        /// <summary>Raised when a response stream reaches its stall point: a deterministic moment to pause.</summary>
        public Action? OnStall { get; set; }

        public FakeServer Add(string path, byte[] data)
        {
            _files[path] = (data, Convert.ToHexString(SHA256.HashData(data))[..16]);
            return this;
        }

        public byte[] Bytes(string path) => _files[path].Data;
        public string ETag(string path) => _files[path].ETag;

        public ModelDownloadManifest Manifest(string id, string version) => new(id, version,
            _files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ModelDownloadFile(
                pair.Key.TrimStart('/'), new Uri(Host + pair.Key), pair.Value.Data.Length,
                Convert.ToHexString(SHA256.HashData(pair.Value.Data)))).ToList());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var ifRange = request.Headers.IfRange?.EntityTag?.Tag.Trim('"');
            long? rangeLength = null;
            if (range?.From is { } rf && _files.TryGetValue(path, out var sized))
            {
                rangeLength = (range.To ?? sized.Data.Length - 1) - rf + 1;
            }

            _log.Enqueue(new LogEntry(request.Method, path, range?.From, rangeLength, ifRange));
            if (Intercept?.Invoke(request) is { } intercepted) return Task.FromResult(intercepted);
            if (!_files.TryGetValue(path, out var file)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue($"\"{file.ETag}\"", WeakETags);
            if (HonourRanges) response.Headers.AcceptRanges.Add("bytes");
            if (request.Method == HttpMethod.Head)
            {
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = file.Data.Length;
                return Task.FromResult(response);
            }

            long from = 0;
            var to = file.Data.Length - 1L;
            var partial = HonourRanges && range?.From is not null && (ifRange is null || ifRange == file.ETag);
            if (partial)
            {
                from = range!.From!.Value;
                to = range.To ?? to;
                response.StatusCode = HttpStatusCode.PartialContent;
            }

            var slice = file.Data.AsMemory((int)from, (int)(to - from + 1)).ToArray();
            response.Content = new StreamContent(new FaultStream(slice, StallAfter?.Invoke(request), OnStall));
            response.Content.Headers.ContentLength = slice.Length;
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Data.Length);
            return Task.FromResult(response);
        }
    }

    /// <summary>Serves bytes, optionally hanging forever after a given offset.</summary>
    private sealed class FaultStream(byte[] data, long? stallAfter, Action? onStall) : Stream
    {
        private long _position;
        private bool _stalled;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAfter is { } stall && _position >= stall)
            {
                if (!_stalled)
                {
                    _stalled = true;
                    onStall?.Invoke();
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var limit = stallAfter is { } s && _position < s ? s : data.Length;
            var count = (int)Math.Min(buffer.Length, Math.Min(256 * 1024, limit - _position));
            if (count <= 0) return 0;
            data.AsMemory((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
