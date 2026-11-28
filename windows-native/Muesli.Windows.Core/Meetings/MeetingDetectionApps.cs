namespace Muesli.Windows.Services;

/// <summary>One app whose detected calls can be muted (macOS <c>MeetingDetectionAppOption</c>).</summary>
public sealed record MeetingDetectionApp(string Id, string Name, Func<string, bool> MatchesProcess);

/// <summary>
/// The apps listed under "Don't notify me when a call is detected in these apps:". macOS lists
/// bundle ids; Windows matches the detected process name. Muting a browser mutes every meeting
/// site in it, as on macOS.
/// </summary>
public static class MeetingDetectionApps
{
    public static IReadOnlyList<MeetingDetectionApp> All { get; } =
    [
        new("chrome", "Chrome", Is("chrome")),
        new("msedge", "Edge", Is("msedge")),
        new("brave", "Brave", Is("brave")),
        new("firefox", "Firefox", Is("firefox")),
        new("opera", "Opera", Is("opera")),
        new("slack", "Slack", Is("slack")),
        new("zoom", "Zoom", name => Is("zoom")(name) || Is("CptHost")(name) || Is("Zoom Workplace")(name)),
        new("teams", "Teams", name => name.Contains("teams", StringComparison.OrdinalIgnoreCase)),
        // Webex calls can surface through its helper processes, which detection also treats as
        // Webex (MeetingPresenceSignals): CiscoCollabHost and the atmgr meeting manager.
        new("webex", "Webex", name => name.Contains("webex", StringComparison.OrdinalIgnoreCase) ||
                                      Is("CiscoCollabHost")(name) || Is("atmgr")(name)),
        new("chime", "Amazon Chime", name => name.Contains("chime", StringComparison.OrdinalIgnoreCase)),
        new("whatsapp", "WhatsApp", Is("WhatsApp")),
        new("discord", "Discord", Is("Discord"))
    ];

    /// <summary>The listed app a detected meeting's process belongs to, if any.</summary>
    public static MeetingDetectionApp? ForProcess(string? processName) =>
        string.IsNullOrWhiteSpace(processName) ? null : All.FirstOrDefault(app => app.MatchesProcess(processName.Trim()));

    /// <summary>True when the user muted detection for the app this process belongs to.</summary>
    public static bool IsMuted(IEnumerable<string>? mutedIds, string? processName) =>
        ForProcess(processName) is { } app &&
        (mutedIds ?? []).Contains(app.Id, StringComparer.OrdinalIgnoreCase);

    private static Func<string, bool> Is(string processName) =>
        name => name.Equals(processName, StringComparison.OrdinalIgnoreCase);
}
