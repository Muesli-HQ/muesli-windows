namespace Muesli.Windows.Core.Services;

/// <summary>A size plus corner radius, in DIPs, for the floating indicator pill.</summary>
public readonly record struct DipSize(double Width, double Height, double CornerRadius);

/// <summary>A point in DIPs.</summary>
public readonly record struct DipPoint(double X, double Y);

/// <summary>An axis-aligned rectangle in DIPs.</summary>
public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public DipPoint Center => new(Left + Width / 2, Top + Height / 2);
}

/// <summary>
/// Pure geometry and metrics for the floating dictation indicator. This is the WinUI port of the
/// WPF reference's inline constants and <c>IndicatorSizeFor</c>/<c>ClampWindowPosition</c>. All
/// values are DIPs; physical-pixel conversion is the caller's concern (per-monitor DPI).
/// </summary>
public static class FloatingIndicatorLayout
{
    public const double CompactIdleWidth = 44;
    public const double CompactIdleHeight = 28;
    public const double CompactIdleRadius = 14;

    public const double ExpandedIdleWidth = 220;
    public const double ExpandedIdleHeight = 36;
    public const double ExpandedIdleRadius = 18;

    /// <summary>
    /// Preparing is the compact pill, not the live one. The macOS design system's state table
    /// gives Preparing 44x28 at #1e1e2e 62%; the retired WPF port used the 76x22 recording size
    /// for it, which is a deviation from the original.
    /// </summary>
    public const double PreparingWidth = 44;
    public const double PreparingHeight = 28;
    public const double PreparingRadius = 14;

    public const double LiveWidth = 76;
    public const double LiveHeight = 22;
    public const double LiveRadius = 11;

    /// <summary>
    /// Window-level opacity per the design system's "Alpha" column. Only the compact idle pill is
    /// translucent as a whole (0.85); every other state is fully opaque at the window level and
    /// gets its translucency from the fill's own alpha over the frost.
    /// </summary>
    public static double WindowAlpha(FloatingIndicatorState state, bool hovered) =>
        state == FloatingIndicatorState.Idle && !hovered ? 0.85 : 1.0;

    public const double TranscribingWidth = 120;
    public const double TranscribingHeight = 32;
    public const double TranscribingRadius = 16;

    public const double StatusWidth = 220;
    public const double StatusWidthLong = 260;
    public const double StatusHeight = 36;
    public const double StatusRadius = 18;

    public const double DragThreshold = 4;
    public const double ScreenEdgeMargin = 8;
    public const double AnchorEdgeMargin = 18;
    public const double CancelRegionWidthDip = 30;

    public const long SuccessReturnMs = 2200;
    public const long ErrorReturnMs = 3600;

    /// <summary>Message lengths beyond this widen the status pill from 220 to 260 DIPs.</summary>
    public const int LongStatusMessageChars = 24;

    // ─── Waveform, per the macOS design system's "Waveform Specifications" ───────────────────
    // (design-system/muesli-design-system.html). The retired WPF port drifted from these: it used
    // 2.5pt bars at 3pt spacing with hand-picked heights, and per-bar weights of
    // [0.62, 1.0, 0.84, 0.52] that are not symmetric. These are the reference values.

    /// <summary>Bar width in DIPs.</summary>
    public const double WaveformBarWidth = 3;

    /// <summary>Gap between bars in DIPs.</summary>
    public const double WaveformBarSpacing = 4;

    /// <summary>Quietest and loudest bar heights in DIPs.</summary>
    public const double WaveformMinHeight = 5;
    public const double WaveformMaxHeight = 26;

    /// <summary>
    /// The tallest bar the live recording waveform may draw, in DIPs. The design system's 26-DIP
    /// maximum is the envelope for the 28-DIP Preparing surface; the Recording pill is only
    /// 22 DIPs tall, so the full envelope would clip. The macOS reference caps live bars below
    /// the pill height (it drives a 14-DIP ceiling inside the recording pill's interior), and the
    /// envelope shape — <see cref="RecordingBarMultipliers"/> — is preserved; only the span is
    /// scaled to fit. See <see cref="WaveformMaxHeightFor"/>.
    /// </summary>
    public const double RecordingWaveformMaxHeight = 14;

    /// <summary>
    /// The effective loudest bar height for a state's pill. Preparing keeps the full 26-DIP
    /// design envelope (its 28-DIP compact pill leaves 1 DIP of breathing room top and bottom);
    /// Recording scales the span down to a 14-DIP ceiling so the four bars stay inside the
    /// 22-DIP capsule without visual overflow.
    /// </summary>
    public static double WaveformMaxHeightFor(FloatingIndicatorState state) =>
        state == FloatingIndicatorState.Recording ? RecordingWaveformMaxHeight : WaveformMaxHeight;

    /// <summary>Bar and stop-square fill opacity.</summary>
    public const double WaveformOpacity = 0.85;

    /// <summary>Stop square size in DIPs (6×6) and its corner radius (1 DIP).</summary>
    public const double StopSquareSize = 6;
    public const double StopSquareRadius = 1;

