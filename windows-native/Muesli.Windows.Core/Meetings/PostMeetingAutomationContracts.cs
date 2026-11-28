namespace Muesli.Windows.Services;

public enum PostMeetingCompletionEvent
{
    RecordingCompleted,
    RecoveryCompleted,
    ManualTest
}

public enum PostMeetingTranscriptPolicy
{
    MetadataOnly,
    Inline,
    AutoExportPath
}

public enum PostMeetingAutomationStatus
{
    Disabled,
    Succeeded,
    InvalidConfiguration,
    Failed,
    TimedOut,
    Cancelled
}

public enum AutomationDestinationOwnership
{
    None,
    UserSelectedDestination
}

public enum MeetingExportMode
{
    Notes,
    Transcript,
    FullMeeting
}

/// <summary>
/// The auto-export file format, with the macOS ids (<c>MeetingAutoExportFileFormat</c>).
/// </summary>
public static class AutoExportFileFormats
{
    public const string Markdown = "markdown";
    public const string Pdf = "pdf";
    public const string MarkdownAndPdf = "markdown_and_pdf";

    public static string Resolve(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AutoExportFileFormat?.Trim().ToLowerInvariant() switch
        {
            Markdown => Markdown,
            Pdf => Pdf,
            MarkdownAndPdf => MarkdownAndPdf,
            // Files from before the picker: the old "Also export PDF" switch added a PDF beside
            // the Markdown copy.
            _ => settings.AutoExportPdfEnabled ? MarkdownAndPdf : Markdown
        };
    }

    public static bool IncludesMarkdown(string format) => format is Markdown or MarkdownAndPdf;
    public static bool IncludesPdf(string format) => format is Pdf or MarkdownAndPdf;
}

/// <summary>macOS <c>MeetingRecordingSavePolicy</c>: whether a meeting's audio is kept after Stop.</summary>
public static class MeetingRecordingSavePolicies
{
    public const string Never = "never";
    public const string Prompt = "prompt";
    public const string Always = "always";

    public static string Resolve(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MeetingRecordingSavePolicy?.Trim().ToLowerInvariant() switch
        {
            Never => Never,
            Prompt => Prompt,
            Always => Always,
            _ => settings.SaveMeetingRecordings ? Always : Never
        };
    }

    /// <summary>
    /// Whether capture keeps its audio files at Stop. "Ask" keeps them until the user answers, so
    /// a "Keep Audio" answer has something to keep.
    /// </summary>
    public static bool RetainsAtStop(string policy) => policy != Never;
}

public sealed record PostMeetingRetryPolicy
{
    public int MaxAttempts { get; init; } = 1;
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
}

public sealed record PostMeetingAutomationOptions
{
    public static PostMeetingAutomationOptions FromSettings(MuesliSettings settings) => new()
    {
        HookEnabled = settings.PostMeetingHookEnabled,
        HookExecutablePath = settings.PostMeetingHookExecutablePath,
        // "Auto-export meetings" is the master switch; the file format says which files it writes.
        AutoExportEnabled = settings.AutoExportMarkdownEnabled &&
                            AutoExportFileFormats.IncludesMarkdown(AutoExportFileFormats.Resolve(settings)),
        AutoExportPdfEnabled = settings.AutoExportMarkdownEnabled &&
                               AutoExportFileFormats.IncludesPdf(AutoExportFileFormats.Resolve(settings)),
        AutoExportDirectory = settings.AutoExportMarkdownDirectory,
        AutoExportMode = settings.AutoExportMarkdownContent switch
        {
            "transcript" => MeetingExportMode.Transcript,
            "full-meeting" => MeetingExportMode.FullMeeting,
            _ => MeetingExportMode.Notes
        },
        TranscriptPolicy = settings.PostMeetingHookTranscriptPolicy switch
        {
            "inline" => PostMeetingTranscriptPolicy.Inline,
            "auto-export-path" => PostMeetingTranscriptPolicy.AutoExportPath,
            _ => PostMeetingTranscriptPolicy.MetadataOnly
        },
        Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.PostMeetingHookTimeoutSeconds, 1, 600)),
        RetryPolicy = new PostMeetingRetryPolicy
        {
            MaxAttempts = Math.Clamp(settings.PostMeetingHookMaxAttempts, 1, 3),
            Delay = TimeSpan.FromMilliseconds(500)
        }
    };

    public bool HookEnabled { get; init; }
    public string? HookExecutablePath { get; init; }
    public bool AutoExportEnabled { get; init; }

    /// <summary>
    /// Asks for an additional automatic PDF alongside the Markdown export.
    /// </summary>
    public bool AutoExportPdfEnabled { get; init; }

    public string? AutoExportDirectory { get; init; }
    public MeetingExportMode AutoExportMode { get; init; } = MeetingExportMode.FullMeeting;
    public PostMeetingTranscriptPolicy TranscriptPolicy { get; init; } = PostMeetingTranscriptPolicy.MetadataOnly;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxCapturedOutputCharacters { get; init; } = 32 * 1024;
    public PostMeetingRetryPolicy RetryPolicy { get; init; } = new();
}

public sealed record PostMeetingExportDiagnostic(
    bool Requested,
    bool Completed,
    string? DestinationPath,
    AutomationDestinationOwnership DestinationOwnership,
    string? Error,
    int Attempts = 0)
{
    public static PostMeetingExportDiagnostic NotRequested { get; } =
        new(false, false, null, AutomationDestinationOwnership.None, null, 0);
}

public sealed record PostMeetingAutomationResult(
    Guid RunId,
    PostMeetingAutomationStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int Attempts,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string? Error,
    PostMeetingExportDiagnostic Export)
{
    public bool Completed => Status == PostMeetingAutomationStatus.Succeeded;

    /// <summary>
    /// The optional automatic PDF export diagnostic (AUTO-01). Null when PDF auto-export was not
    /// requested. Kept as an additive, nullable property so older serialized results deserialize
    /// unchanged.
    /// </summary>
    public PostMeetingExportDiagnostic? PdfExport { get; init; }
}
