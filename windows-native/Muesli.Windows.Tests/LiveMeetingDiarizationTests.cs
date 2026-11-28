using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class LiveMeetingDiarizationTests
{
    [Fact]
    public void LinkerJoinsSimilarSpeakersAndSeparatesDifferentOnes()
    {
        var linker = new LiveSpeakerLinker();

        var first = linker.Assign([1f, 0f, 0f], 10);
        var sameVoiceLater = linker.Assign([0.9f, 0.1f, 0f], 5);
        var otherVoice = linker.Assign([0f, 1f, 0f], 8);
        var tooShort = linker.AddUnmatched();
        var otherVoiceAgain = linker.Assign([0.1f, 0.95f, 0.05f], 3);

        Assert.Equal(0, first);
        Assert.Equal(first, sameVoiceLater);
        Assert.Equal(1, otherVoice);
        Assert.Equal(2, tooShort);
        Assert.Equal(otherVoice, otherVoiceAgain);
        Assert.Equal(3, linker.SpeakerCount);
    }

    [Fact]
    public void LinkerUsesDurationWeightedCentroid()
    {
        var linker = new LiveSpeakerLinker(threshold: 0.8f);

        linker.Assign([1f, 0f], 20);
        // A brief, off-centre turn joins without dragging the long speaker's centroid away.
        Assert.Equal(0, linker.Assign([0.85f, 0.5f], 0.5));
        Assert.Equal(0, linker.Assign([1f, 0.05f], 1));
    }

    [Fact]
    public void MergeAdjacentJoinsBlockBoundarySplitsOfOneSpeaker()
    {
        var merged = LiveMeetingDiarizationSession.MergeAdjacent(
        [
            new DiarizedSegment("speaker_0", 0, 29_970),
            new DiarizedSegment("speaker_0", 30_030, 40_000),
            new DiarizedSegment("speaker_1", 40_100, 45_000),
            new DiarizedSegment("speaker_0", 46_000, 50_000),
            new DiarizedSegment("speaker_0", 52_000, 53_000)
        ]);

        Assert.Equal(
        [
            new DiarizedSegment("speaker_0", 0, 40_000),
            new DiarizedSegment("speaker_1", 40_100, 45_000),
            new DiarizedSegment("speaker_0", 46_000, 50_000),
            new DiarizedSegment("speaker_0", 52_000, 53_000)
        ],
        merged);
    }

    [Theory]
    [InlineData(160_000, 160_000, true)]
    [InlineData(160_000, 163_000, true)]
    [InlineData(160_000, 170_000, false)]
    [InlineData(0, 0, false)]
    public void StreamMustMatchFinalTrackLength(long fed, long track, bool expected) =>
        Assert.Equal(expected, LiveMeetingDiarizationSession.StreamMatchesTrack(fed, track));

    [Fact]
    public async Task CompleteFallsBackWhenTheFinalTrackIsLongerThanTheFedStream()
    {
        using var directory = new TestDirectory();
        var path = WriteSilence(directory.Path, 64_000);
        using var client = new NativeDiarizationClient();
        using var session = new LiveMeetingDiarizationSession(client);

        session.Feed(new float[8_000]);
        var completion = await session.CompleteAsync(path, CancellationToken.None);

        Assert.Null(completion.Result);
        Assert.Equal("length-mismatch", completion.FallbackReason);
        Assert.Equal(8_000, completion.FedSamples);
        Assert.Equal(64_000, completion.TrackSamples);
    }

    [Fact]
    public async Task CompleteReturnsLiveResultWhenTheStreamCoversTheTrack()
    {
        using var directory = new TestDirectory();
        var path = WriteSilence(directory.Path, 8_000);
        using var client = new NativeDiarizationClient();
        using var session = new LiveMeetingDiarizationSession(client);

        // Under a second of audio is never diarized, so this needs no model.
        session.Feed(new float[4_000]);
        session.Feed(new float[4_000]);
        var completion = await session.CompleteAsync(path, CancellationToken.None);
        session.Feed(new float[16_000]);

        Assert.NotNull(completion.Result);
        Assert.Null(completion.FallbackReason);
        Assert.Empty(completion.Result.Segments);
        Assert.Equal(500, completion.Result.DurationMs);
        Assert.Equal(8_000, session.FedSamples);
        Assert.Equal(0, completion.BlocksProcessed);
    }

    [Fact]
    public async Task CompleteFallsBackWhenBlocksPileUpFasterThanTheyAreDiarized()
    {
        using var directory = new TestDirectory();
        var path = WriteSilence(directory.Path, 16_000 * 8);
        using var client = new NativeDiarizationClient();
        var release = new TaskCompletionSource();
        using var session = new LiveMeetingDiarizationSession(
            client,
            blockSeconds: 1,
            async _ =>
            {
                await release.Task;
                return [];
            });

        for (var second = 0; second < LiveMeetingDiarizationSession.MaxBacklogBlocks + 4; second++)
        {
            session.Feed(new float[16_000]);
        }
        release.SetResult();
        var completion = await session.CompleteAsync(path, CancellationToken.None);

        Assert.Null(completion.Result);
        Assert.Equal("backlog-overflow", completion.FallbackReason);
    }

    [Fact]
    public async Task CompleteOffsetsBlockSegmentsIntoTrackTime()
    {
        using var directory = new TestDirectory();
        var path = WriteSilence(directory.Path, 16_000 * 2);
        using var client = new NativeDiarizationClient();
        using var session = new LiveMeetingDiarizationSession(
            client,
            blockSeconds: 1,
            // Turns too short to embed, so no native model is touched.
            _ => Task.FromResult<IReadOnlyList<NativeDiarizationClient.LocalSpeakerSegment>>(
                [new NativeDiarizationClient.LocalSpeakerSegment(0.1f, 0.2f, 0)]));

        session.Feed(new float[16_000 * 2]);
        var completion = await session.CompleteAsync(path, CancellationToken.None);

        Assert.NotNull(completion.Result);
        Assert.Equal(2, completion.BlocksProcessed);
        Assert.Equal([100, 1_100], completion.Result.Segments.Select(segment => segment.StartMs));
        Assert.Equal(2, completion.Result.Segments.Select(segment => segment.SpeakerId).Distinct().Count());
    }

    private static string WriteSilence(string directory, int samples)
    {
        var path = Path.Combine(directory, "system.wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16_000, 1));
        writer.WriteSamples(new float[samples], 0, samples);
        return path;
    }
}
