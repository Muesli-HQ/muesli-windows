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
    /// Preparing shares the 76x22 live pill, per the current macOS <c>frameForState</c>, so the
    /// waiting waveform hands off to the live one without a resize.
    /// </summary>
    public const double PreparingWidth = 76;
    public const double PreparingHeight = 22;
    public const double PreparingRadius = 11;

    public const double LiveWidth = 76;
    public const double LiveHeight = 22;
    public const double LiveRadius = 11;

    /// <summary>
    /// Window-level opacity, per macOS <c>styleForState</c>. Only the compact idle pill is
    /// translucent as a whole (0.90); every other state is fully opaque at the window level and
    /// gets its translucency from the fill's own alpha.
    /// </summary>
    public static double WindowAlpha(FloatingIndicatorState state, bool hovered) =>
        state == FloatingIndicatorState.Idle && !hovered ? 0.90 : 1.0;

    /// <summary>The macOS glass tint, Catppuccin Mocha base, used by every non-recording state.</summary>
    public const uint GlassTintRgb = 0x1E1E2E;

    /// <summary>
    /// macOS lays the tint over a dark <c>.hudWindow</c> blur, which greys out whatever is behind
    /// the pill first. The WPF pill has no blur, so without a stand-in the bluish tint sits
    /// directly over the desktop and reads light blue over bright windows. This neutral layer
    /// replaces the blur's darkening.
    /// </summary>
    public const uint GlassBaseRgb = 0x1C1C1E;
    public const double GlassBaseAlpha = 0.5; // ponytail: hand-tuned stand-in for the HUD blur; tune by eye

    /// <summary>
    /// The pill fill as one ARGB colour: <paramref name="rgb"/> at <paramref name="alpha"/>
    /// composited over the neutral glass base (source-over).
    /// </summary>
    public static (byte A, byte R, byte G, byte B) GlassFill(uint rgb, double alpha)
    {
        var top = Math.Clamp(alpha, 0, 1);
        var outAlpha = top + GlassBaseAlpha * (1 - top);
        byte Channel(int shift)
        {
            var tint = (rgb >> shift) & 0xFF;
            var baseValue = (GlassBaseRgb >> shift) & 0xFF;
            return (byte)Math.Round((tint * top + baseValue * GlassBaseAlpha * (1 - top)) / outAlpha);
        }
        return ((byte)Math.Round(outAlpha * 255), Channel(16), Channel(8), Channel(0));
    }

    /// <summary>
    /// The transcribing icon, an 18x18 vector stand-in for the SF Symbol <c>wand.and.sparkles</c>
    /// the macOS pill shows (Windows ships no equivalent glyph): a diagonal wand with a tip and
    /// three four-point sparkles.
    /// </summary>
    public const string TranscribingIconPathData =
        "M2.2,14.4 L3.6,15.8 L11.6,7.8 L10.2,6.4 Z M10.9,5.7 L12.3,7.1 L13.3,6.1 L11.9,4.7 Z " +
        "M14.6,0.6 Q14.6,3.2 17.2,3.2 Q14.6,3.2 14.6,5.8 Q14.6,3.2 12,3.2 Q14.6,3.2 14.6,0.6 Z " +
        "M15.6,8.9 Q15.6,10.6 17.3,10.6 Q15.6,10.6 15.6,12.3 Q15.6,10.6 13.9,10.6 Q15.6,10.6 15.6,8.9 Z " +
        "M7.4,1.5 Q7.4,3 8.9,3 Q7.4,3 7.4,4.5 Q7.4,3 5.9,3 Q7.4,3 7.4,1.5 Z";

    /// <summary>
    /// The icon's repeating wiggle, after macOS's <c>.wiggle</c> symbol effect: a short
    /// rotation shake (degrees at each key time, seconds) and then a rest until the cycle ends.
    /// </summary>
    public static readonly (double Seconds, double Degrees)[] TranscribingWiggleKeys =
        [(0, 0), (0.12, -12), (0.26, 10), (0.4, -6), (0.52, 0), (1.6, 0)];

    /// <summary>Transcribing: macOS <c>transcribingPillSize</c>'s 190-point minimum at 32 tall.</summary>
    public const double TranscribingWidth = 190;
    public const double TranscribingHeight = 32;
    public const double TranscribingRadius = 16;

    public const double StatusWidth = 180;
    public const double StatusWidthMax = 420;
    public const double StatusHeight = 36;
    public const double StatusRadius = 18;

    public const double DragThreshold = 4;
    public const double ScreenEdgeMargin = 8;
    public const double AnchorEdgeMargin = 18;
    public const double CancelRegionWidthDip = 30;

    /// <summary>macOS shows success notices for 3s and dictation warnings for 3s.</summary>
    public const long SuccessReturnMs = 3000;
    public const long ErrorReturnMs = 3000;

    // ─── Waveform, per macOS IndicatorWaveformDynamics.standingBarFrame ──────────────────────

    /// <summary>Bar width and gap in DIPs.</summary>
    public const double WaveformBarWidth = 3;
    public const double WaveformBarSpacing = 3;

    /// <summary>Bar height is <c>3 + 11 x amplitude</c>: 3 DIPs silent, 14 DIPs at full level.</summary>
    public const double WaveformMinHeight = 3;
    public const double WaveformAmplitudeSpan = 11;

    /// <summary>Bar and stop-square fill opacity.</summary>
    public const double WaveformOpacity = 0.85;

    /// <summary>
    /// The Muesli brand mark: the 13 bar heights measured from the canonical 1024px app icon
    /// (macOS <c>assets/muesli.icns</c>), normalized to the tallest bar. Same profile as
    /// <c>scripts/generate-winui-assets.ps1</c>.
    /// </summary>
    public static readonly double[] BrandMarkBars =
        [0.302, 0.579, 0.852, 1.000, 0.899, 0.602, 0.302, 0.602, 0.899, 1.000, 0.852, 0.579, 0.302];

    /// <summary>
    /// Pixel-snapped brand-mark geometry, in DIPs, for a mark <paramref name="size"/> DIPs tall at
    /// display <paramref name="scale"/>. Like <c>scripts/generate-winui-assets.ps1</c>'s small icon
    /// frames, every bar and gap is a whole number of physical pixels (at least one), because at
    /// pill size fractional 13-bar gaps antialias away and the mark smears into a blob. Bar heights
    /// share the tallest bar's pixel parity so centring never puts a bar half a pixel off.
    /// </summary>
    public static (double BarWidth, double Gap, double[] Heights) BrandMarkGeometry(double size, double scale)
    {
        scale = scale > 0 ? scale : 1;
        var bar = Math.Max(1, Math.Round(size / 13 * scale));
        var gap = Math.Max(1, Math.Round(size / 30 * scale));
        var tallest = Math.Round(size * scale);
        var heights = BrandMarkBars
            .Select(m => Math.Max(bar, tallest - 2 * Math.Round((tallest - m * size * scale) / 2)) / scale)
            .ToArray();
        return (bar / scale, gap / scale, heights);
    }

    /// <summary>Stop square size in DIPs (6×6) and its corner radius (1 DIP).</summary>
    public const double StopSquareSize = 6;
    public const double StopSquareRadius = 1;

    /// <summary>
    /// Gap from the right edge of the recording pill to the stop square, matching the macOS
    /// reference's <c>addStopLayer</c> (<c>x = width - 6 - 8</c>).
    /// </summary>
    public const double StopSquareRightMargin = 8;

    /// <summary>
    /// The recording pill's left control. Dictation uses the heavy cross mark U+2715 at 7pt and
    /// 45% white; meetings swap in pause/play at 8pt and 86% white, per the macOS reference.
    /// </summary>
    public const string RecordingCancelGlyph = "\u2715";
    public const double RecordingCancelFontSize = 7;
    public const byte RecordingCancelAlpha = 0x73;
    public const double MeetingControlFontSize = 8;
    public const byte MeetingControlAlpha = 0xDB;

    // ─── Waveform animation, per macOS waveformTimerFired ─────────────────────────────────────

    /// <summary>Waveform refresh rate.</summary>
    public const int WaveformUpdateFramesPerSecond = 30;

    /// <summary>Live amplitude smoothing: exponential-average weight for each new sample.</summary>
    public const double WaveformAmplitudeSmoothingWeight = 0.48;

    /// <summary>The five-bar standing envelope, symmetric about the centre.</summary>
    public static readonly double[] WaveformBarMultipliers = [0.6, 0.85, 1.0, 0.85, 0.6];

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
        // macOS warningPillSize: 18 + 24 icon + 4 + text + 2 + 18, 180 minimum. ~6.2 DIPs per
        // character approximates 11pt medium Segoe UI.
        Math.Clamp(66 + (message?.Length ?? 0) * 6.2, StatusWidth, StatusWidthMax);

    /// <summary>How long a status pill stays visible before returning to idle.</summary>
    public static long ReturnMilliseconds(FloatingIndicatorState state) =>
        state == FloatingIndicatorState.Error ? ErrorReturnMs : SuccessReturnMs;

    /// <summary>
    /// Decides which dictation action a click inside the pill at <paramref name="xDip"/> should
    /// trigger for a given state. The leftmost region cancels; the rest of a live pill stops and
    /// transcribes. A transcribing pill ignores clicks, as on macOS (Esc and right-click still cancel).
    /// </summary>
    public static FloatingIndicatorAction ActionForClick(
        FloatingIndicatorState state,
        double xDip,
        double cancelRegion = CancelRegionWidthDip) =>
        state switch
        {
            // macOS handleClick acts only while recording; a preparing pill ignores clicks.
            FloatingIndicatorState.Recording =>
                xDip < cancelRegion ? FloatingIndicatorAction.Cancel : FloatingIndicatorAction.Stop,
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

    private static bool Different(DipPoint first, DipPoint second) =>
        Math.Abs(first.X - second.X) > 0.5 || Math.Abs(first.Y - second.Y) > 0.5;
}

public enum FloatingIndicatorAction
{
    None,
    Cancel,
    Stop
}
