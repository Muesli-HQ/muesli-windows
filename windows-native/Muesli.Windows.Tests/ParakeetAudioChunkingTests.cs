using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class ParakeetAudioChunkingTests
{
    [Fact]
    public void LongRecordingUsesBoundedGaplessChunksAndPrefersQuietBoundary()
    {
        const int sampleRate = 1000;
        var samples = Enumerable.Repeat(0.2f, sampleRate * 70).ToArray();
        Array.Fill(samples, 0f, sampleRate * 28, sampleRate / 2);

        var ranges = ParakeetAudioChunking.Ranges(samples, sampleRate);

        Assert.True(ranges.Count >= 3);
        Assert.InRange(ranges[0].End, sampleRate * 28, sampleRate * 29);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(samples.Length, ranges[^1].End);
        for (var index = 0; index < ranges.Count; index++)
        {
            Assert.InRange(ranges[index].End - ranges[index].Start, 1, sampleRate * 30);
            if (index > 0) Assert.Equal(ranges[index - 1].End, ranges[index].Start);
        }
    }

    [Fact]
    public void ChunkedTranscriptRestoresOffsetsAndUniqueSegmentIds()
    {
        var segment = new TranscriptSegment("parakeet_seg_0", "You", 100, 700, "hello");
        var first = new TranscriptionResult("hello", Segments: [segment]);
        var second = new TranscriptionResult("world", Segments: [segment with { Text = "world" }]);

        var merged = ParakeetAudioChunking.Merge([(0, first), (480_000, second)], 16_000, 60_000);

        Assert.Equal("hello world", merged.Text);
        Assert.Equal(60_000, merged.DurationMs);
        Assert.Equal([100, 30_100], merged.Segments!.Select(item => item.StartMs));
        Assert.Equal(2, merged.Segments!.Select(item => item.Id).Distinct().Count());
    }
}
