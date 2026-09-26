using System;
using System.IO;
using System.Linq;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Framework-neutral tests for the meeting-notification panel: layout geometry, action mapping,
/// dedup/suppression, countdown pause/resume, and the responsive scan gate. Live window behaviour is
/// covered by the gated UI tests.
/// </summary>
public sealed class MeetingNotificationLayoutTests
{
    private static readonly DipRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Single_action_card_widens_for_long_text_and_stays_within_bounds()
    {
        var textX = MeetingNotificationLayout.TextX(hasPlatformIcon: true);
        var shortCard = MeetingNotificationLayout.SingleActionCardWidth(40, textX);
        var longCard = MeetingNotificationLayout.SingleActionCardWidth(200, textX);

        Assert.Equal(MeetingNotificationLayout.MinCardWidth, shortCard, 3);
        Assert.True(longCard > shortCard);
        Assert.True(longCard <= MeetingNotificationLayout.MaxSingleActionCardWidth);
        Assert.True(
            MeetingNotificationLayout.SingleActionTextWidth(longCard, textX) >= 200 - 0.5,
            "the widened card must actually expose the required text width");
        // Text beyond the maximum card width is clamped, never allowed to push the card past 420.
        Assert.Equal(
            MeetingNotificationLayout.MaxSingleActionCardWidth,
            MeetingNotificationLayout.SingleActionCardWidth(1000, textX), 3);
    }

    [Fact]
    public void Split_action_cards_are_the_minimum_width()
    {
        Assert.Equal(MeetingNotificationLayout.MinCardWidth, MeetingNotificationLayout.SplitActionCardWidth(), 3);
        Assert.True(MeetingNotificationLayout.SplitButtonX(MeetingNotificationLayout.MinCardWidth) > 0);
    }

    [Fact]
    public void Every_join_action_label_fits_its_split_segment()
    {
        foreach (var action in new[]
                 {
                     MeetingJoinDefaultAction.JoinAndRecord,
                     MeetingJoinDefaultAction.JoinOnly,
                     MeetingJoinDefaultAction.TranscribeOnly
                 })
        {
            var label = action.Label();
            var measured = MeetingNotificationTextMetrics.EstimateWidthDip(label, 11);
            Assert.True(
                MeetingNotificationLayout.SplitButtonLabelFits(label, measured),
                $"'{label}' (~{measured:F1} DIP) does not fit the {MeetingNotificationLayout.SplitButtonWidth}-DIP split button");
        }
    }

    [Fact]
    public void Start_transcribing_fits_the_single_action_button()
    {
        const string label = "Start Transcribing";
        Assert.True(MeetingNotificationTextMetrics.EstimateWidthDip(label, 12) <= MeetingNotificationLayout.SplitButtonWidth);
    }

    [Theory]
    [InlineData(100, 1280, 720)]
    [InlineData(125, 1024, 768)]
    [InlineData(150, 1536, 864)]
    [InlineData(200, 1920, 1040)]
    [InlineData(100, 3840, 2160)]
    public void Placement_is_sixteen_dips_from_the_top_and_right_edges_at_every_scale(
        int scalePercent, int workWidth, int workHeight)
    {
        // Placement is computed in DIPs; the scale only affects the physical conversion, so the same
        // DIP work area must always yield a panel 16 DIP from the top and right.
        var area = new DipRect(0, 0, workWidth, workHeight);
        var panelWidth = MeetingNotificationLayout.PanelWidth(MeetingNotificationLayout.MinCardWidth);
        var panelHeight = MeetingNotificationLayout.PanelHeight;
        var topLeft = MeetingNotificationLayout.PlaceTopRight(area, panelWidth, panelHeight);

        Assert.Equal(area.Right - panelWidth - MeetingNotificationLayout.ScreenMargin, topLeft.X, 3);
        Assert.Equal(area.Top + MeetingNotificationLayout.ScreenMargin, topLeft.Y, 3);
        Assert.True(MeetingNotificationLayout.FitsWithin(area, topLeft, panelWidth, panelHeight));
        Assert.True(scalePercent is 100 or 125 or 150 or 200);
    }

    [Fact]
    public void Placement_handles_negative_monitor_coordinates()
    {
        var area = new DipRect(-1920, -200, 1920, 1080);
        var panelWidth = MeetingNotificationLayout.PanelWidth(MeetingNotificationLayout.MinCardWidth);
        var panelHeight = MeetingNotificationLayout.PanelHeight;
        var topLeft = MeetingNotificationLayout.PlaceTopRight(area, panelWidth, panelHeight);

        Assert.Equal(area.Right - panelWidth - MeetingNotificationLayout.ScreenMargin, topLeft.X, 3);
        Assert.True(MeetingNotificationLayout.FitsWithin(area, topLeft, panelWidth, panelHeight));
    }

