namespace Muesli.Windows.Core.Services;

/// <summary>
/// Pure waveform math for the floating indicator, shared by the WinUI and WPF renderers. A port
/// of the macOS <c>IndicatorWaveformDynamics</c> and <c>waveformTimerFired</c>: five standing bars
/// whose height is <c>3 + 11 x amplitude</c>, driven by microphone level while recording and by a
/// staggered shimmer while waiting (Preparing).
/// </summary>
public static class IndicatorWaveform
{
    public static int BarCount => FloatingIndicatorLayout.WaveformBarMultipliers.Length;

    /// <summary>Level-mode bar opacity.</summary>
    public const double LevelOpacity = 0.85;

    /// <summary>Maps average power in dBFS to [0,1]: -68 dB is silent, -30 dB is full scale.</summary>
    public static double Amplitude(double decibels) =>
        double.IsFinite(decibels) ? Math.Clamp((decibels + 68) / 38, 0, 1) : 0;

    /// <summary>Amplitude for a linear RMS level in [0,1].</summary>
    public static double AmplitudeFromRms(double rms) =>
        rms > 0 ? Amplitude(20 * Math.Log10(rms)) : 0;

    public static double Smooth(double amplitude, double previous)
    {
        var weight = FloatingIndicatorLayout.WaveformAmplitudeSmoothingWeight;
        return weight * amplitude + (1 - weight) * previous;
    }

    public static double Weight(int index)
    {
        var multipliers = FloatingIndicatorLayout.WaveformBarMultipliers;
        return index >= 0 && index < multipliers.Length ? multipliers[index] : 1.0;
    }

    public static double BarHeight(double amplitude) =>
        FloatingIndicatorLayout.WaveformMinHeight +
        FloatingIndicatorLayout.WaveformAmplitudeSpan * Math.Clamp(amplitude, 0, 1);

    /// <summary>Recording bar height for a smoothed amplitude in [0,1].</summary>
    public static double LevelBarHeight(double smoothedAmplitude, int index) =>
        BarHeight(smoothedAmplitude * Weight(index));

    /// <summary>The waiting shimmer: each bar breathes in height and opacity, staggered by index.</summary>
    public static (double Height, double Opacity) WaitingBar(double elapsedSeconds, int index)
    {
        var wave = Math.Sin(elapsedSeconds * 5.8 + index * 0.72) + 1;
        return (BarHeight(0.28 + wave * 0.22 * Weight(index)), 0.38 + wave * 0.18);
    }
}

/// <summary>Exponential moving average of live waveform amplitude.</summary>
public sealed class IndicatorAmplitudeSmoother
{
    public double Current { get; private set; }

    public double Next(double amplitude) => Current = IndicatorWaveform.Smooth(amplitude, Current);

    public void Reset() => Current = 0;
}
