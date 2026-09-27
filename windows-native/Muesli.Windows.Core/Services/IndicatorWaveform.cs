namespace Muesli.Windows.Core.Services;

/// <summary>
/// Pure waveform rendering math for the floating indicator, shared by the WinUI and WPF renderers
/// and unit-testable without a UI runtime. The retired WPF port drifted from the design system's
/// waveform table (2.5-DIP bars, 3-DIP spacing, asymmetric weights, hand-picked heights); these
/// helpers derive every value from <see cref="FloatingIndicatorLayout"/>'s canonical tokens
/// instead, and scale the envelope to each pill so the 26-DIP design maximum never clips the
/// 22-DIP Recording surface.
/// </summary>
public static class IndicatorWaveform
{
    public static int BarCountFor(FloatingIndicatorState state) =>
        state == FloatingIndicatorState.Recording
            ? FloatingIndicatorLayout.RecordingBarMultipliers.Length
            : FloatingIndicatorLayout.PreparingBarMultipliers.Length;

    public static double[] MultipliersFor(FloatingIndicatorState state) =>
        state == FloatingIndicatorState.Recording
            ? FloatingIndicatorLayout.RecordingBarMultipliers
            : FloatingIndicatorLayout.PreparingBarMultipliers;

    /// <summary>The bar height for a smoothed <paramref name="level"/> in [0,1].</summary>
    public static double LevelBarHeight(FloatingIndicatorState state, double level, int index)
    {
        var multipliers = MultipliersFor(state);
        var multiplier = index >= 0 && index < multipliers.Length ? multipliers[index] : 1.0;
        var span = (FloatingIndicatorLayout.WaveformMaxHeightFor(state) - FloatingIndicatorLayout.WaveformMinHeight) * multiplier;
        return FloatingIndicatorLayout.WaveformMinHeight + Math.Clamp(level, 0, 1) * span;
    }

    /// <summary>
    /// The Preparing ("waiting") pulse for one bar at <paramref name="elapsedSeconds"/>. A 0.6s
    /// eased oscillation that automatically reverses, staggered 0.07s per bar so the five bars
    /// ripple outward from the centre. Returns a per-bar multiplier in [0,1] to combine with the
    /// bar's own symmetric envelope weight.
    /// </summary>
    public static double PreparingPulseScale(double elapsedSeconds, int index)
    {
        var period = FloatingIndicatorLayout.WaveformPulsePeriodSeconds;
        if (period <= 0) return 0;
        var phase = elapsedSeconds * (2 * Math.PI) / period
                    - index * FloatingIndicatorLayout.WaveformPulseStaggerSeconds;
        var wave = (Math.Sin(phase) + 1.0) / 2.0;
        return wave * wave * (3 - 2 * wave); // smoothstep: ease-in / ease-out
    }
}

/// <summary>
/// Exponential moving average for live microphone amplitude with the design system's 0.5 weight.
/// </summary>
public sealed class IndicatorAmplitudeSmoother
{
    private double _value;

    public double Current => _value;

    public double Next(double raw)
    {
        var weight = FloatingIndicatorLayout.WaveformAmplitudeSmoothingWeight;
        _value = weight * raw + (1.0 - weight) * _value;
        return _value;
    }

    public void Reset() => _value = 0;
}