    [Fact]
    public void Placement_clamps_a_narrow_work_area_so_the_panel_stays_visible()
    {
        var area = new DipRect(0, 0, 320, 120);
        var panelWidth = MeetingNotificationLayout.PanelWidth(MeetingNotificationLayout.MinCardWidth);
        var panelHeight = MeetingNotificationLayout.PanelHeight;
        var topLeft = MeetingNotificationLayout.PlaceTopRight(area, panelWidth, panelHeight);

        Assert.True(topLeft.X >= area.Left - 0.5);
        Assert.True(topLeft.Y + panelHeight <= area.Bottom + 0.5);
    }
}

public sealed class MeetingNotificationActionTests
{
    [Fact]
    public void Detection_request_is_transcribe_only_and_never_opens_a_url()
    {
        var meeting = new DetectedMeeting(
            "Google Meet", "Google Meet abc-defg-hij", "Meet - abc-defg-hij", "chrome",
            "https://meet.google.com/abc-defg-hij", "key", 1234, MeetingEvidenceStrength.Strong);
        var request = MeetingNotificationRequestFactory.FromDetectedMeeting(meeting);

        Assert.Equal("Meeting detected", request.Title);
        Assert.Equal(MeetingNotificationKind.ActiveDetected, request.Kind);
        Assert.Equal("Start Transcribing", request.ActionLabel);
        Assert.Null(request.MeetingUrl);
        Assert.False(request.HasJoinActions);
        Assert.False(request.HasSplitAction);
        Assert.Equal("Google Meet", request.Platform);
    }

    [Fact]
    public void Every_default_action_arms_itself_when_all_actions_are_available()
    {
        foreach (var action in new[]
                 {
                     MeetingJoinDefaultAction.JoinAndRecord,
                     MeetingJoinDefaultAction.JoinOnly,
                     MeetingJoinDefaultAction.TranscribeOnly
                 })
        {
            Assert.Equal(action, action.Resolved(hasJoinAndRecord: true, hasJoinOnly: true));
        }
    }

    [Fact]
    public void Without_a_join_link_every_action_falls_back_to_transcribe_only()
    {
        Assert.Equal(MeetingJoinDefaultAction.TranscribeOnly,
            MeetingJoinDefaultAction.JoinAndRecord.Resolved(false, false));
        Assert.Equal(MeetingJoinDefaultAction.TranscribeOnly,
            MeetingJoinDefaultAction.JoinOnly.Resolved(false, false));
        Assert.Empty(MeetingJoinDefaultAction.JoinAndRecord.AvailableAlternatives(false, false));
    }

    [Fact]
    public void The_chevron_offers_the_two_unarmed_actions()
    {
        Assert.Equal(
            new[] { MeetingJoinDefaultAction.JoinOnly, MeetingJoinDefaultAction.TranscribeOnly },
            MeetingJoinDefaultAction.JoinAndRecord.AvailableAlternatives(true, true).ToArray());
        Assert.Equal(
            new[] { MeetingJoinDefaultAction.JoinAndRecord, MeetingJoinDefaultAction.JoinOnly },
            MeetingJoinDefaultAction.TranscribeOnly.AvailableAlternatives(true, true).ToArray());
        Assert.Equal(
            new[] { MeetingJoinDefaultAction.JoinAndRecord, MeetingJoinDefaultAction.TranscribeOnly },
            MeetingJoinDefaultAction.JoinOnly.AvailableAlternatives(true, true).ToArray());
    }

    [Fact]
    public void Labels_say_transcribe_not_record()
    {
        Assert.Equal("Join & Transcribe", MeetingJoinDefaultAction.JoinAndRecord.Label());
        Assert.Equal("Join Only", MeetingJoinDefaultAction.JoinOnly.Label());
        Assert.Equal("Transcribe Only", MeetingJoinDefaultAction.TranscribeOnly.Label());
    }

    [Fact]
    public void Scheduled_preview_maps_to_the_split_action()
    {
        var scheduled = MeetingNotificationRequestFactory.Preview("scheduled").Single();
        Assert.Equal(MeetingNotificationKind.ScheduledUpcoming, scheduled.Kind);
        Assert.True(scheduled.HasSplitAction);
        Assert.True(scheduled.HasJoinAndRecord);
        Assert.False(string.IsNullOrWhiteSpace(scheduled.MeetingUrl));
        Assert.Equal(MeetingJoinDefaultAction.JoinAndRecord, scheduled.DefaultAction.Resolved(true, true));
    }
}

