using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class IndicatorWaveformTests
{
    [Fact]
    public void Preparing_uses_the_five_bar_symmetric_envelope()
    {
        Assert.Equal(5, IndicatorWaveform.BarCountFor(FloatingIndicatorState.Preparing));
        Assert.Equal(FloatingIndicatorLayout.PreparingBarMultipliers, IndicatorWaveform.MultipliersFor(FloatingIndicatorState.Preparing));
        // Symmetric about the centre bar.
        Assert.Equal(0.6, FloatingIndicatorLayout.PreparingBarMultipliers[0]);
        Assert.Equal(0.85, FloatingIndicatorLayout.PreparingBarMultipliers[1]);
        Assert.Equal(1.0, FloatingIndicatorLayout.PreparingBarMultipliers[2]);
        Assert.Equal(0.85, FloatingIndicatorLayout.PreparingBarMultipliers[3]);
        Assert.Equal(0.6, FloatingIndicatorLayout.PreparingBarMultipliers[4]);
    }

    [Fact]
    public void Recording_uses_the_five_bar_symmetric_envelope()
    {
        // The macOS reference animates the same five-bar envelope for recording and preparing;
        // the retired Windows port's four-bar shape was the deviation.
        Assert.Equal(5, IndicatorWaveform.BarCountFor(FloatingIndicatorState.Recording));
        Assert.Equal(FloatingIndicatorLayout.RecordingBarMultipliers, IndicatorWaveform.MultipliersFor(FloatingIndicatorState.Recording));
        Assert.Equal([0.6, 0.85, 1.0, 0.85, 0.6], FloatingIndicatorLayout.RecordingBarMultipliers);
    }

    [Fact]
    public void Recording_waveform_is_capped_inside_the_22_dip_pill()
    {
        // The design-system 26-DIP envelope would overflow the 22-DIP recording pill, so the
        // recording span is scaled to a 14-DIP ceiling while the square-waveform shape stays.
        Assert.Equal(14, FloatingIndicatorLayout.WaveformMaxHeightFor(FloatingIndicatorState.Recording));
        var loudest = IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, 1.0, 1);
        Assert.InRange(loudest, FloatingIndicatorLayout.WaveformMinHeight, FloatingIndicatorLayout.WaveformMaxHeightFor(FloatingIndicatorState.Recording));
    }

    [Fact]
    public void Preparing_waveform_can_use_the_full_envelope()
    {
        Assert.Equal(26, FloatingIndicatorLayout.WaveformMaxHeightFor(FloatingIndicatorState.Preparing));
        var loudest = IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Preparing, 1.0, 2);
        Assert.InRange(loudest, FloatingIndicatorLayout.WaveformMinHeight, FloatingIndicatorLayout.WaveformMaxHeight);
    }

    [Fact]
    public void LevelBarHeight_is_monotonic_and_shaped_by_the_envelope()
    {
        var quiet = IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, 0.0, 1);
        var loud = IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, 1.0, 1);
        Assert.True(loud > quiet);
        // Inner bars reach higher than outer bars at the same level.
        Assert.True(
            IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, 1.0, 1) >
            IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, 1.0, 0));
    }

    [Fact]
    public void LevelBarHeight_never_goes_below_minimum()
    {
        Assert.Equal(
            FloatingIndicatorLayout.WaveformMinHeight,
            IndicatorWaveform.LevelBarHeight(FloatingIndicatorState.Recording, -2.0, 1));
    }

    [Fact]
    public void PreparingPulseScale_stays_in_unit_range()
    {
        for (var index = 0; index < 5; index++)
        {
            for (var t = 0.0; t < 2.0; t += 0.05)
            {
                var scale = IndicatorWaveform.PreparingPulseScale(t, index);
                Assert.InRange(scale, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void PreparingPulseScale_staggers_neighbouring_bars()
    {
        // At a fixed time, adjacent bars are at different phases because of the 0.07s stagger.
        var t = 0.123;
        var leading = IndicatorWaveform.PreparingPulseScale(t, 0);
        var trailing = IndicatorWaveform.PreparingPulseScale(t, 1);
        Assert.NotEqual(leading, trailing);
    }

    [Fact]
    public void Smoother_converges_toward_the_input_with_exponential_weight()
    {
        var smoother = new IndicatorAmplitudeSmoother();
        Assert.Equal(0, smoother.Next(0.0f));
        Assert.Equal(0.4, smoother.Next(0.8f), 6);    // 0.5*0.8 + 0.5*0.0
        Assert.Equal(0.6, smoother.Next(0.8f), 6);    // 0.5*0.8 + 0.5*0.4
        // Toward steady state.
        for (var i = 0; i < 100; i++) smoother.Next(1.0f);
        Assert.InRange(smoother.Current, 0.99, 1.0);
    }
}
