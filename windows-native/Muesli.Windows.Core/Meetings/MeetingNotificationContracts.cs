namespace Muesli.Windows.Services;

/// <summary>
/// The kind of meeting surface a notification represents. Windows currently only produces
/// <see cref="ActiveDetected"/> from the live detector; <see cref="ScheduledUpcoming"/> exists so the
/// split-action state can be built and tested deterministically until a real scheduled-meeting
/// source is wired up.
/// </summary>
public enum MeetingNotificationKind
{
    ActiveDetected,
    ScheduledUpcoming,
    SignalLost
}

/// <summary>The three configurable scheduled-meeting actions, mirroring the macOS default action.</summary>
public enum MeetingJoinDefaultAction
{
    JoinAndRecord,
    JoinOnly,
    TranscribeOnly
}

public static class MeetingJoinDefaultActions
{
    public const MeetingJoinDefaultAction Fallback = MeetingJoinDefaultAction.JoinAndRecord;

    public static string Label(this MeetingJoinDefaultAction action) => action switch
    {
        MeetingJoinDefaultAction.JoinAndRecord => "Join & Transcribe",
        MeetingJoinDefaultAction.JoinOnly => "Join Only",
        _ => "Transcribe Only"
    };

    /// <summary>Resolves the armed action for the actions that are actually available.</summary>
    public static MeetingJoinDefaultAction Resolved(
        this MeetingJoinDefaultAction action, bool hasJoinAndRecord, bool hasJoinOnly) => action switch
        {
            MeetingJoinDefaultAction.JoinAndRecord => hasJoinAndRecord
                ? MeetingJoinDefaultAction.JoinAndRecord
                : MeetingJoinDefaultAction.TranscribeOnly,
            MeetingJoinDefaultAction.JoinOnly => hasJoinOnly
                ? MeetingJoinDefaultAction.JoinOnly
                : MeetingJoinDefaultAction.TranscribeOnly,
            _ => MeetingJoinDefaultAction.TranscribeOnly
        };

    /// <summary>The other actions offered by the chevron menu, in menu order.</summary>
    public static IReadOnlyList<MeetingJoinDefaultAction> AvailableAlternatives(
        this MeetingJoinDefaultAction action, bool hasJoinAndRecord, bool hasJoinOnly)
    {
        var armed = action.Resolved(hasJoinAndRecord, hasJoinOnly);
        var result = new List<MeetingJoinDefaultAction>();
        foreach (var candidate in new[]
                 {
                     MeetingJoinDefaultAction.JoinAndRecord,
                     MeetingJoinDefaultAction.JoinOnly,
                     MeetingJoinDefaultAction.TranscribeOnly
                 })
        {
            if (candidate == armed) continue;
            if (candidate == MeetingJoinDefaultAction.JoinAndRecord && !hasJoinAndRecord) continue;
            if (candidate == MeetingJoinDefaultAction.JoinOnly && !hasJoinOnly) continue;
            result.Add(candidate);
        }

        return result;
    }
}

/// <summary>A user action or terminal state for a meeting notification.</summary>
public enum MeetingNotificationAction
{
    StartTranscribing,
    JoinAndRecord,
    JoinOnly,
    TranscribeOnly,
    Dismiss,
    StopTranscribing
}

/// <summary>
/// The observable outcome of trying to present a notification. The app logs the exact sanitized
/// reason for every non-shown outcome instead of silently returning.
/// </summary>
public enum MeetingNotificationOutcome
{
    ActiveMeetingDetected,
    UpcomingScheduledMeeting,
    RecordingAlreadyActive,
    CaptureStarting,
    StartFailed,
    Dismissed,
    AutoDismissed,
    SuppressedDuplicate,
    SuppressedBusy,
    SuppressedDisabled,
    MeetingSignalLost
}

/// <summary>Everything the notification window needs to render one prompt.</summary>
public sealed record MeetingNotificationRequest(
    string PromptId,
    MeetingNotificationKind Kind,
    string Title,
    string Subtitle,
    string Platform,
    string Glyph,
    string AccentHex,
    string ShortLabel,
    string ActionLabel,
    string? MeetingUrl,
    MeetingJoinDefaultAction DefaultAction = MeetingJoinDefaultActions.Fallback,
    double DismissAfterSeconds = MeetingNotificationPolicy.ActiveAutoDismissSeconds,
    nint WindowHandle = 0)
{
    public bool IsScheduled => Kind == MeetingNotificationKind.ScheduledUpcoming;
    public MeetingNotificationAction SingleAction => Kind == MeetingNotificationKind.SignalLost
        ? MeetingNotificationAction.StopTranscribing : MeetingNotificationAction.StartTranscribing;

    public bool HasJoinActions =>
        IsScheduled && !string.IsNullOrWhiteSpace(MeetingUrl);

    public bool HasJoinAndRecord => HasJoinActions;

    public bool HasJoinOnly => HasJoinActions;

    public bool HasSplitAction => HasJoinActions;
}

/// <summary>
/// Callbacks the notification window forwards to the owning service after the fade-out. The window
/// itself never owns capture, persistence, or transcription.
/// </summary>
public sealed record MeetingNotificationCallbacks(
    Action<MeetingNotificationAction> OnAction,
    Action OnDismiss,
    Action OnAutoDismiss);

public static class MeetingNotificationPolicy
{
    /// <summary>Default auto-dismiss for an active detected meeting, in seconds.</summary>
    public const double ActiveAutoDismissSeconds = 15;

    /// <summary>A scheduled "starting now" prompt may stay for 30 seconds.</summary>
    public const double ScheduledStartingNowSeconds = 30;

    /// <summary>Fade-out duration applied on action, dismissal, and auto-dismiss.</summary>
    public const double FadeOutSeconds = 0.2;
}
