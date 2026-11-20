using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

// Ported from macOS SpeakerAttributionTests / TranscriptFormatterTests (2f10b23d), in milliseconds.
public sealed class SpeakerAttributionTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 9, 0, 0);

    [Fact]
    public void BriefGuestsCountAndOneSignificantVoiceKeepsYou()
    {
        var guest = new List<DiarizedSegment> { new("a", 0, 600_000), new("b", 600_000, 606_000), new("c", 606_000, 608_000) };
        Assert.Equal(["a", "b"], SpeakerAttribution.SignificantSpeakerIds(guest).Order());
        Assert.DoesNotContain(SpeakerAttribution.LocalDiarization(guest)!, segment => segment.SpeakerId == "c");
        Assert.Null(SpeakerAttribution.LocalDiarization([new("a", 0, 60_000), new("c", 60_000, 62_000)]));
        Assert.Null(SpeakerAttribution.LocalDiarization(null));
    }

    [Fact]
    public void TotalOverlapWinsAndNearestSpeakerFallsBackWithinTwoSeconds()
    {
        // A–B–A: B owns the single longest stretch, A the larger total.
        List<DiarizedSegment> diarization = [new("A", 0, 1_500), new("B", 1_500, 3_500), new("A", 3_500, 5_000)];
        Assert.Equal("A", SpeakerAttribution.SpeakerId(new("s", "You", 0, 5_000, "x"), diarization));
        Assert.Equal("A", SpeakerAttribution.SpeakerId(new("s", "You", 6_000, 6_500, "x"), diarization));
        Assert.Null(SpeakerAttribution.SpeakerId(new("s", "You", 9_000, 9_500, "x"), diarization));
    }

    [Fact]
    public void SentencesShareTheTurnByVisibleLength()
    {
        var split = SpeakerAttribution.SentenceSegments([new("m", "You", 1_000, 3_000, "Hi there. Okay!")]);
        Assert.Equal(["Hi there.", "Okay!"], split.Select(segment => segment.Text));
        Assert.Equal(1_000, split[0].StartMs);
        Assert.Equal(split[0].EndMs, split[1].StartMs);
        Assert.Equal(3_000, split[1].EndMs);
        Assert.True(split[0].EndMs - split[0].StartMs > split[1].EndMs - split[1].StartMs);
    }

    [Fact]
    public void MicTurnsSplitIntoLocalSpeakersNumberedApartFromRemoteOnes()
    {
        var formatted = TranscriptFormatter.Merge(
            [new TranscriptSegment("m", "You", 0, 12_000, "Let us start. Thanks, I agree.")],
            [new TranscriptSegment("s", "System", 20_000, 21_000, "Remote here")],
            [new DiarizedSegment("r", 19_000, 22_000)],
            Start,
            [new DiarizedSegment("x", 0, 6_000), new DiarizedSegment("y", 6_000, 12_000)]);
        Assert.Contains("Local speaker 1: Let us start.", formatted);
        Assert.Contains("Local speaker 2: Thanks, I agree.", formatted);
        Assert.Contains("Speaker 1: Remote here", formatted);
        Assert.DoesNotContain("You:", formatted);

        var single = TranscriptFormatter.Merge(
            [new TranscriptSegment("m", "You", 0, 6_000, "Just me.")], [], [], Start, [new DiarizedSegment("x", 0, 6_000)]);
        Assert.DoesNotContain("Local speaker", single);
        Assert.Contains("You", single);
    }

    [Fact]
    public void ChatParserRecognisesLocalSpeakerLabels()
    {
        var messages = TranscriptChatMessage.Parse("[00:00:01] Local speaker 2: hi\n[00:00:02] Local speaker: yo");
        Assert.Equal(["Local speaker 2", "Local speaker"], messages.Select(message => message.Speaker));
        Assert.Equal(["hi", "yo"], messages.Select(message => message.Text));
    }
}
