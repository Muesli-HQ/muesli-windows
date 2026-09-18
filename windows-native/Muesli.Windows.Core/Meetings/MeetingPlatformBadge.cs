namespace Muesli.Windows.Services;

public sealed record MeetingPlatformBadge(string Platform, string Glyph, string AccentHex, string ShortLabel);

/// <summary>
/// Platform badges for the detection prompt.
///
/// Muesli does not ship the vendors' trademarked logos, so each platform gets a neutral Segoe Fluent
/// glyph plus that platform's recognisable accent colour and a short label. That identifies the
/// platform at a glance without redistributing marks Muesli has no licence to use.
/// </summary>
public static class MeetingPlatformBadges
{
    private static readonly MeetingPlatformBadge Fallback = new("Meeting", "", "#7C8798", "MTG");

    private static readonly Dictionary<string, MeetingPlatformBadge> Badges = new(StringComparer.OrdinalIgnoreCase)
    {
        [MeetingUrlParser.Zoom] = new(MeetingUrlParser.Zoom, "", "#2D8CFF", "ZM"),
        [MeetingUrlParser.GoogleMeet] = new(MeetingUrlParser.GoogleMeet, "", "#00897B", "MEET"),
        [MeetingUrlParser.MicrosoftTeams] = new(MeetingUrlParser.MicrosoftTeams, "", "#6264A7", "TEAMS"),
        [MeetingUrlParser.Webex] = new(MeetingUrlParser.Webex, "", "#00BCEB", "WBX"),
        [MeetingUrlParser.Chime] = new(MeetingUrlParser.Chime, "", "#F90", "CHIME"),
        [MeetingUrlParser.FaceTime] = new(MeetingUrlParser.FaceTime, "", "#34C759", "FT")
    };

    public static MeetingPlatformBadge For(string? platform) =>
        platform is not null && Badges.TryGetValue(platform, out var badge) ? badge : Fallback;

    /// <summary>Every platform the URL parser can recognise must have a distinct badge.</summary>
    public static bool HasBadge(string? platform) =>
        platform is not null && Badges.ContainsKey(platform);
}
