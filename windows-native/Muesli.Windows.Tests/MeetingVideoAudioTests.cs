using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingVideoAudioTests.swift (2f10b23d).
public sealed class MeetingVideoAudioTests
{
    [Fact]
    public void DelayedAecOutputKeepsCapturePositionsAcrossGaps()
    {
        var spans = new MeetingVideoAudioSpans();
        spans.Append(100, 3);
        spans.Append(200, 3);
        Assert.Equal([100L], spans.Consume([1, 2]).Select(item => item.Start));
        var later = spans.Consume([3, 4, 5, 6]);
        Assert.Equal([102L, 200L], later.Select(item => item.Start));
        Assert.Equal([3], later[0].Samples);
        Assert.Equal([4, 5, 6], later[1].Samples);
    }

    [Fact]
    public void LateAudioIsNotShiftedAndSingleSourceIsNotAttenuated()
    {
        var mix = new MeetingVideoAudioBuffer(100);
        mix.Append([10, 20], 102, microphone: true);
        Assert.Equal([0, 0, 10, 20], mix.Take(4));
        mix.Append([90, 80, 70, 60], 102, microphone: true);
        Assert.Equal([70, 60], mix.Take(2));
        mix.Append([30_000], 106, microphone: true);
        mix.Append([30_000], 106, microphone: false);
        Assert.Equal([short.MaxValue], mix.Take(1));
    }
}
