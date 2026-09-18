namespace Muesli.Windows.Services;

/// <summary>
/// Hold-to-talk timing aligned with the macOS HotkeyTriggerTiming contract.
/// Accidental taps arm the pill but do not start capture until the start delay elapses.
/// </summary>
public static class HotkeyTriggerTiming
{
    public const int DefaultThresholdMilliseconds = 250;
    public const int DefaultMeetingThresholdMilliseconds = 600;
    public const int MinThresholdMilliseconds = 50;
    public const int MaxThresholdMilliseconds = 2_000;
    public const int ShortDiscardMilliseconds = 300;
    public const double DoubleTapTapGuardDelaySeconds = 0.18;
    public const double DoubleTapWindowSeconds = 0.35;

    public static int ClampMilliseconds(int value) =>
        Math.Clamp(value, MinThresholdMilliseconds, MaxThresholdMilliseconds);

    public static TimeSpan StartDelay(int thresholdMilliseconds, bool doubleTapEnabled)
    {
        var startSeconds = ClampMilliseconds(thresholdMilliseconds) / 1000.0;
        if (doubleTapEnabled)
        {
            startSeconds = Math.Max(startSeconds, DoubleTapTapGuardDelaySeconds);
        }

        return TimeSpan.FromSeconds(startSeconds);
    }

    public static TimeSpan PrepareDelay(int thresholdMilliseconds, bool doubleTapEnabled)
    {
        var startSeconds = StartDelay(thresholdMilliseconds, doubleTapEnabled).TotalSeconds;
        var prepareSeconds = Math.Min(0.15, Math.Max(0, startSeconds - 0.10));
        if (doubleTapEnabled)
        {
            prepareSeconds = Math.Max(DoubleTapTapGuardDelaySeconds, prepareSeconds);
        }

        return TimeSpan.FromSeconds(Math.Min(prepareSeconds, startSeconds));
    }

    public static TimeSpan DoubleTapWindow { get; } = TimeSpan.FromSeconds(DoubleTapWindowSeconds);
}
