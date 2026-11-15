using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// macOS keeps what it transcribed during the meeting as the final transcript, so Stop is short.
/// Windows does the same: only stretches the model could not keep up with are transcribed again.
/// </summary>
public sealed class RollingTranscriptOwnershipTests
{
    private const int Rate = 16000;

    [Fact]
    public async Task PiecesTranscribedDuringTheMeetingKeepTheModelsSentenceTiming()
    {
        var preview = new MeetingRollingTranscriber(_ => Task.FromResult(new TranscriptionResult(
            "First sentence. Second sentence.", null, 16000,
            [
                new TranscriptSegment("a", "", 500, 2000, "First sentence."),
                new TranscriptSegment("b", "", 2200, 2900, "Second sentence.")
            ])));
        LiveTranscriptSnapshot? latest = null;
        preview.SnapshotChanged += (_, snapshot) => latest = snapshot;
        preview.Feed(new(LiveTranscriptChannel.System, new float[16 * Rate], 32 * Rate));
        await preview.FinishAsync();

        Assert.NotNull(latest);
        var first = latest.Committed.First(segment => segment.Text == "First sentence.");
        var second = latest.Committed.First(segment => segment.Text == "Second sentence.");
        // Positions are on the meeting's own timeline: the piece began 32 s in.
        Assert.Equal(32 * Rate + 500 * 16, first.StartSample);
        Assert.Equal(32 * Rate + 2000 * 16, first.EndSample);
        Assert.Equal(32 * Rate + 2200 * 16, second.StartSample);
        Assert.Empty(preview.Gaps);
    }

    [Fact]
    public void TheDuringMeetingTranscriptOwnsTheFinalAndOnlyGapsAreRedone()
    {
        var committed = new[]
        {
            new LiveTranscriptSegment("s2", LiveTranscriptChannel.System, 20 * Rate, 25 * Rate, "later"),
            new LiveTranscriptSegment("m1", LiveTranscriptChannel.Microphone, 0, 5 * Rate, "hello"),
            new LiveTranscriptSegment("s1", LiveTranscriptChannel.System, 6 * Rate, 9 * Rate, "hi")
        };
        var gaps = new[] { new LiveTranscriptGap(LiveTranscriptChannel.System, 40 * Rate, 55 * Rate, "transcription-backlog") };

        var result = RollingTranscriptOwnership.Build(committed, gaps, 1, "parakeet-v3");
        Assert.Equal(LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, result.OwnershipMode);
        Assert.Equal("parakeet-v3", result.ModelId);
        Assert.Equal(["m1", "s1", "s2"], result.Committed.Select(segment => segment.Id));
        var gap = Assert.Single(result.Gaps);
        Assert.Equal((40L * Rate, 55L * Rate), (gap.StartSample, gap.EndSample));
    }

    [Fact]
    public void AComputerAudioTrackWithNoTranscriptIsRedoneInFullWhileASilentMicrophoneIsNot()
    {
        // You listened without speaking: the microphone has nothing, and that is not a fault.
        var onlySystem = RollingTranscriptOwnership.Build(
            [new LiveTranscriptSegment("s1", LiveTranscriptChannel.System, 0, Rate, "speech")], [], 0, "m");
        Assert.Empty(onlySystem.Gaps);

        // Nothing from the other side of the call: speech detection may have missed it all.
        var onlyMic = RollingTranscriptOwnership.Build(
            [new LiveTranscriptSegment("m1", LiveTranscriptChannel.Microphone, 0, Rate, "speech")], [], 0, "m");
        var gap = Assert.Single(onlyMic.Gaps);
        Assert.Equal(LiveTranscriptChannel.System, gap.Channel);
        Assert.Equal(long.MaxValue, gap.EndSample);
    }

    [Fact]
    public async Task OnlyTheGapIsTranscribedAgainAtStop()
    {
        var live = RollingTranscriptOwnership.Build(
            [
                new LiveTranscriptSegment("m1", LiveTranscriptChannel.Microphone, 0, 4 * Rate, "kept from the meeting"),
                new LiveTranscriptSegment("s1", LiveTranscriptChannel.System, 5 * Rate, 9 * Rate, "also kept")
            ], [], 0, "m");
        var transcribed = new List<string>();
        var (microphone, system) = await MeetingGapRecoveryService.BuildUnifiedOwnerResultsAsync(
            live, new MeetingAudioPaths(null, null), "m",
            (title, path) => { transcribed.Add(path); return Task.FromResult(new TranscriptionResult("")); },
            CancellationToken.None);

        // No gaps: nothing is re-transcribed, and the transcript is what was heard during the meeting.
        Assert.Empty(transcribed);
        Assert.Equal("kept from the meeting", microphone.Text);
        Assert.Equal("also kept", system.Text);
        Assert.Equal(5000, Assert.Single(system.Segments!).StartMs);
    }
}