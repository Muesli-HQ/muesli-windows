using System.Net.Http;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Downloads a model's files from an immutable, content-addressed source through the shared
/// <see cref="ModelDownloadCoordinator"/> (resume, retries, parallel ranges, verification).
/// </summary>
/// <remarks>
/// Release archives on GitHub are mutable: the publisher can replace an asset under the same URL.
/// On 2026-11-07 sherpa-onnx re-exported the Parakeet v2 and v3 archives, so every fresh install
/// failed its archive checksum. A Hugging Face URL pinned to a full commit hash cannot change, so
/// the pinned per-file hashes stay satisfiable for as long as the revision exists.
/// </remarks>
internal static partial class PinnedModelFileDownloader
{
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
        DownloadAsync(ModelDownloadCoordinator.Shared, baseUrl, files, destinationDirectory, displayName, progress, cancellationToken);

    internal static Task DownloadAsync(
        HttpClient httpClient,
        string baseUrl,
        IReadOnlyDictionary<string, string> files,
        string destinationDirectory,
        string displayName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        DownloadAsync(new ModelDownloadCoordinator(httpClient), baseUrl, files, destinationDirectory, displayName, progress, cancellationToken);

    private static Task DownloadAsync(
        ModelDownloadCoordinator coordinator,
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

        var manifest = new ModelDownloadManifest(
            displayName,
            baseUrl.TrimEnd('/'),
            files.Select(pair => new ModelDownloadFile(pair.Key, FileUri(baseUrl, pair.Key), null, pair.Value)).ToList());
        return coordinator.DownloadAsync(manifest, destinationDirectory, ModelDownloadProgressAdapter.For(displayName, progress), cancellationToken);
    }

    private static Uri FileUri(string baseUrl, string relativePath) =>
        new($"{baseUrl.TrimEnd('/')}/{string.Join('/', relativePath.Split('/', '\\').Select(Uri.EscapeDataString))}");
}