    // ─── Waveform animation, per "Waveform Specifications" and "Animation & Interaction" ─────
    /// <summary>Full pulse period (one scale-Y cycle) in seconds.</summary>
    public const double WaveformPulsePeriodSeconds = 0.6;

    /// <summary>Per-bar phase offset, in seconds, from leading to trailing bar.</summary>
    public const double WaveformPulseStaggerSeconds = 0.07;

    /// <summary>Maximum waveform/amplitude refresh rate.</summary>
    public const int WaveformUpdateFramesPerSecond = 30;

    /// <summary>Live amplitude smoothing: exponential-average weight for each new sample.</summary>
    public const double WaveformAmplitudeSmoothingWeight = 0.5;

    /// <summary>The five-bar preparing envelope, symmetric about the centre.</summary>
    public static readonly double[] PreparingBarMultipliers = [0.6, 0.85, 1.0, 0.85, 0.6];

    /// <summary>The four-bar recording envelope.</summary>
    public static readonly double[] RecordingBarMultipliers = [0.7, 1.0, 1.0, 0.7];

    /// <summary>Resting preparing heights: the envelope at its quiet baseline.</summary>
    public static readonly double[] PreparingBarHeights =
        [.. PreparingBarMultipliers.Select(m => WaveformMinHeight + m * 6)];

    /// <summary>Resting recording heights, used before the first microphone peak arrives.</summary>
    public static readonly double[] RecordingBarHeights =
        [.. RecordingBarMultipliers.Select(m => WaveformMinHeight + m * 5)];

    public static DipSize SizeFor(FloatingIndicatorState state, bool hovered, string? message = "")
    {
        return state switch
        {
            FloatingIndicatorState.Idle => hovered
                ? new DipSize(ExpandedIdleWidth, ExpandedIdleHeight, ExpandedIdleRadius)
                : new DipSize(CompactIdleWidth, CompactIdleHeight, CompactIdleRadius),
            FloatingIndicatorState.Preparing =>
                new DipSize(PreparingWidth, PreparingHeight, PreparingRadius),
            FloatingIndicatorState.Recording =>
                new DipSize(LiveWidth, LiveHeight, LiveRadius),
            FloatingIndicatorState.Transcribing =>
                new DipSize(TranscribingWidth, TranscribingHeight, TranscribingRadius),
            FloatingIndicatorState.Success or FloatingIndicatorState.Error =>
                new DipSize(StatusWidthFor(message), StatusHeight, StatusRadius),
            _ => new DipSize(CompactIdleWidth, CompactIdleHeight, CompactIdleRadius)
        };
    }

    /// <summary>
    /// The width of a status pill: compact by default, widening for long status text.
    /// </summary>
    public static double StatusWidthFor(string? message) =>
        (message?.Length ?? 0) > LongStatusMessageChars ? StatusWidthLong : StatusWidth;

    /// <summary>How long a status pill stays visible before returning to idle.</summary>
    public static long ReturnMilliseconds(FloatingIndicatorState state) =>
        state == FloatingIndicatorState.Error ? ErrorReturnMs : SuccessReturnMs;

    /// <summary>
    /// Decides which dictation action a click inside the pill at <paramref name="xDip"/> should
    /// trigger for a given state. The leftmost region cancels; the rest of a live pill stops and
    /// transcribes; a transcribing pill's body also cancels.
    /// </summary>
    public static FloatingIndicatorAction ActionForClick(
        FloatingIndicatorState state,
        double xDip,
        double cancelRegion = CancelRegionWidthDip) =>
        state switch
        {
            FloatingIndicatorState.Preparing or FloatingIndicatorState.Recording =>
                xDip < cancelRegion ? FloatingIndicatorAction.Cancel : FloatingIndicatorAction.Stop,
            FloatingIndicatorState.Transcribing => FloatingIndicatorAction.Cancel,
            _ => FloatingIndicatorAction.None
        };

    /// <summary>
    /// Top-left position for an anchored indicator, following the eight supported anchors inside
    /// <paramref name="workArea"/>. The default anchor is "Middle Right".
    /// </summary>
    public static DipPoint AnchorTopLeft(string anchor, DipSize size, DipRect workArea)
    {
        var anchorName = string.IsNullOrWhiteSpace(anchor) ? "Middle Right" : anchor;
        var left = anchorName switch
        {
            "Top Left" or "Bottom Left" or "Middle Left" => workArea.Left + AnchorEdgeMargin,
            "Top Right" or "Bottom Right" or "Middle Right" => workArea.Right - size.Width - AnchorEdgeMargin,
            "Top Center" or "Bottom Center" => workArea.Left + workArea.Width / 2 - size.Width / 2,
            _ => workArea.Right - size.Width - AnchorEdgeMargin
        };
        var top = anchorName switch
        {
            "Bottom Left" or "Bottom Center" or "Bottom Right" => workArea.Bottom - size.Height - AnchorEdgeMargin,
            "Middle Left" or "Middle Right" => workArea.Top + (workArea.Height - size.Height) / 2,
            _ => workArea.Top + AnchorEdgeMargin
        };
        return new DipPoint(left, top);
    }

