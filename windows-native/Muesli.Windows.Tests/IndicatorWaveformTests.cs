using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class IndicatorWaveformTests
{
    [Fact]
    public void Waveform_uses_the_five_bar_symmetric_envelope()
    {
        Assert.Equal(5, IndicatorWaveform.BarCount);
        Assert.Equal([0.6, 0.85, 1.0, 0.85, 0.6], FloatingIndicatorLayout.WaveformBarMultipliers);
    }

    [Fact]
    public void Amplitude_maps_minus_68_to_minus_30_dB_onto_unit_range()
    {
        // macOS IndicatorWaveformDynamics.amplitude: (dB + 68) / 38, clamped.
        Assert.Equal(0, IndicatorWaveform.Amplitude(-68));
        Assert.Equal(0.5, IndicatorWaveform.Amplitude(-49), 6);
        Assert.Equal(1, IndicatorWaveform.Amplitude(-30));
        Assert.Equal(1, IndicatorWaveform.Amplitude(0));
        Assert.Equal(0, IndicatorWaveform.Amplitude(double.NegativeInfinity));
        Assert.Equal(0, IndicatorWaveform.AmplitudeFromRms(0));
        Assert.Equal(IndicatorWaveform.Amplitude(-40), IndicatorWaveform.AmplitudeFromRms(0.01), 6);
    }

    [Fact]
    public void Bar_height_spans_3_to_14_dips_inside_the_22_dip_pill()
    {
        Assert.Equal(3, IndicatorWaveform.BarHeight(0));
        Assert.Equal(14, IndicatorWaveform.BarHeight(1));
        Assert.Equal(3, IndicatorWaveform.BarHeight(-2));
        Assert.Equal(14, IndicatorWaveform.BarHeight(5));
        Assert.True(IndicatorWaveform.BarHeight(1) < FloatingIndicatorLayout.LiveHeight);
    }

    [Fact]
    public void Level_bars_are_shaped_by_the_envelope()
    {
        Assert.Equal(14, IndicatorWaveform.LevelBarHeight(1, 2));
        Assert.True(IndicatorWaveform.LevelBarHeight(1, 1) > IndicatorWaveform.LevelBarHeight(1, 0));
        Assert.Equal(IndicatorWaveform.LevelBarHeight(1, 0), IndicatorWaveform.LevelBarHeight(1, 4), 6);
        Assert.Equal(3, IndicatorWaveform.LevelBarHeight(0, 2));
    }

    [Fact]
    public void Waiting_shimmer_stays_in_bounds_and_staggers_bars()
    {
        for (var index = 0; index < 5; index++)
        {
            for (var t = 0.0; t < 2.0; t += 0.05)
            {
                var (height, opacity) = IndicatorWaveform.WaitingBar(t, index);
                Assert.InRange(height, 3, 14);
                Assert.InRange(opacity, 0.38, 0.74 + 1e-9);
            }
        }
        Assert.NotEqual(IndicatorWaveform.WaitingBar(0.1, 0), IndicatorWaveform.WaitingBar(0.1, 1));
    }

    [Fact]
    public void Smoother_uses_the_048_weight()
    {
        var smoother = new IndicatorAmplitudeSmoother();
        Assert.Equal(0, smoother.Next(0));
        Assert.Equal(0.48, smoother.Next(1), 6);
        Assert.Equal(0.48 + 0.52 * 0.48, smoother.Next(1), 6);
        for (var i = 0; i < 100; i++) smoother.Next(1);
        Assert.InRange(smoother.Current, 0.99, 1.0);
        smoother.Reset();
        Assert.Equal(0, smoother.Current);
    }
}