public sealed class MeetingNotificationSuppressionTests
{
    private static MeetingNotificationRequest Request(string id) => new(
        id, MeetingNotificationKind.ActiveDetected, "Meeting detected", "Zoom · Sync", "Zoom",
        string.Empty, "#2D8CFF", "ZM", "Start Transcribing", null);

    [Fact]
    public void The_same_prompt_cannot_be_shown_twice()
    {
        var state = new MeetingNotificationSuppressionState();
        var request = Request("episode-1");
        Assert.True(state.Evaluate(request, true, false, false).Show);
        state.MarkShown(request.PromptId);
        var second = state.Evaluate(request, true, false, false);
        Assert.False(second.Show);
        Assert.Equal(MeetingNotificationOutcome.SuppressedDuplicate, second.Outcome);
    }

    [Fact]
    public void Explicit_dismissal_suppresses_until_the_meeting_ends()
    {
        var state = new MeetingNotificationSuppressionState();
        var request = Request("episode-2");
        state.SuppressForUser(request.PromptId);
        Assert.False(state.Evaluate(request, true, false, false).Show);
        Assert.True(state.Forget(request.PromptId));
        Assert.True(state.Evaluate(request, true, false, false).Show);
    }

    [Fact]
    public void Auto_dismissal_suppresses_until_the_meeting_ends()
    {
        var state = new MeetingNotificationSuppressionState();
        var request = Request("episode-3");
        state.SuppressForAuto(request.PromptId);
        Assert.False(state.Evaluate(request, true, false, false).Show);
        Assert.True(state.Forget(request.PromptId));
        Assert.True(state.Evaluate(request, true, false, false).Show);
    }

    [Fact]
    public void Recording_busy_and_disabled_suppress_with_named_outcomes()
    {
        var state = new MeetingNotificationSuppressionState();
        var request = Request("episode-4");
        Assert.Equal(MeetingNotificationOutcome.RecordingAlreadyActive,
            state.Evaluate(request, true, true, false).Outcome);
        Assert.Equal(MeetingNotificationOutcome.SuppressedBusy,
            state.Evaluate(request, true, false, true).Outcome);
        Assert.Equal(MeetingNotificationOutcome.SuppressedDisabled,
            state.Evaluate(request, false, false, false).Outcome);
    }

    [Fact]
    public void Starting_recording_marks_the_episode_consumed()
    {
        var state = new MeetingNotificationSuppressionState();
        var request = Request("episode-5");
        state.MarkRecordingStarted(request.PromptId);
        Assert.Equal(MeetingNotificationOutcome.RecordingAlreadyActive,
            state.Evaluate(request, true, false, false).Outcome);
        Assert.True(state.Forget(request.PromptId));
    }
}

public sealed class MeetingNotificationCountdownTests
{
    [Fact]
    public void Hover_pauses_and_resumes_with_the_exact_remaining_duration()
    {
        var countdown = new MeetingNotificationCountdown();
        countdown.Start(15);
        Assert.False(countdown.Advance(TimeSpan.FromSeconds(4)));
        Assert.Equal(11, countdown.RemainingSeconds, 3);

        countdown.Pause();
        Assert.True(countdown.IsPaused);
        Assert.False(countdown.Advance(TimeSpan.FromSeconds(30)));
        Assert.Equal(11, countdown.RemainingSeconds, 3);

        countdown.Resume();
        Assert.False(countdown.IsPaused);
        countdown.Advance(TimeSpan.FromSeconds(11));
        Assert.True(countdown.IsComplete);
    }

    [Fact]
    public void The_progress_bar_drains_from_full_to_zero()
    {
        var countdown = new MeetingNotificationCountdown();
        countdown.Start(20);
        Assert.Equal(1, countdown.Progress, 3);
        countdown.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, countdown.Progress, 3);
    }

    [Fact]
    public void Auto_dismiss_callback_is_suppressed_when_paused_during_fade()
    {
        Assert.True(MeetingNotificationAutoDismissPolicy.FiresAutoDismissAfterFade(false));
        Assert.False(MeetingNotificationAutoDismissPolicy.FiresAutoDismissAfterFade(true));
        Assert.True(MeetingNotificationAutoDismissPolicy.SuppressesCloseCallbackDuringAutoDismiss(true));
        Assert.False(MeetingNotificationAutoDismissPolicy.SuppressesCloseCallbackDuringAutoDismiss(false));
    }
}

