using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class WhisperCppPreviewDecoderTests
{
    [Fact]
    public void MergesBpeTokensIntoTimedWordsWithMeanConfidence()
    {
        var window = WhisperCppPreviewDecoder.Window("Use parakeet.",
            [(" Use", 0.20, 0.6f), (" par", 0.58, 0.8f), ("ake", 0.78, 0.9f), ("et", 0.90, 1.0f), (".", 1.10, 0.5f)], 2.0);
        Assert.Equal(["Use", " parakeet."], window.Words.Select(word => word.Text));
        Assert.Equal(0.20, window.Words[0].Start, 3);
        Assert.Equal(0.58, window.Words[0].End, 3);
        Assert.Equal(1.10, window.Words[1].End, 3);
        Assert.Equal(0.8f, window.Words[1].Confidence!.Value, 3);
    }

    [Fact]
    public void DefersMissingOutOfRangeAndCollapsedTimings()
    {
        Assert.Equal(BufferedTranscriptionPreviewDeferral.MissingWordTimings,
            Assert.Throws<BufferedTranscriptionPreviewDeferral>(() => WhisperCppPreviewDecoder.Window("hi", [], 1)).Reason);
        Assert.Equal(BufferedTranscriptionPreviewDeferral.InvalidWordTimings,
            Assert.Throws<BufferedTranscriptionPreviewDeferral>(() => WhisperCppPreviewDecoder.Window("a b", [(" a", 0.1, 1f), (" b", 3.0, 1f)], 2)).Reason);
        Assert.Equal(BufferedTranscriptionPreviewDeferral.CollapsedWordTimings,
            Assert.Throws<BufferedTranscriptionPreviewDeferral>(() => WhisperCppPreviewDecoder.Window("a b c d",
                [(" a", 0.5, 1f), (" b", 0.5, 1f), (" c", 0.5, 1f), (" d", 0.5, 1f)], 2)).Reason);
    }
}
