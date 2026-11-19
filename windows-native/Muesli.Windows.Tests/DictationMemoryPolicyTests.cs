using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class DictationMemoryPolicyTests
{
    private const ulong MiB = 1048576;
    private const ulong LowMemory = 400 * MiB;
    private const ulong PlentyOfMemory = 4096 * MiB;

    [Theory]
    [InlineData(400, true)]
    [InlineData(1535, true)]
    [InlineData(1536, false)]
    [InlineData(4096, false)]
    public void DecodesDuringCaptureOnlyBelowTheThreshold(int availableMiB, bool expected)
    {
        Assert.Equal(expected, DictationMemoryPolicy.ShouldDecodeDuringCapture((ulong)availableMiB * MiB));
        Assert.Equal(expected, DictationMemoryPolicy.IsLow((ulong)availableMiB * MiB));
    }

    [Fact]
    public void AnUnavailableReadingKeepsBatchAndIsNeverReportedAsANumber()
    {
        Assert.False(DictationMemoryPolicy.ShouldDecodeDuringCapture(null));
        Assert.False(DictationMemoryPolicy.IsLow(null));
        Assert.Equal("unavailable", DictationMemoryPolicy.Describe(null));
        Assert.Equal("400", DictationMemoryPolicy.Describe(LowMemory));
    }

    [Fact]
    public void SlowRecognitionWithLowMemoryShowsTheNotice()
    {
        // The dictation that prompted this: 29 s of speech recognised in 16.7 s with under 400 MB free.
        Assert.Equal(
            DictationSlowNotice.LowMemoryMessage,
            DictationSlowNotice.For(29_000, 16_700, LowMemory, modelWarmupPending: false));
    }

    [Theory]
    [InlineData(30_000, 4_300)] // normal speed for 30 s of speech
    [InlineData(10_000, 1_700)]
    [InlineData(2_000, 1_500)] // slow for its length, but a wait too short to explain
    [InlineData(2_000, 2_999)]
    [InlineData(0, 5_000)]
    public void OrdinaryOrShortWaitsShowNothing(long audioMs, long recognitionMs)
    {
        Assert.Null(DictationSlowNotice.For(audioMs, recognitionMs, LowMemory, modelWarmupPending: false));
    }

    [Fact]
    public void SlownessIsNotBlamedOnMemoryWithoutALowReading()
    {
        Assert.Null(DictationSlowNotice.For(29_000, 16_700, PlentyOfMemory, modelWarmupPending: false));
        Assert.Null(DictationSlowNotice.For(29_000, 16_700, null, modelWarmupPending: false));
    }

    [Fact]
    public void ADecodeThatWaitedForTheModelToLoadShowsNothing()
    {
        Assert.Null(DictationSlowNotice.For(29_000, 16_700, LowMemory, modelWarmupPending: true));
    }

    [Fact]
    public void TheNoticeIsANeutralPillThatYieldsToTheNextDictation()
    {
        var status = DictationSlowNotice.LowMemoryMessage;
        Assert.Equal(FloatingIndicatorState.Notice, FloatingIndicatorStateClassifier.Classify(status, false, false));
        Assert.Equal(FloatingIndicatorState.Recording, FloatingIndicatorStateClassifier.Classify(status, true, false));
        Assert.True(FloatingIndicatorStateClassifier.IsTransientOutcome(FloatingIndicatorState.Notice));
        Assert.False(FloatingIndicatorStateClassifier.IsActive(FloatingIndicatorState.Notice));
        // Once its dwell has passed, the sticky status must not bring it back.
        Assert.True(FloatingIndicatorStateClassifier.IsAlreadyDismissed(FloatingIndicatorState.Notice, status, status));
        Assert.Equal(FloatingIndicatorState.Idle, FloatingIndicatorStateClassifier.Classify("Dictation inserted", false, false));
        Assert.Equal(FloatingIndicatorState.Notice, FloatingIndicatorProbe.ParseState("notice"));
    }
}