    /// <summary>
    /// Clamps a requested top-left corner so the whole pill stays inside <paramref name="workArea"/>
    /// with at least <paramref name="margin"/> DIPs to every edge. If the pill is larger than the
    /// work area it is centred on the offending axis.
    /// </summary>
    public static DipPoint Clamp(DipPoint requested, DipSize size, DipRect workArea, double margin = ScreenEdgeMargin)
    {
        var minimumLeft = workArea.Left + margin;
        var maximumLeft = workArea.Right - size.Width - margin;
        var minimumTop = workArea.Top + margin;
        var maximumTop = workArea.Bottom - size.Height - margin;
        var left = maximumLeft >= minimumLeft
            ? Math.Clamp(requested.X, minimumLeft, maximumLeft)
            : workArea.Left + Math.Max(0, (workArea.Width - size.Width) / 2);
        var top = maximumTop >= minimumTop
            ? Math.Clamp(requested.Y, minimumTop, maximumTop)
            : workArea.Top + Math.Max(0, (workArea.Height - size.Height) / 2);
        return new DipPoint(left, top);
    }

    /// <summary>
    /// Moves a window from its original top-left by a pointer delta measured in physical screen
    /// pixels. The delta is converted to DIPs with the active monitor's <paramref name="scale"/>,
    /// then clamped to <paramref name="workArea"/>.
    /// </summary>
    /// <remarks>
    /// Dragging must be driven by absolute screen-space pointer deltas, never by coordinates
    /// relative to the moving window: the moving coordinate system feeds the window's own movement
    /// back into the next delta and the gesture oscillates.
    /// </remarks>
    public static DipPoint DragTopLeft(
        DipPoint originalTopLeft,
        double deltaXPixels,
        double deltaYPixels,
        double scale,
        DipSize size,
        DipRect workArea,
        double margin = ScreenEdgeMargin)
    {
        var scaleFactor = scale > 0 ? scale : 1.0;
        var requested = new DipPoint(
            originalTopLeft.X + deltaXPixels / scaleFactor,
            originalTopLeft.Y + deltaYPixels / scaleFactor);
        return Clamp(requested, size, workArea, margin);
    }

    /// <summary>
    /// Clamps a saved custom centre so the pill stays inside the work area, producing the repaired
    /// centre. Returns <c>null</c> when the position was already valid (unchanged).
    /// </summary>
    public static DipPoint? RepairSavedCenter(DipPoint savedCenter, DipSize size, DipRect workArea, double margin = ScreenEdgeMargin)
    {
        var clampedTopLeft = Clamp(
            new DipPoint(savedCenter.X - size.Width / 2, savedCenter.Y - size.Height / 2),
            size,
            workArea,
            margin);
        var repairedCenter = new DipPoint(clampedTopLeft.X + size.Width / 2, clampedTopLeft.Y + size.Height / 2);
        return Different(repairedCenter, savedCenter) ? repairedCenter : null;
    }

    /// <summary>
    /// Converts a DIP length to physical pixels at the supplied scale factor. Used to map the
    /// DIP-based sizes onto <c>AppWindow.Resize</c>/<c>Move</c>, which take physical pixels.
    /// </summary>
    public static int ToPixels(double dip, double scale) => checked((int)Math.Round(dip * scale));

    /// <summary>Converts a physical pixel length to DIPs at the supplied scale factor.</summary>
    public static double ToDips(double pixel, double scale) => pixel / scale;

    /// <summary>A safe scale factor from a monitor DPI value (96 = 100%).</summary>
    public static double DpiScaleFor(uint dpi) => Math.Max(1d, dpi / 96d);

    /// <summary>
    /// The height of one of the four live recording bars for a raw microphone peak. This is the
    /// WPF reference's <c>UpdateRecordingLevel</c>: a wide-topped (square-root) response, small
    /// baseline so silence still reads, and per-bar weights so the centre bars grow taller.
    /// </summary>
    public static double RecordingBarHeight(float peak, int index)
    {
        if (index < 0 || index >= RecordingBarMultipliers.Length)
        {
            return RecordingBarHeights[index >= 0 ? index % RecordingBarHeights.Length : 0];
        }

        // Wide-topped (square-root) response so quiet speech still moves the bars, held between
        // the design system's 5-DIP minimum and the Recording pill's 14-DIP ceiling and shaped by
        // the symmetric four-bar envelope.
        var normalized = Math.Clamp(Math.Sqrt(Math.Max(0, peak) * 8), 0.12, 1.0);
        var span = (WaveformMaxHeightFor(FloatingIndicatorState.Recording) - WaveformMinHeight) * RecordingBarMultipliers[index];
        return WaveformMinHeight + normalized * span;
    }

    private static bool Different(DipPoint first, DipPoint second) =>
        Math.Abs(first.X - second.X) > 0.5 || Math.Abs(first.Y - second.Y) > 0.5;
}

public enum FloatingIndicatorAction
{
    None,
    Cancel,
    Stop
}
