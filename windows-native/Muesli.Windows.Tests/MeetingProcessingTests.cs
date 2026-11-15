using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// A stopped meeting must finish on an ordinary PC: long recordings are decoded in pieces, and the
/// pill names the step it is on.
/// </summary>
public sealed class MeetingProcessingTests
{
    private const int Rate = 16000;

    [Fact]
    public void ShortRecordingsAreStillDecodedInOnePass()
    {
        var samples = new float[(int)(LongAudioChunker.SinglePassSeconds * Rate)];
        var chunk = Assert.Single(LongAudioChunker.Split(samples, Rate));
        Assert.Equal(0, chunk.Start);
        Assert.Equal(samples.Length, chunk.Length);
        Assert.Single(LongAudioChunker.Split([], Rate));
    }

    [Fact]
    public void LongRecordingsAreCutIntoBoundedPiecesAtQuietPointsWithoutLosingAudio()
    {
        // Six minutes of "speech" with a 300 ms pause every 13 seconds.
        var samples = new float[360 * Rate];
        for (var index = 0; index < samples.Length; index++)
        {
            var inPause = index % (13 * Rate) < (int)(0.3 * Rate);
            samples[index] = inPause ? 0f : (float)Math.Sin(index * 0.05) * 0.4f;
        }

        var chunks = LongAudioChunker.Split(samples, Rate);
        Assert.True(chunks.Count >= 12);
        var position = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(position, chunk.Start);
            Assert.InRange(chunk.Length, 1, (int)(LongAudioChunker.MaxChunkSeconds * Rate));
            position += chunk.Length;
        }
        Assert.Equal(samples.Length, position);

        // Every cut except the end of the file lands inside a pause, not inside speech.
        foreach (var chunk in chunks.Take(chunks.Count - 1))
        {
            var cut = chunk.Start + chunk.Length;
            Assert.True(cut % (13 * Rate) < (int)(0.3 * Rate), $"cut at {cut / (double)Rate:0.00}s is not in a pause");
        }
    }

    [Fact]
    public void ThePillNamesEachStepAndGrowsToFitTheLabel()
    {
        Assert.Equal(
            ["Stopping Audio", "Transcribing", "Identifying Speakers", "Cleaning", "Titling", "Summarizing"],
            Enum.GetValues<MeetingProcessingStage>().Select(MeetingProcessingStages.Label));

        Assert.Equal(FloatingIndicatorLayout.TranscribingWidth, FloatingIndicatorLayout.TranscribingWidthFor("Transcribing"));
        Assert.Equal(FloatingIndicatorLayout.TranscribingWidth, FloatingIndicatorLayout.TranscribingWidthFor(""));
        Assert.True(FloatingIndicatorLayout.TranscribingWidthFor("Identifying Speakers") > FloatingIndicatorLayout.TranscribingWidth);
        Assert.Equal(
            FloatingIndicatorLayout.TranscribingWidthFor("Identifying Speakers"),
            FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Transcribing, false, "Identifying Speakers").Width);
    }
}