using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Windows meeting detection: strict join-URL validation and the conservative false-positive policy
/// that decides whether a scan is allowed to interrupt the user.
/// </summary>
public sealed class Phase6DetectionTests
{
    // ---- supported join URLs -----------------------------------------------------

    [Theory]
    [InlineData("https://meet.google.com/abc-defg-hij", MeetingUrlParser.GoogleMeet, "abc-defg-hij")]
    [InlineData("https://us02web.zoom.us/j/81234567890", MeetingUrlParser.Zoom, "81234567890")]
    [InlineData("https://zoom.us/wc/81234567890/join", MeetingUrlParser.Zoom, "81234567890")]
    [InlineData("https://acme.zoomgov.com/j/812345678", MeetingUrlParser.Zoom, "812345678")]
    [InlineData("https://teams.live.com/meet/9876543210", MeetingUrlParser.MicrosoftTeams, null)]
    [InlineData("https://acme.webex.com/meet/priya", MeetingUrlParser.Webex, "priya")]
    [InlineData("https://acme.webex.com/join/priya", MeetingUrlParser.Webex, "priya")]
    [InlineData("https://chime.aws/1234567890", MeetingUrlParser.Chime, "1234567890")]
    [InlineData("https://app.chime.aws/meetings/my-standup", MeetingUrlParser.Chime, "my-standup")]
    public void SupportedJoinUrlsAreRecognizedWithTheirPlatform(string url, string platform, string? code)
    {
        var match = MeetingUrlParser.TryParse(url);
        Assert.NotNull(match);
        Assert.Equal(platform, match!.Platform);
        if (code is not null) Assert.Equal(code, match.MeetingCode);
    }

    [Fact]
    public void TeamsMeetupJoinLinksAreRecognized()
    {
        var match = MeetingUrlParser.TryParse(
            "https://teams.microsoft.com/l/meetup-join/19%3ameeting_abcdef%40thread.v2/0?context=%7b%7d");
        Assert.NotNull(match);
        Assert.Equal(MeetingUrlParser.MicrosoftTeams, match!.Platform);
    }

