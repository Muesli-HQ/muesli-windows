using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Covers the two parts of the floating indicator that the pure layout tests do not reach: the
/// presentation probe that lets UI Automation render the hardware-driven states, and the custom
/// drag position's persistence and restoration across size, DPI, and monitor changes.
/// </summary>
public sealed class FloatingIndicatorProbeAndPersistenceTests
{
    // ─── Presentation probe ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_is_disabled_unless_the_flag_is_present()
    {
        Assert.False(FloatingIndicatorProbe.IsEnabled(null));
        Assert.False(FloatingIndicatorProbe.IsEnabled([]));
        Assert.False(FloatingIndicatorProbe.IsEnabled(["Muesli.exe", "--profile-root", @"C:\temp\p"]));
    }

    [Fact]
    public void Probe_is_enabled_by_the_flag_in_any_position_or_casing()
    {
        Assert.True(FloatingIndicatorProbe.IsEnabled(["Muesli.exe", "--indicator-probe"]));
        Assert.True(FloatingIndicatorProbe.IsEnabled(["--indicator-probe", "--profile-root", @"C:\temp\p"]));
        Assert.True(FloatingIndicatorProbe.IsEnabled(["Muesli.exe", "  --Indicator-Probe  "]));
    }

    [Theory]
    [InlineData("idle", FloatingIndicatorState.Idle)]
    [InlineData("preparing", FloatingIndicatorState.Preparing)]
    [InlineData("recording", FloatingIndicatorState.Recording)]
    [InlineData("transcribing", FloatingIndicatorState.Transcribing)]
    [InlineData("success", FloatingIndicatorState.Success)]
    [InlineData("error", FloatingIndicatorState.Error)]
    [InlineData("  Recording\r\n", FloatingIndicatorState.Recording)]
    public void Probe_parses_every_state_name(string text, FloatingIndicatorState expected) =>
        Assert.Equal(expected, FloatingIndicatorProbe.ParseState(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("listening")]
    [InlineData("half-written")]
    public void Probe_ignores_unrecognised_or_partial_text(string? text) =>
        Assert.Null(FloatingIndicatorProbe.ParseState(text));

    [Fact]
    public void Probe_status_text_classifies_back_to_the_state_it_names()
    {
        // The probe drives presentation by publishing the same status the real pipeline would, so
        // the shipping classifier must map it back to the requested state.
        foreach (var state in new[]
                 {
                     FloatingIndicatorState.Preparing,
                     FloatingIndicatorState.Success,
                     FloatingIndicatorState.Error
                 })
        {
            var status = FloatingIndicatorProbe.StatusFor(state);
            Assert.Equal(state, FloatingIndicatorStateClassifier.Classify(status, false, false));
        }

        Assert.Equal(
            FloatingIndicatorState.Recording,
            FloatingIndicatorStateClassifier.Classify(
                FloatingIndicatorProbe.StatusFor(FloatingIndicatorState.Recording), true, false));
        Assert.Equal(
            FloatingIndicatorState.Transcribing,
            FloatingIndicatorStateClassifier.Classify(
                FloatingIndicatorProbe.StatusFor(FloatingIndicatorState.Transcribing), false, true));
    }

    [Fact]
    public void Probe_outcome_messages_identify_themselves_as_a_probe()
    {
        // A probe screenshot must never be mistakable for a real dictation outcome.
        Assert.Contains("Probe", FloatingIndicatorProbe.SuccessMessage, StringComparison.Ordinal);
        Assert.Contains("Probe", FloatingIndicatorProbe.ErrorMessage, StringComparison.Ordinal);
    }

    // ─── An expired outcome pill must not re-show itself ─────────────────────────────────────

    [Fact]
    public void An_outcome_that_has_had_its_dwell_is_suppressed_while_the_status_stays_put()
    {
        // Observed live: a failed dictation left "Dictation failed: Access is denied" in Status,
        // the dwell timer fired, UpdateState re-classified the same text as Error, and the pill
        // re-armed itself indefinitely instead of returning to idle.
        const string failure = "Dictation failed: Access is denied. (0x80070005)";
        var state = FloatingIndicatorStateClassifier.Classify(failure, false, false);
        Assert.Equal(FloatingIndicatorState.Error, state);

        Assert.False(FloatingIndicatorStateClassifier.IsAlreadyDismissed(state, failure, null));
        Assert.True(FloatingIndicatorStateClassifier.IsAlreadyDismissed(state, failure, failure));
    }

    [Fact]
    public void A_different_outcome_still_shows_after_an_earlier_one_was_dismissed()
    {
        var dismissed = "Dictation failed: Access is denied. (0x80070005)";
        var next = FloatingIndicatorStateClassifier.Classify("Dictation inserted", false, false);

        Assert.Equal(FloatingIndicatorState.Success, next);
        Assert.False(FloatingIndicatorStateClassifier.IsAlreadyDismissed(next, "Dictation inserted", dismissed));
    }

    [Theory]
    [InlineData(FloatingIndicatorState.Idle)]
    [InlineData(FloatingIndicatorState.Preparing)]
    [InlineData(FloatingIndicatorState.Recording)]
    [InlineData(FloatingIndicatorState.Transcribing)]
    public void Only_outcome_states_are_ever_suppressed(FloatingIndicatorState state) =>
        Assert.False(FloatingIndicatorStateClassifier.IsAlreadyDismissed(state, "same", "same"));

    // ─── Custom position persistence ─────────────────────────────────────────────────────────

    [Fact]
    public void Dragged_position_round_trips_through_settings_as_a_custom_anchor()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());