public sealed class MeetingScanGateTests
{
    [Fact]
    public void Overlapping_scans_are_skipped_not_queued()
    {
        var gate = new MeetingScanGate();
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.Equal(1, gate.SkippedCount);
        Assert.True(gate.IsScanning);

        gate.Exit();
        Assert.False(gate.IsScanning);
        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public void The_scan_budget_stops_once_elapsed_exceeds_the_limit()
    {
        Assert.False(MeetingScanBudget.ShouldStop(0, 5000));
        Assert.False(MeetingScanBudget.ShouldStop(5000, 5000));
        Assert.True(MeetingScanBudget.ShouldStop(5001, 5000));
    }
}

/// <summary>
/// Source contracts proving the correct wiring without a live desktop: no XamlRoot dependency, no
/// ContentDialog detection prompt, and the required AutomationIds/High Contrast handling exist.
/// </summary>
public sealed class MeetingNotificationWiringContractTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(TestRepositoryLayout.Combine(parts));

    [Fact]
    public void The_notification_is_independent_of_the_dashboard()
    {
        var window = Read("windows-native", "Muesli.Windows.WinUI", "MeetingNotificationWindow.xaml.cs");
        // The doc comment mentions XamlRoot (to say it is not used); the code must not reference it.
        Assert.DoesNotContain(".XamlRoot", window, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlRoot =", window, StringComparison.Ordinal);
        Assert.Contains("IsAlwaysOnTop = true", window, StringComparison.Ordinal);
        Assert.Contains("SetBorderAndTitleBar(false, false)", window, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", window, StringComparison.Ordinal);
        Assert.Contains("WsExToolWindow", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Show(false)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detected_meeting_path_no_longer_uses_a_content_dialog()
    {
        var app = Read("windows-native", "Muesli.Windows.WinUI", "App.xaml.cs");
        Assert.DoesNotContain("PromptDetectedMeetingAsync", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Join & record", app, StringComparison.Ordinal);
        Assert.Contains("PresentDetectedMeeting", app, StringComparison.Ordinal);
        Assert.Contains("StartDetectedMeetingCaptureAsync", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_exposes_every_required_automation_id()
    {
        var code = Read("windows-native", "Muesli.Windows.WinUI", "MeetingNotificationWindow.xaml.cs");
        foreach (var id in new[]
                 {
                     "MeetingNotificationWindow", "MeetingNotificationTitle", "MeetingNotificationSubtitle",
                     "MeetingNotificationCountdown", "MeetingNotificationPrimaryAction", "MeetingNotificationChevron",
                     "MeetingNotificationPlatformBadge", "MeetingNotificationDismiss",
                     "MeetingNotificationMenuJoinAndRecord", "MeetingNotificationMenuJoinOnly",
                     "MeetingNotificationMenuTranscribeOnly"
                 })
        {
            Assert.Contains(id, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_window_scopes_its_own_tokens_and_supports_high_contrast()
    {
        var xaml = Read("windows-native", "Muesli.Windows.WinUI", "MeetingNotificationWindow.xaml");
        Assert.Contains("MeetingNotificationCardBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("MeetingNotificationCountdownBrush", xaml, StringComparison.Ordinal);
        var code = Read("windows-native", "Muesli.Windows.WinUI", "MeetingNotificationWindow.xaml.cs");
        Assert.Contains("HighContrast", code, StringComparison.Ordinal);
        Assert.Contains("SystemColorWindowColorBrush", code, StringComparison.Ordinal);
        Assert.Contains("Escape", code, StringComparison.Ordinal);
        Assert.Contains("reduceMotion", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Replacement_suppresses_the_old_prompt_callbacks()
    {
        var service = Read("windows-native", "Muesli.Windows.WinUI", "Services", "WinUiMeetingNotificationService.cs");
        Assert.Contains("SuppressCallbacks()", service, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_window, window)", service, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detector_prioritizes_native_observations_and_bounds_the_scan()
    {
        var detector = Read("windows-native", "Muesli.Windows.Platform", "Services", "MeetingDetectionService.cs");
        Assert.Contains("ObserveWindow", detector, StringComparison.Ordinal);
        Assert.Contains("IsPlausibleForUiAutomation", detector, StringComparison.Ordinal);
        Assert.Contains("MeetingScanBudget.ShouldStop", detector, StringComparison.Ordinal);
        Assert.Contains("BrowserUrlCacheTtl", detector, StringComparison.Ordinal);
        Assert.Contains("MaxBrowserEditScan", detector, StringComparison.Ordinal);
        Assert.Contains("IsWindowCloaked", detector, StringComparison.Ordinal);
        Assert.Contains("MeetingEnded", detector, StringComparison.Ordinal);
    }
}