    [Fact]
    public void FaceTimeLinksKeepTheFragmentThatCarriesTheKey()
    {
        var match = MeetingUrlParser.TryParse("https://facetime.apple.com/join#v=1,p=abc,k=def");
        Assert.NotNull(match);
        Assert.Equal(MeetingUrlParser.FaceTime, match!.Platform);
        Assert.Contains("#v=1", match.JoinUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAdvertisedPlatformHasAWorkingParse()
    {
        string[] samples =
        [
            "https://us02web.zoom.us/j/81234567890",
            "https://meet.google.com/abc-defg-hij",
            "https://teams.live.com/meet/9876543210",
            "https://acme.webex.com/meet/priya",
            "https://chime.aws/1234567890",
            "https://facetime.apple.com/join#v=1,p=a,k=b"
        ];
        var parsed = samples.Select(MeetingUrlParser.TryParse).ToList();
        Assert.All(parsed, match => Assert.NotNull(match));
        Assert.Equal(
            MeetingUrlParser.SupportedPlatforms.OrderBy(name => name, StringComparer.Ordinal),
            parsed.Select(match => match!.Platform).Distinct().OrderBy(name => name, StringComparer.Ordinal));
    }

    // ---- invalid and hostile URLs ------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url at all")]
    [InlineData("https://example.com/meeting")]
    [InlineData("https://meet.google.com")]                      // landing page, no meeting
    [InlineData("https://meet.google.com/new")]                  // "start a meeting", not one
    [InlineData("https://meet.google.com/notacode")]             // wrong code shape
    [InlineData("https://zoom.us/pricing")]                      // marketing page
    [InlineData("https://zoom.us/j/12")]                         // implausible meeting id
    [InlineData("https://blog.example.com/how-to-use-zoom.us/j/81234567890")]
    public void NonMeetingUrlsAreRejected(string? url) => Assert.Null(MeetingUrlParser.TryParse(url));

    [Theory]
    [InlineData("https://zoom.us.attacker.example/j/81234567890")]
    [InlineData("https://meet.google.com.attacker.example/abc-defg-hij")]
    [InlineData("https://notzoom.us/j/81234567890")]
    [InlineData("https://fakewebex.com/meet/priya")]
    public void LookalikeHostsCannotImpersonateASupportedPlatform(string url)
    {
        // A substring check would accept every one of these.
        Assert.Null(MeetingUrlParser.TryParse(url));
    }

    [Theory]
    [InlineData("javascript:alert('https://meet.google.com/abc-defg-hij')")]
    [InlineData("file:///C:/meet.google.com/abc-defg-hij")]
    public void NonWebSchemesAreNeverTreatedAsMeetings(string url) =>
        Assert.Null(MeetingUrlParser.TryParse(url));

    // ---- evidence classification -------------------------------------------------

    [Fact]
    public void AValidatedJoinUrlIsStrongEvidence() =>
        Assert.Equal(
            MeetingEvidenceStrength.Strong,
            MeetingEvidenceClassifier.Classify("Some tab", "chrome", "https://meet.google.com/abc-defg-hij"));

    [Fact]
    public void ADedicatedMeetingProcessIsStrongEvidence() =>
        Assert.Equal(
            MeetingEvidenceStrength.Strong,
            MeetingEvidenceClassifier.Classify("Zoom", "CptHost", null));

    [Fact]
    public void ATitleThatMerelyMentionsAPlatformIsOnlyWeakEvidence() =>
        Assert.Equal(
            MeetingEvidenceStrength.Weak,
            MeetingEvidenceClassifier.Classify("How to run a better Zoom meeting - Google Chrome", "chrome", null));

    [Fact]
    public void AnOrdinaryWindowIsNoEvidence() =>
        Assert.Equal(
            MeetingEvidenceStrength.None,
            MeetingEvidenceClassifier.Classify("Inbox - Outlook", "outlook", null));

    // ---- false-positive suppression ----------------------------------------------

    [Fact]
    public void CameraAloneNeverQualifiesAsAMeeting()
    {
        var snapshot = new MeetingPresenceSnapshot(MeetingEvidenceStrength.None, false, true, null);
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(snapshot));
        Assert.Contains("camera in use", MeetingCandidateResolver.DescribeSuppression(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void CameraPlusMicrophoneWithoutAMeetingAppStillNeverQualifies()
    {
        // A video-recording app uses both sensors and is not a meeting.
        var snapshot = new MeetingPresenceSnapshot(MeetingEvidenceStrength.None, true, true, null);
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(snapshot));
    }

    [Fact]
    public void MicrophoneAloneNeverQualifiesAsAMeeting() =>
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(
            new(MeetingEvidenceStrength.None, true, false, null)));

    [Fact]
    public void ATitleOnlyMatchWithoutMicrophoneActivityIsSuppressed()
    {
        var snapshot = new MeetingPresenceSnapshot(MeetingEvidenceStrength.Weak, false, false, "article");
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(snapshot));
        Assert.Contains("no microphone activity", MeetingCandidateResolver.DescribeSuppression(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleOnlyMatchWithLiveMicrophoneQualifies() =>
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(
            new(MeetingEvidenceStrength.Weak, true, false, "titled-window")));

    [Fact]
    public void StrongEvidenceQualifiesWithoutAnySensorActivity() =>
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(
            new(MeetingEvidenceStrength.Strong, false, false, "zoom-meeting")));

    // ---- hysteresis and lifecycle ------------------------------------------------

    private static MeetingPresenceSnapshot Strong(string key) => new(MeetingEvidenceStrength.Strong, false, false, key);
    private static MeetingPresenceSnapshot Empty() => new(MeetingEvidenceStrength.None, false, false, null);

    [Fact]
    public void ATransientWindowDoesNotPromptBeforeTheConfirmationThreshold()
    {
        var resolver = new MeetingCandidateResolver();
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m1")).Action);
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m1")).Action);
    }

    [Fact]
    public void APersistentMeetingPromptsExactlyOnce()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m1")).Action);
        for (var scan = 0; scan < 10; scan++)
        {
            Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m1")).Action);
        }
    }

    [Fact]
    public void FlickeringBetweenTwoCandidatesRestartsConfirmationInsteadOfPrompting()
    {
        var resolver = new MeetingCandidateResolver();
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m1")).Action);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m2")).Action);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m1")).Action);
    }

    [Fact]
    public void AConfirmedMeetingOnlyEndsAfterTheGraceWindow()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        resolver.Observe(Strong("m1"));

        for (var scan = 1; scan < MeetingCandidateResolver.RequiredAbsences; scan++)
        {
            Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Empty()).Action);
        }
        var decision = resolver.Observe(Empty());
        Assert.Equal(MeetingCandidateAction.Ended, decision.Action);
        Assert.Equal("m1", decision.Key);
    }

    [Fact]
    public void ABriefDisappearanceDoesNotEndAConfirmedMeeting()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        resolver.Observe(Strong("m1"));
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Empty()).Action);
        Assert.Equal(MeetingCandidateAction.None, resolver.Observe(Strong("m1")).Action);
        Assert.Equal("m1", resolver.ConfirmedKey);
    }

    [Fact]
    public void DismissingAMeetingSuppressesEveryLaterPromptForIt()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m1")).Action);

        resolver.Dismiss("m1");
        for (var scan = 0; scan < 10; scan++)
        {
            var decision = resolver.Observe(Strong("m1"));
            Assert.Equal(MeetingCandidateAction.None, decision.Action);
            Assert.Contains("dismissed", decision.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DismissingOneMeetingDoesNotSuppressADifferentOne()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Dismiss("m1");
        resolver.Observe(Strong("m2"));
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m2")).Action);
    }

    [Fact]
    public void AMeetingThatEndsAndReturnsCanPromptAgainAfterItsDismissalIsForgotten()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        resolver.Observe(Strong("m1"));
        resolver.Dismiss("m1");

        // The meeting genuinely ends, so the detector forgets the dismissal.
        resolver.Forget("m1");
        resolver.Observe(Strong("m1"));
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m1")).Action);
    }

    [Fact]
    public void ResetClearsConfirmationAndDismissalState()
    {
        var resolver = new MeetingCandidateResolver();
        resolver.Observe(Strong("m1"));
        resolver.Observe(Strong("m1"));
        resolver.Dismiss("m1");
        resolver.Reset();

        Assert.Null(resolver.ConfirmedKey);
        resolver.Observe(Strong("m1"));
        Assert.Equal(MeetingCandidateAction.Prompt, resolver.Observe(Strong("m1")).Action);
    }

    // ---- platform badges ---------------------------------------------------------

    [Fact]
    public void EverySupportedPlatformHasItsOwnBadge()
    {
        Assert.All(MeetingUrlParser.SupportedPlatforms, platform =>
            Assert.True(MeetingPlatformBadges.HasBadge(platform), $"{platform} has no badge"));
    }

    [Fact]
    public void BadgeAccentsAndLabelsAreDistinctSoPlatformsAreTelledApart()
    {
        var badges = MeetingUrlParser.SupportedPlatforms.Select(MeetingPlatformBadges.For).ToList();
        Assert.Equal(badges.Count, badges.Select(badge => badge.AccentHex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(badges.Count, badges.Select(badge => badge.ShortLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(badges, badge => Assert.False(string.IsNullOrWhiteSpace(badge.Glyph)));
    }

    [Fact]
    public void AnUnknownPlatformFallsBackToANeutralBadgeRatherThanThrowing()
    {
        var badge = MeetingPlatformBadges.For("Some Other Conferencing App");
        Assert.False(string.IsNullOrWhiteSpace(badge.Glyph));
        Assert.False(MeetingPlatformBadges.HasBadge("Some Other Conferencing App"));
        Assert.Equal(badge, MeetingPlatformBadges.For(null));
    }

    [Fact]
    public void EveryBadgeAccentIsAParsableColour()
    {
        Assert.All(MeetingUrlParser.SupportedPlatforms, platform =>
        {
            var hex = MeetingPlatformBadges.For(platform).AccentHex;
            Assert.StartsWith("#", hex, StringComparison.Ordinal);
            Assert.True(hex.Length is 4 or 7 or 9);
            Assert.True(uint.TryParse(
                hex.AsSpan(1),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out _));
        });
    }

    [Fact]
    public void AnEmptyScanWithNoConfirmedMeetingReportsWhyItWasSuppressed()
    {
        var resolver = new MeetingCandidateResolver();
        var decision = resolver.Observe(new(MeetingEvidenceStrength.None, false, true, null));
        Assert.Equal(MeetingCandidateAction.None, decision.Action);
        Assert.Contains("camera", decision.Reason, StringComparison.Ordinal);
    }
}
