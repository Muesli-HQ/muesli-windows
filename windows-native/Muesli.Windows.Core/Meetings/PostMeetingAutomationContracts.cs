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
        AutoExportEnabled = settings.AutoExportMarkdownEnabled,
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
}
