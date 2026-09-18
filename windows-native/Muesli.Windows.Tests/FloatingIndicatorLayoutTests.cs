using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class FloatingIndicatorLayoutTests
{
    // ─── State → size mapping ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FloatingIndicatorState.Idle, false, 44, 28, 14)]
    [InlineData(FloatingIndicatorState.Idle, true, 220, 36, 18)]
    // Preparing is the compact pill in the macOS design system's state table, not the live one.
    [InlineData(FloatingIndicatorState.Preparing, false, 44, 28, 14)]
    [InlineData(FloatingIndicatorState.Recording, false, 76, 22, 11)]
    [InlineData(FloatingIndicatorState.Transcribing, false, 120, 32, 16)]
    [InlineData(FloatingIndicatorState.Success, false, 220, 36, 18)]
    [InlineData(FloatingIndicatorState.Error, false, 220, 36, 18)]
    public void SizeFor_maps_every_state(FloatingIndicatorState state, bool hovered, double width, double height, double radius)
    {
        var size = FloatingIndicatorLayout.SizeFor(state, hovered);
        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
        Assert.Equal(radius, size.CornerRadius);
    }

    [Theory]
    [InlineData("Some quite long status message that extends well past the threshold", true)]
    [InlineData("Saved", false)]
    public void SizeFor_widens_status_pill_for_long_messages(string message, bool expectedLong)
    {
        var size = FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Success, false, message);
        Assert.Equal(expectedLong ? 260 : 220, size.Width);

        var error = FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Error, false, message);
        Assert.Equal(expectedLong ? 260 : 220, error.Width);
    }

    // ─── State transitions ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_prioritises_live_flags_over_status_text()
    {
        Assert.Equal(
            FloatingIndicatorState.Recording,
            FloatingIndicatorStateClassifier.Classify("Listening", true, false));
        Assert.Equal(
            FloatingIndicatorState.Transcribing,
            FloatingIndicatorStateClassifier.Classify("Transcribing locally…", false, true));
    }

    [Fact]
    public void Classify_marks_outcome_states()
    {
        Assert.Equal(
            FloatingIndicatorState.Error,
            FloatingIndicatorStateClassifier.Classify("Dictation failed: boom", false, false));
        Assert.Equal(
            FloatingIndicatorState.Success,
            FloatingIndicatorStateClassifier.Classify("Dictation saved", false, false));
        // A failed paste is still a failure even though the transcript was saved and copied.
        Assert.Equal(
            FloatingIndicatorState.Error,
            FloatingIndicatorStateClassifier.Classify("Paste failed; transcript saved and copied", false, false));
    }

    [Fact]
    public void Classify_marks_preparation_states()
    {
        Assert.Equal(
            FloatingIndicatorState.Preparing,
            FloatingIndicatorStateClassifier.Classify("Preparing microphone…", false, false));
        Assert.Equal(
            FloatingIndicatorState.Preparing,
            FloatingIndicatorStateClassifier.Classify("Hold to dictate", false, false));
        Assert.Equal(
            FloatingIndicatorState.Idle,
            FloatingIndicatorStateClassifier.Classify("Ready", false, false));
    }

    [Fact]
    public void Classify_returns_idle_for_user_cancellation()
    {
        Assert.Equal(
            FloatingIndicatorState.Idle,
            FloatingIndicatorStateClassifier.Classify("Dictation cancelled", false, false));
    }

    [Fact]
    public void Classify_treats_recording_flag_as_authoritative()
    {
        Assert.Equal(
            FloatingIndicatorState.Recording,
            FloatingIndicatorStateClassifier.Classify("Dictation saved", true, false));
    }

    // ─── Action routing ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FloatingIndicatorState.Preparing, 10, FloatingIndicatorAction.Cancel)]
    [InlineData(FloatingIndicatorState.Recording, 10, FloatingIndicatorAction.Cancel)]
    [InlineData(FloatingIndicatorState.Recording, 40, FloatingIndicatorAction.Stop)]
    [InlineData(FloatingIndicatorState.Transcribing, 60, FloatingIndicatorAction.Cancel)]
    [InlineData(FloatingIndicatorState.Idle, 60, FloatingIndicatorAction.None)]
    [InlineData(FloatingIndicatorState.Success, 60, FloatingIndicatorAction.None)]
    public void ActionForClick_routes_by_state_and_x(FloatingIndicatorState state, double x, FloatingIndicatorAction expected)
    {
        Assert.Equal(expected, FloatingIndicatorLayout.ActionForClick(state, x));
    }

    // ─── Anchor placement ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Top Left", 18, 18)]
    [InlineData("Top Center", 850, 18)]
    [InlineData("Top Right", 1682, 18)]
    [InlineData("Middle Left", 18, 502)]
    [InlineData("Middle Right", 1682, 502)]
    [InlineData("Bottom Left", 18, 986)]
    [InlineData("Bottom Center", 850, 986)]
    [InlineData("Bottom Right", 1682, 986)]
    public void AnchorTopLeft_places_every_anchor(string anchor, double expectedX, double expectedY)
    {
        var area = new DipRect(0, 0, 1920, 1040);
        var size = new DipSize(220, 36, 18);
        var point = FloatingIndicatorLayout.AnchorTopLeft(anchor, size, area);
        Assert.Equal(expectedX, point.X, 3);
        Assert.Equal(expectedY, point.Y, 3);
    }

    [Fact]
    public void AnchorTopLeft_defaults_to_middle_right()
    {
        var area = new DipRect(0, 0, 1920, 1040);
        var size = new DipSize(220, 36, 18);
        var point = FloatingIndicatorLayout.AnchorTopLeft("", size, area);
        Assert.Equal(1682, point.X, 3);
        Assert.Equal(502, point.Y, 3);
    }

    // ─── Clamping ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_keeps_indicator_inside_work_area()
    {
        var code = new DipRect(0, 0, 1920, 1040);
        var size = new DipSize(220, 36, 18);
        var clamped = FloatingIndicatorLayout.Clamp(new DipPoint(1900, 1030), size, code);
        Assert.Equal(1692, clamped.X);
        Assert.Equal(996, clamped.Y);
    }

    [Fact]
    public void Clamp_supports_negative_origin_monitors()
    {
        var area = new DipRect(-2560, -320, 2560, 1400);
        var size = new DipSize(260, 36, 18);
        var clamped = FloatingIndicatorLayout.Clamp(new DipPoint(-5000, -800), size, area);
        Assert.Equal(-2552, clamped.X);
        Assert.Equal(-312, clamped.Y);
        Assert.True(clamped.X + size.Width <= area.Right);
        Assert.True(clamped.Y + size.Height <= area.Bottom);
    }

    [Fact]
    public void Clamp_repairs_position_after_indicator_expands()
    {
        var area = new DipRect(0, 0, 1280, 720);
        var expanded = new DipSize(260, 36, 18);
        var clamped = FloatingIndicatorLayout.Clamp(new DipPoint(1258, 700), expanded, area);
        Assert.Equal(1012, clamped.X);
        Assert.Equal(676, clamped.Y);
    }

    // ─── Custom-position persistence ─────────────────────────────────────────────────────────

    [Fact]
    public void RepairSavedCenter_returns_null_when_position_is_valid()
    {
        var area = new DipRect(0, 0, 1920, 1040);
        var size = new DipSize(44, 28, 14);
        var center = new DipPoint(500, 500);
        Assert.Null(FloatingIndicatorLayout.RepairSavedCenter(center, size, area));
    }

    [Fact]
    public void RepairSavedCenter_clamps_an_out_of_bounds_center()
    {
        var area = new DipRect(0, 0, 1920, 1040);
        var size = new DipSize(220, 36, 18);
        var clamped = FloatingIndicatorLayout.RepairSavedCenter(new DipPoint(1900, 1020), size, area);
        Assert.NotNull(clamped);
        Assert.Equal(1692 + 110, clamped!.Value.X);
        Assert.Equal(996 + 18, clamped.Value.Y);
    }

    // ─── DPI conversion ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(192, 2.0)]
    public void DpiScaleFor_computes_scale_factor(uint dpi, double expected) =>
        Assert.Equal(expected, FloatingIndicatorLayout.DpiScaleFor(dpi));

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(96, 1.0)]
    [InlineData(50, 1.0)]
    public void DpiScaleFor_never_goes_below_one(uint dpi, double expected) =>
        Assert.Equal(expected, FloatingIndicatorLayout.DpiScaleFor(dpi));

    [Fact]
    public void ToPixels_and_ToDips_are_inverse()
    {
        const double dip = 36;
        const double scale = 1.5;
        var pixels = FloatingIndicatorLayout.ToPixels(dip, scale);
        Assert.Equal(54, pixels);
        Assert.Equal(dip, FloatingIndicatorLayout.ToDips(pixels, scale), 3);
    }

    // ─── Live waveform math ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RecordingBarHeight_rises_with_peak_and_stays_in_bounds()
    {
        // Bounds come from the macOS design system's waveform specification (min 5pt, max 26pt),
        // not from the retired WPF port's narrower 3–12pt range.
        var baseline = FloatingIndicatorLayout.RecordingBarHeight(0, 1);
        var loud = FloatingIndicatorLayout.RecordingBarHeight(1, 1);
        Assert.True(loud > baseline);
        Assert.InRange(
            loud,
            FloatingIndicatorLayout.WaveformMinHeight,
            FloatingIndicatorLayout.WaveformMaxHeight);
        Assert.InRange(
            baseline,
            FloatingIndicatorLayout.WaveformMinHeight,
            FloatingIndicatorLayout.WaveformMaxHeight);

        // The envelope is symmetric: the inner bars reach higher than the outer ones.
        Assert.True(FloatingIndicatorLayout.RecordingBarHeight(1, 1) >
                    FloatingIndicatorLayout.RecordingBarHeight(1, 0));
        Assert.Equal(
            FloatingIndicatorLayout.RecordingBarHeight(1, 0),
            FloatingIndicatorLayout.RecordingBarHeight(1, 3),
            3);
    }

    [Fact]
    public void RecordingBarHeight_clamps_negative_peak_to_quiet()
    {
        // Negative peaks (rare but possible from float noise) must not produce a grotesque height.
        var height = FloatingIndicatorLayout.RecordingBarHeight(-1, 0);
        Assert.InRange(height, 3, 12);
    }

    // ─── Status return durations ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReturnMilliseconds_uses_error_duration_for_error_and_success_otherwise()
    {
        Assert.Equal(FloatingIndicatorLayout.ErrorReturnMs, FloatingIndicatorLayout.ReturnMilliseconds(FloatingIndicatorState.Error));
        Assert.Equal(FloatingIndicatorLayout.SuccessReturnMs, FloatingIndicatorLayout.ReturnMilliseconds(FloatingIndicatorState.Success));
        Assert.Equal(FloatingIndicatorLayout.SuccessReturnMs, FloatingIndicatorLayout.ReturnMilliseconds(FloatingIndicatorState.Idle));
    }
}