        var dragged = store.Load() with
        {
            IndicatorAnchor = "Custom",
            IndicatorLeft = 812.5,
            IndicatorTop = 233.25
        };
        store.Save(dragged);

        var reloaded = new SettingsStore(path, new InMemorySecretStore()).Load();
        Assert.Equal("Custom", reloaded.IndicatorAnchor);
        Assert.Equal(812.5, reloaded.IndicatorLeft);
        Assert.Equal(233.25, reloaded.IndicatorTop);
    }

    [Fact]
    public void A_profile_with_no_saved_position_falls_back_to_the_default_anchor()
    {
        using var directory = new TestDirectory();
        var settings = new SettingsStore(directory.File("settings.json"), new InMemorySecretStore()).Load();

        Assert.Null(settings.IndicatorLeft);
        Assert.Null(settings.IndicatorTop);
        Assert.Equal("Middle Right", settings.IndicatorAnchor);
    }

    // ─── The pill stays put when its size changes ────────────────────────────────────────────

    [Fact]
    public void A_saved_centre_keeps_the_pill_stationary_as_the_state_resizes_it()
    {
        var area = new DipRect(0, 0, 1920, 1040);
        var centre = new DipPoint(900, 500);

        // Idle compact → idle hovered → recording → status: the centre must not drift.
        foreach (var size in new[]
                 {
                     FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Idle, false),
                     FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Idle, true),
                     FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Recording, false),
                     FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Success, false, "Dictation inserted")
                 })
        {
            var topLeft = FloatingIndicatorLayout.Clamp(
                new DipPoint(centre.X - size.Width / 2, centre.Y - size.Height / 2), size, area);
            Assert.Equal(centre.X, topLeft.X + size.Width / 2, 3);
            Assert.Equal(centre.Y, topLeft.Y + size.Height / 2, 3);
        }
    }

    [Fact]
    public void A_centre_saved_on_a_wider_monitor_is_repaired_onto_the_current_one()
    {
        // The monitor the position was saved on is gone; the saved centre now sits off-screen.
        var nowAvailable = new DipRect(0, 0, 1280, 800);
        var size = FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Idle, false);

        var repaired = FloatingIndicatorLayout.RepairSavedCenter(new DipPoint(3400, 1400), size, nowAvailable);

        Assert.NotNull(repaired);
        var topLeft = new DipPoint(repaired!.Value.X - size.Width / 2, repaired.Value.Y - size.Height / 2);
        Assert.True(topLeft.X >= nowAvailable.Left + FloatingIndicatorLayout.ScreenEdgeMargin);
        Assert.True(topLeft.Y >= nowAvailable.Top + FloatingIndicatorLayout.ScreenEdgeMargin);
        Assert.True(topLeft.X + size.Width <= nowAvailable.Right - FloatingIndicatorLayout.ScreenEdgeMargin);
        Assert.True(topLeft.Y + size.Height <= nowAvailable.Bottom - FloatingIndicatorLayout.ScreenEdgeMargin);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void A_saved_centre_survives_a_dpi_change_without_drifting(uint dpi)
    {
        // The saved centre is stored in DIPs, so the same logical position must map to the
        // equivalent physical pixels at every scale the indicator supports.
        var scale = FloatingIndicatorLayout.DpiScaleFor(dpi);
        var area = new DipRect(0, 0, 1920, 1040);
        var size = FloatingIndicatorLayout.SizeFor(FloatingIndicatorState.Idle, true);
        var centre = new DipPoint(960, 520);

        var topLeft = FloatingIndicatorLayout.Clamp(
            new DipPoint(centre.X - size.Width / 2, centre.Y - size.Height / 2), size, area);

        var pixelLeft = FloatingIndicatorLayout.ToPixels(topLeft.X, scale);
        var pixelWidth = FloatingIndicatorLayout.ToPixels(size.Width, scale);
        var recoveredCentre = FloatingIndicatorLayout.ToDips(pixelLeft + pixelWidth / 2.0, scale);

        // Windows positions windows on whole pixels, so a round trip through physical pixels can
        // legitimately lose up to half a pixel on each of the origin and the width. The property
        // that matters is that the drift stays sub-pixel and never accumulates into visible motion.
        var toleranceDip = 1.0 / scale;
        Assert.True(
            Math.Abs(centre.X - recoveredCentre) <= toleranceDip,
            $"At {dpi} DPI the centre drifted {Math.Abs(centre.X - recoveredCentre):F3} DIP, " +
            $"which exceeds the {toleranceDip:F3} DIP sub-pixel budget.");
    }
}
