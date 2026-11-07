using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Downloads a model's files one by one from an immutable, content-addressed source and verifies
/// each against its pinned SHA-256 while it streams.
/// </summary>
/// <remarks>
/// Release archives on GitHub are mutable: the publisher can replace an asset under the same URL.
/// On 2026-11-07 sherpa-onnx re-exported the Parakeet v2 and v3 archives, so every fresh install
/// failed its archive checksum. A Hugging Face URL pinned to a full commit hash cannot change, so
/// the pinned per-file hashes stay satisfiable for as long as the revision exists.
/// </remarks>
internal static partial class PinnedModelFileDownloader
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromHours(2) });

    [GeneratedRegex(@"^https://huggingface\.co/[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+/resolve/[0-9a-f]{40}/?$")]
    private static partial Regex ImmutableHuggingFaceRevision();

    /// <summary>True only for a Hugging Face <c>resolve/&lt;40-hex commit&gt;</c> base URL.</summary>
    public static bool IsImmutableSource(string? baseUrl) =>
        baseUrl is not null && ImmutableHuggingFaceRevision().IsMatch(baseUrl);

    public static Task DownloadAsync(
        string baseUrl,
        IReadOnlyDictionary<string, string> files,
        string destinationDirectory,
        string displayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        DownloadAsync(SharedClient.Value, baseUrl, files, destinationDirectory, displayName, progress, cancellationToken);

    internal static async Task DownloadAsync(
        HttpClient httpClient,
        string baseUrl,
        IReadOnlyDictionary<string, string> files,
        string destinationDirectory,
        string displayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsImmutableSource(baseUrl))
        {
            throw new InvalidOperationException(
                $"{displayName} must download from an immutable Hugging Face revision (resolve/<commit>), not '{baseUrl}'.");
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"{displayName} has no pinned files to download.");
        }

        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var stage = $"Downloading {displayName}";
        var totalBytes = await TryGetTotalBytesAsync(httpClient, baseUrl, files.Keys, cancellationToken);
        var reporter = new ThrottledProgress(progress, stage, totalBytes);

        foreach (var (relativePath, expectedSha256) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveInside(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var partial = $"{destination}.{Guid.NewGuid():N}.partial";
            try
            {
                using var response = await httpClient.GetAsync(
                    FileUri(baseUrl, relativePath), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(
                                 partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        reporter.Add(read);
                    }

                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"{displayName} file {relativePath} failed verification. Expected {expectedSha256}; found {actual}.");
                }

                File.Move(partial, destination, overwrite: true);
            }
            finally
            {
                TryDelete(partial);
            }
        }

        reporter.Complete();
    }

    private static async Task<long?> TryGetTotalBytesAsync(
        HttpClient httpClient,
        string baseUrl,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var relativePath in relativePaths)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, FileUri(baseUrl, relativePath));
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is not { } length)
                {
                    return null;
                }

                total += length;
            }
            catch (HttpRequestException)
            {
                // Sizes only drive the progress bar; the GET reports any real failure.
                return null;
            }
        }

        return total;
    }

    private static Uri FileUri(string baseUrl, string relativePath) =>
        new($"{baseUrl.TrimEnd('/')}/{string.Join('/', relativePath.Split('/', '\\').Select(Uri.EscapeDataString))}");

    private static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Pinned model path '{relativePath}' must be relative.");
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Pinned model path '{relativePath}' escapes the model directory.");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reports at most four times a second. The previous downloaders reported every 1 MB chunk,
    /// several hundred UI updates per model, which fed the onboarding refresh storm.
    /// </summary>
    private sealed class ThrottledProgress(IProgress<ModelDownloadProgress>? sink, string stage, long? totalBytes)
    {
        private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
        private long _received;
        private bool _reportedOnce;

        public void Add(int bytes)
        {
            _received += bytes;
            if (sink is null || (_reportedOnce && _sinceLastReport.Elapsed < ProgressInterval)) return;
            _reportedOnce = true;
            _sinceLastReport.Restart();
            sink.Report(new ModelDownloadProgress(stage, _received, totalBytes));
        }

        public void Complete() => sink?.Report(new ModelDownloadProgress(stage, _received, totalBytes ?? _received));
    }
}
