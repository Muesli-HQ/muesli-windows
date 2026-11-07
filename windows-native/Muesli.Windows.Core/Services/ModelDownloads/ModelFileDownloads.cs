using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Muesli.Windows.Services;

/// <summary>
/// Single-file downloads (cleanup GGUFs, streaming archives, VAD and diarization models) through
/// the shared <see cref="ModelDownloadCoordinator"/>.
/// </summary>
public static class ModelFileDownloads
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/> with resume, retries,
    /// parallel ranges and verification. Partial bytes live in a resume folder beside the
    /// destination, keyed by the URL, so a later attempt resumes even when the caller passes a
    /// different temporary destination name.
    /// </summary>
    public static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string? sha256,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken,
        ModelDownloadCoordinator? coordinator = null)
    {
        var source = new Uri(url, UriKind.Absolute);
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        var fileName = Path.GetFileName(source.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri)))[..16].ToLowerInvariant();
        var resumeRoot = Path.Combine(directory, $".dl-{key}.resume");

        var manifest = new ModelDownloadManifest(fileName, sha256 ?? source.AbsoluteUri, [new ModelDownloadFile(fileName, source, null, sha256)]);
        await (coordinator ?? ModelDownloadCoordinator.Shared).DownloadAsync(
            manifest, resumeRoot, ModelDownloadProgressAdapter.For(label, progress, downloadingStage: label), cancellationToken);

        Directory.CreateDirectory(directory);
        File.Move(Path.Combine(resumeRoot, fileName), destination, overwrite: true);
        try
        {
            Directory.Delete(resumeRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only bookkeeping files remain; the next download of this URL reuses or replaces them.
        }
    }
}
