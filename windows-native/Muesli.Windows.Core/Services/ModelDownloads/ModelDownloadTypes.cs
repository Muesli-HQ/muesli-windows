namespace Muesli.Windows.Services;

/// <summary>One file of a model download. Size and SHA-256 are verified when known.</summary>
public sealed record ModelDownloadFile(string RelativePath, Uri Source, long? ExpectedBytes = null, string? Sha256 = null);

/// <summary>
/// Everything a model needs on disk. <see cref="Version"/> identifies this exact set of files; a
/// changed version or file list invalidates partial downloads and resume state.
/// </summary>
public sealed record ModelDownloadManifest(
    string ModelId,
    string Version,
    IReadOnlyList<ModelDownloadFile> Files,
    int MaximumConcurrency = 2);

public enum ModelDownloadPhase
{
    Downloading,
    Verifying,
    Ready,
    Paused,
    Failed
}

/// <summary>A point-in-time view of a model download, emitted at most every 100 ms.</summary>
public sealed record ModelDownloadSnapshot(
    string ModelId,
    ModelDownloadPhase Phase,
    string? CurrentFile,
    long CompletedBytes,
    long? TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    double BytesPerSecond,
    TimeSpan? TimeRemaining,
    int RetryCount,
    string Message)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes.Value, 0, 1)
        : null;

    /// <summary>Bridges to the progress type the lifecycle services and pages consume, keeping the full snapshot.</summary>
    public ModelDownloadProgress ToProgress(string stage) => new(stage, CompletedBytes, TotalBytes) { Snapshot = this };
}

/// <summary>Feeds engine snapshots to the lifecycle progress channel with stage text it understands.</summary>
public static class ModelDownloadProgressAdapter
{
    public static IProgress<ModelDownloadSnapshot>? For(
        string displayName,
        IProgress<ModelDownloadProgress>? progress,
        string? downloadingStage = null) =>
        progress is null
            ? null
            : new Relay(snapshot => progress.Report(snapshot.ToProgress(snapshot.Phase switch
            {
                ModelDownloadPhase.Verifying => $"Verifying {displayName}",
                ModelDownloadPhase.Ready => $"{displayName} downloaded",
                ModelDownloadPhase.Paused => "Download paused",
                ModelDownloadPhase.Failed => snapshot.Message,
                _ => downloadingStage ?? $"Downloading {displayName}"
            })));

    private sealed class Relay(Action<ModelDownloadSnapshot> report) : IProgress<ModelDownloadSnapshot>
    {
        public void Report(ModelDownloadSnapshot value) => report(value);
    }
}

public enum ModelDownloadFailure
{
    Network,
    Stalled,
    HttpStatus,
    InvalidContentLength,
    SizeMismatch,
    ChecksumMismatch,
    InsufficientDiskSpace,
    InvalidPath,
    InsecureSource
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(ModelDownloadFailure failure, string message, Exception? inner = null, int? statusCode = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public ModelDownloadFailure Failure { get; }

    public int? StatusCode { get; }

    /// <summary>
    /// Transient conditions are retried. Integrity, disk-space and client errors fail fast: trying
    /// again cannot fix them. 408 and 429 are treated as transient.
    /// </summary>
    public bool IsRetryable => Failure switch
    {
        ModelDownloadFailure.Network or ModelDownloadFailure.Stalled => true,
        ModelDownloadFailure.HttpStatus => StatusCode is >= 500 or 408 or 429,
        _ => false
    };
}
