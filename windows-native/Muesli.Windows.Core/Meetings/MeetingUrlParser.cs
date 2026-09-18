using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public sealed record MeetingUrlMatch(
    string Platform,
    string JoinUrl,
    string? MeetingCode)
{
    /// <summary>Short label for the prompt, e.g. "Google Meet abc-defg-hij".</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(MeetingCode) ? Platform : $"{Platform} {MeetingCode}";
}

/// <summary>
/// Recognizes and validates the meeting links Muesli supports.
///
/// Matching is deliberately strict: the host must equal a known domain or be a subdomain of one, and
/// the path must look like a real join route. A substring test would accept
/// <c>https://zoom.us.attacker.example/j/1</c> and any page that merely mentions a platform, which is
/// exactly how a detector starts prompting on articles, docs, and calendar invites in a browser tab.
/// </summary>
public static class MeetingUrlParser
{
    public const string Zoom = "Zoom";
    public const string GoogleMeet = "Google Meet";
    public const string MicrosoftTeams = "Microsoft Teams";
    public const string Webex = "Webex";
    public const string Chime = "Amazon Chime";
    public const string FaceTime = "FaceTime";

    private static readonly Regex MeetCode = new(@"^[a-z]{3}-[a-z]{4}-[a-z]{3}$", RegexOptions.Compiled);
    private static readonly Regex ZoomNumericId = new(@"^\d{9,12}$", RegexOptions.Compiled);

    public static IReadOnlyList<string> SupportedPlatforms { get; } =
        [Zoom, GoogleMeet, MicrosoftTeams, Webex, Chime, FaceTime];

    /// <summary>Returns a validated match, or null when the value is not a supported meeting link.</summary>
    public static MeetingUrlMatch? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (candidate.Length > 2048) return null;
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = $"https://{candidate}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        // Only real web links; a file:// or javascript: value is never a meeting.
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (IsHost(host, "meet.google.com")) return ParseGoogleMeet(uri, segments);
        if (IsHost(host, "zoom.us") || IsHost(host, "zoomgov.com")) return ParseZoom(uri, segments);
        if (IsHost(host, "teams.microsoft.com") || IsHost(host, "teams.live.com")) return ParseTeams(uri, segments);
        if (IsHost(host, "webex.com")) return ParseWebex(uri, segments);
        if (IsHost(host, "chime.aws")) return ParseChime(uri, segments);
        if (IsHost(host, "facetime.apple.com")) return ParseFaceTime(uri, segments);
        return null;
    }

    public static bool IsSupportedMeetingUrl(string? value) => TryParse(value) is not null;

    /// <summary>Host equality or a true subdomain match; never a substring match.</summary>
    private static bool IsHost(string host, string domain) =>
        host.Equals(domain, StringComparison.Ordinal) || host.EndsWith($".{domain}", StringComparison.Ordinal);

    private static MeetingUrlMatch? ParseGoogleMeet(Uri uri, string[] segments)
    {
        // meet.google.com/abc-defg-hij — the bare host is the landing page, not a meeting.
        if (segments.Length == 0) return null;
        var code = segments[0].ToLowerInvariant();
        if (code is "new" or "_meet" or "landing") return null;
        if (!MeetCode.IsMatch(code)) return null;
        return new(GoogleMeet, $"https://meet.google.com/{code}", code);
    }

    private static MeetingUrlMatch? ParseZoom(Uri uri, string[] segments)
    {
        if (segments.Length == 0) return null;
        var route = segments[0].ToLowerInvariant();
        // /j/<id> join, /wc/<id>/join web client, /my/<name> personal room, /s/<id> start.
        if (route is "j" or "wc" or "s")
        {
            var id = segments.Length > 1 ? segments[1] : "";
            if (!ZoomNumericId.IsMatch(id)) return null;
            return new(Zoom, uri.GetLeftPart(UriPartial.Query), id);
        }
        if (route == "my" && segments.Length > 1)
        {
            return new(Zoom, uri.GetLeftPart(UriPartial.Query), segments[1]);
        }
        return null;
    }

    private static MeetingUrlMatch? ParseTeams(Uri uri, string[] segments)
    {
        var path = string.Join('/', segments).ToLowerInvariant();
        // teams.microsoft.com/l/meetup-join/... and teams.live.com/meet/<id>
        if (path.StartsWith("l/meetup-join", StringComparison.Ordinal) ||
            path.StartsWith("meet/", StringComparison.Ordinal) ||
            path.StartsWith("l/message", StringComparison.Ordinal) == false && path.StartsWith("dl/launcher", StringComparison.Ordinal))
        {
            var code = segments.LastOrDefault(segment => segment.Length >= 6);
            return new(MicrosoftTeams, uri.GetLeftPart(UriPartial.Query), code);
        }
        return null;
    }

    private static MeetingUrlMatch? ParseWebex(Uri uri, string[] segments)
    {
        if (segments.Length == 0) return null;
        var route = segments[0].ToLowerInvariant();
        // /meet/<user>, /join/<user>, /wbxmjs/joinservice..., /<site>/j.php?MTID=...
        if (route is "meet" or "join" && segments.Length > 1)
        {
            return new(Webex, uri.GetLeftPart(UriPartial.Query), segments[1]);
        }
        if (route == "wbxmjs" || segments.Any(segment => segment.Equals("j.php", StringComparison.OrdinalIgnoreCase)))
        {
            return new(Webex, uri.GetLeftPart(UriPartial.Query), null);
        }
        return null;
    }

    private static MeetingUrlMatch? ParseChime(Uri uri, string[] segments)
    {
        if (segments.Length == 0) return null;
        // chime.aws/1234567890 and app.chime.aws/meetings/<id>
        if (segments[0].Equals("meetings", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
        {
            return new(Chime, uri.GetLeftPart(UriPartial.Query), segments[1]);
        }
        return segments[0].All(char.IsDigit) && segments[0].Length >= 8
            ? new(Chime, uri.GetLeftPart(UriPartial.Query), segments[0])
            : null;
    }

    private static MeetingUrlMatch? ParseFaceTime(Uri uri, string[] segments)
    {
        // facetime.apple.com/join#v=1,p=...,k=... — the fragment carries the key and must be kept.
        if (segments.Length == 0 || !segments[0].Equals("join", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrWhiteSpace(uri.Fragment)) return null;
        return new(FaceTime, uri.ToString(), null);
    }
}
