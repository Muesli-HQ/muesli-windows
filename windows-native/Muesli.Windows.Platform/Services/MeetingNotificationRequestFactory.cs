namespace Muesli.Windows.Services;

/// <summary>Builds notification requests from detection results and deterministic preview states.</summary>
public static class MeetingNotificationRequestFactory
{
    /// <summary>
    /// An already-joined, detected meeting is transcribe-only: it never opens the meeting URL.
    /// </summary>
    public static MeetingNotificationRequest FromDetectedMeeting(DetectedMeeting meeting)
    {
        var badge = MeetingPlatformBadges.For(meeting.Platform);
        var subtitle = string.IsNullOrWhiteSpace(meeting.Title)
            ? badge.Platform
            : meeting.Title.StartsWith(badge.Platform, StringComparison.OrdinalIgnoreCase)
                ? meeting.Title
                : $"{badge.Platform} · {meeting.Title}";

        return new MeetingNotificationRequest(
            PromptId: meeting.Key,
            Kind: MeetingNotificationKind.ActiveDetected,
            Title: "Meeting detected",
            Subtitle: subtitle,
            Platform: badge.Platform,
            Glyph: badge.Glyph,
            AccentHex: badge.AccentHex,
            ShortLabel: badge.ShortLabel,
            ActionLabel: "Start Transcribing",
            MeetingUrl: null,
            DefaultAction: MeetingJoinDefaultActions.Fallback,
            DismissAfterSeconds: MeetingNotificationPolicy.ActiveAutoDismissSeconds,
            WindowHandle: meeting.WindowHandle);
    }

    /// <summary>
    /// Deterministic preview states for visual qualification. Preview requests never touch
    /// production data and never start capture.
    /// </summary>
    public static IReadOnlyList<MeetingNotificationRequest> Preview(string state)
    {
        var normalized = (state ?? "active").Trim().ToLowerInvariant();
        var active = new MeetingNotificationRequest(
            "preview:active", MeetingNotificationKind.ActiveDetected,
            "Meeting detected", "Google Meet · Product planning", "Google Meet",
            MeetingPlatformBadges.For("Google Meet").Glyph, MeetingPlatformBadges.For("Google Meet").AccentHex, "MEET",
            "Start Transcribing", null, MeetingJoinDefaultActions.Fallback,
            MeetingNotificationPolicy.ActiveAutoDismissSeconds);
        var zoom = new MeetingNotificationRequest(
            "preview:zoom", MeetingNotificationKind.ActiveDetected,
            "Meeting detected", "Zoom · Weekly sync", "Zoom",
            MeetingPlatformBadges.For("Zoom").Glyph, MeetingPlatformBadges.For("Zoom").AccentHex, "ZM",
            "Start Transcribing", null, MeetingJoinDefaultActions.Fallback,
            MeetingNotificationPolicy.ActiveAutoDismissSeconds);
        var longTitle = new MeetingNotificationRequest(
            "preview:long-title", MeetingNotificationKind.ActiveDetected,
            "Meeting detected",
            "Google Meet · Quarterly roadmap review with the platform and infrastructure teams",
            "Google Meet", MeetingPlatformBadges.For("Google Meet").Glyph,
            MeetingPlatformBadges.For("Google Meet").AccentHex, "MEET",
            "Start Transcribing", null, MeetingJoinDefaultActions.Fallback,
            MeetingNotificationPolicy.ActiveAutoDismissSeconds);
        var fallback = new MeetingNotificationRequest(
            "preview:no-platform", MeetingNotificationKind.ActiveDetected,
            "Meeting detected", "Meeting", "Meeting",
            MeetingPlatformBadges.For(null).Glyph, MeetingPlatformBadges.For(null).AccentHex, "MTG",
            "Start Transcribing", null, MeetingJoinDefaultActions.Fallback,
            MeetingNotificationPolicy.ActiveAutoDismissSeconds);
        var scheduled = new MeetingNotificationRequest(
            "preview:scheduled", MeetingNotificationKind.ScheduledUpcoming,
            "Upcoming meeting", "Design review · starts in 2 min", "Microsoft Teams",
            MeetingPlatformBadges.For("Microsoft Teams").Glyph, MeetingPlatformBadges.For("Microsoft Teams").AccentHex, "TEAMS",
            "Join & Transcribe", "https://teams.microsoft.com/l/meetup-join/preview",
            MeetingJoinDefaultActions.Fallback, MeetingNotificationPolicy.ScheduledStartingNowSeconds);

        return normalized switch
        {
            "zoom" => new[] { zoom },
            "long-title" => new[] { longTitle },
            "no-platform" or "fallback" => new[] { fallback },
            "scheduled" or "split" => new[] { scheduled },
            _ => new[] { active }
        };
    }
}
