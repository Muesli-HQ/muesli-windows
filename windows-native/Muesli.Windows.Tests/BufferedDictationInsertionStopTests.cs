using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-32 (macOS 4481c15a): the pill's "still recording" notices.</summary>
public sealed class BufferedDictationInsertionStopTests
{
    [Theory]
    [InlineData(BufferedDictationInsertionStop.CursorOwnershipLost, "Cursor updates stopped · Still recording", "Cursor stopped")]
    [InlineData(BufferedDictationInsertionStop.TranscriptionFailed, "Live transcription stopped · Still recording", "Live text stopped")]
    public void TheStatusDetailMapsBackToItsReason(BufferedDictationInsertionStop reason, string title, string compact)
    {
        Assert.Equal(reason, BufferedDictationInsertionStops.FromStatus(reason.Detail()));
        Assert.Equal(title, reason.Title());
        Assert.Equal(compact, reason.CompactTitle());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Listening")]
    [InlineData("Cursor updates stopped · Still recording")]
    public void OtherStatusesCarryNoNotice(string? status) =>
        Assert.Null(BufferedDictationInsertionStops.FromStatus(status));

    [Fact]
    public void ANoticeRebuildsThePillAndWidensIt()
    {
        var plain = new IndicatorSnapshot { State = "recording" };
        var notice = plain with { RecordingNotice = BufferedDictationInsertionStop.CursorOwnershipLost.Title() };
        Assert.True(IndicatorProtocol.IsStructuralChange(plain, notice));
        Assert.False(IndicatorProtocol.IsStructuralChange(notice, notice with { }));

        var width = FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Recording, false, "", false, notice.RecordingNotice).Width;
        Assert.True(width > FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Recording, false).Width);
        Assert.Equal(FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Transcribing, false, "Transcribing"),
            FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Transcribing, false, "Transcribing", false, notice.RecordingNotice));
    }
}
