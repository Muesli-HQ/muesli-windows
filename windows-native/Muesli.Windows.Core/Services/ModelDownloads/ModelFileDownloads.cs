using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Muesli.Windows.Services;

/// <summary>
/// A downloaded file that stays in its resume folder until the caller's install succeeds. If a
/// later step (another download, extraction, verification) is paused or fails, the file is kept
/// and the next attempt reuses it instead of downloading it again.
/// </summary>
public sealed class ModelFileLease
{
    internal ModelFileLease(string path, string resumeRoot)
    {
        Path = path;
        ResumeRoot = resumeRoot;
    }

    /// <summary>The verified file. Read or copy it; do not move it.</summary>
    public string Path { get; }

    internal string ResumeRoot { get; }

    /// <summary>Removes the downloaded file and its bookkeeping once it has been installed.</summary>
    public void Release() => ModelFileDownloads.DeleteResumeFolder(ResumeRoot);
}

/// <summary>
/// Single-file downloads (cleanup GGUFs, streaming archives, VAD and diarization models) through
/// the shared <see cref="ModelDownloadCoordinator"/>.
/// </summary>
public static class ModelFileDownloads
{
    // One download per resume folder at a time. Two callers fetching the same URL into the same
    // folder (the live-model installer and meeting speech detection both fetch the Silero VAD)
    // take turns instead of colliding on one partial file or deleting it under each other.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

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
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        var plan = Plan(url, directory, sha256);
        var gate = Gates.GetOrAdd(plan.ResumeRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await DownloadIntoAsync(plan, label, progress, cancellationToken, coordinator);
            Directory.CreateDirectory(directory);
            File.Move(plan.FilePath, destination, overwrite: true);
            DeleteResumeFolder(plan.ResumeRoot);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into its resume folder under <paramref name="directory"/>
    /// and leaves it there. Call <see cref="ModelFileLease.Release"/> after the install succeeds; on
    /// pause or failure, leave it, and the next fetch returns at once from the verified file.
    /// </summary>
    public static async Task<ModelFileLease> FetchAsync(
        string url,
        string directory,
        string? sha256,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken,
        ModelDownloadCoordinator? coordinator = null)
    {
        var plan = Plan(url, Path.GetFullPath(directory), sha256);
        var gate = Gates.GetOrAdd(plan.ResumeRoot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await DownloadIntoAsync(plan, label, progress, cancellationToken, coordinator);
            return new ModelFileLease(plan.FilePath, plan.ResumeRoot);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The resume folder a download of <paramref name="url"/> into <paramref name="directory"/> uses.</summary>
    public static string ResumeFolderFor(string url, string directory) =>
        Plan(url, Path.GetFullPath(directory), null).ResumeRoot;

    internal static void DeleteResumeFolder(string resumeRoot)
    {
        try
        {
            if (Directory.Exists(resumeRoot)) Directory.Delete(resumeRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only bookkeeping remains; the next download of this URL reuses or replaces it.
        }
    }

    private sealed record DownloadPlan(string ResumeRoot, string FilePath, ModelDownloadManifest Manifest);

    private static DownloadPlan Plan(string url, string directory, string? sha256)
    {
        var source = new Uri(url, UriKind.Absolute);
        var fileName = Path.GetFileName(source.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "download.bin";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri)))[..16].ToLowerInvariant();
        var resumeRoot = Path.Combine(directory, $".dl-{key}.resume");
        var manifest = new ModelDownloadManifest(fileName, sha256 ?? source.AbsoluteUri, [new ModelDownloadFile(fileName, source, null, sha256)]);
        return new DownloadPlan(resumeRoot, Path.Combine(resumeRoot, fileName), manifest);
    }

    private static Task DownloadIntoAsync(
        DownloadPlan plan,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken,
        ModelDownloadCoordinator? coordinator) =>
        (coordinator ?? ModelDownloadCoordinator.Shared).DownloadAsync(
            plan.Manifest, plan.ResumeRoot, ModelDownloadProgressAdapter.For(label, progress, downloadingStage: label), cancellationToken);
}
