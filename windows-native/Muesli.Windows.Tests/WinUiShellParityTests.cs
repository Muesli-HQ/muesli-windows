using Muesli.Windows.ComputerUse;
using Muesli.Windows.WinUI.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class WinUiShellParityTests
{
    [Fact]
    public void FeatureTourCatalogKeepsSixBeatsAndFloatingIndicator()
    {
        Assert.Equal(2, FeatureTourCatalog.CurrentVersion);
        Assert.Equal(6, FeatureTourCatalog.Steps.Count);
        Assert.Contains(FeatureTourCatalog.Steps, step => step.TargetName == "FloatingIndicator");
        Assert.Equal(
            new[] { "dictations", "FloatingIndicator", "models", "meetings", "settings", "about" },
            FeatureTourCatalog.Steps.Select(step => step.TargetName).ToArray());
    }

    [Fact]
    public void OnboardingCatalogMatchesSevenResumableSteps()
    {
        Assert.Equal(7, OnboardingStepCatalog.Steps.Count);
        Assert.Equal(OnboardingProgress.LastStep, OnboardingStepCatalog.Steps[^1].Step);
        Assert.Equal("Welcome to Muesli", OnboardingStepCatalog.Get(0).Title);
        Assert.Equal("You are ready", OnboardingStepCatalog.Get(99).Title);
    }

    [Fact]
    public void CaptureStatusStaysTruthfulForMissingSystemAudioAndPausedRecovery()
    {
        var recording = MeetingCaptureStatusFormatter.Describe(new MeetingCaptureSnapshot(
            MeetingSessionState.Recording,
            "unavailable",
            true,
            false,
            [],
            false,
            false));
        Assert.Contains("Microphone capturing", recording);
        Assert.Contains("System audio not captured", recording);

        var paused = MeetingCaptureStatusFormatter.Describe(new MeetingCaptureSnapshot(
            MeetingSessionState.RecoverableInterruption,
            "processtreeloopback",
            true,
            true,
            ["loopback dropped"],
            true,
            true));
        Assert.Contains("Paused", paused);
        Assert.Contains("process-tree loopback", paused);
        Assert.Contains("Recovered from an interruption", paused);

        Assert.Contains(
            "Calendar join is unavailable",
            MeetingCaptureStatusFormatter.DescribeDetection("ms-teams", MeetingEvidenceStrength.Strong, true, false));
        Assert.Contains(
            "Upcoming calendar meetings are not available",
            MeetingCaptureStatusFormatter.DescribeDetection(null, MeetingEvidenceStrength.None, false, false));
    }

    [Fact]
    public void ComputerUseNeedsASignedInPlannerAndTheNoticeIsHonest()
    {
        var chatGpt = new MuesliSettings { ComputerUseEnabled = true, ComputerUsePlannerProvider = "none" };
        Assert.False(ComputerUseConfiguration.TryValidate(chatGpt, chatGptSignedIn: false, out var error));
        Assert.Contains("Sign in to ChatGPT", error);
        Assert.True(ComputerUseConfiguration.TryValidate(chatGpt, chatGptSignedIn: true, out _));
        Assert.Contains("sends the screen", ComputerUseConfiguration.Notice);
        Assert.EndsWith(Path.Combine("computer-use", "runs", "dict_1.json"), ComputerUseConfiguration.RunStepsPath(@"C:\tmp\profile", "dict_1"));
    }

    [Fact]
    public void ComputerUseRunRoundTripsAndCopiesLikeMacOs()
    {
        var root = Path.Combine(Path.GetTempPath(), "muesli-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            ComputerUseRun.Save(root, "dict_1", new CuaRunResult(CuaRunStatus.TimedOut, "Computer Use ran out of time.",
                [new CuaTraceEvent("command", "Command", "Play a song", null), new CuaTraceEvent("tool_call", "Tool call", "open_url {}", 1)]));
            var run = ComputerUseRun.Load(root, "dict_1")!;
            Assert.Equal("timed_out", run.Status);
            Assert.Equal("Timed out", run.StatusLabel);
            Assert.Equal(new ComputerUseStep("Step 1", "Tool call", "open_url {}"), run.Steps[1]);
            Assert.StartsWith("CUA Command\nPlay a song\n\nFinal Status\ntimed_out\n\nFinal Message\nComputer Use ran out of time.\n\nStep Trail\nRun - Command",
                run.TraceText("Play a song"));

            // A run saved before the steps were structured still shows.
            File.WriteAllText(Path.Combine(root, "computer-use", "runs", "dict_2.txt"), "Failed\nCommand: Play\n[3] Failed: ChatGPT is busy");
            var legacy = ComputerUseRun.Load(root, "dict_2")!;
            Assert.Equal("failed", legacy.Status);
            Assert.Equal(new ComputerUseStep("Step 3", "Failed", "ChatGPT is busy"), legacy.Steps[1]);

            ComputerUseRun.Delete(root, "dict_2");
            Assert.Null(ComputerUseRun.Load(root, "dict_2"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PlannerEffortFallsBackToTheModelDefault()
    {
        Assert.Equal("gpt-6.1-sol", ComputerUseConfiguration.Model("").Id);
        Assert.Equal("gpt-6.1-sol", ComputerUseConfiguration.Model("custom-model").Id);
        var astra = ComputerUseConfiguration.Model("gpt-6-astra");
        Assert.Equal("high", ComputerUseConfiguration.Effort(astra, ""));
        Assert.Equal("high", ComputerUseConfiguration.Effort(astra, "none"));
        Assert.Equal("none", ComputerUseConfiguration.Effort(ComputerUseConfiguration.Model("gpt-6-luna"), "none"));
        Assert.Equal("Extra High", ComputerUseConfiguration.EffortLabel("xhigh"));
    }

    [Fact]
    public void HistoryFiltersHeadingsAndLongTextFollowMacOs()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0);
        Assert.Equal(["All time"], HistoryFormat.AvailableDateFilters(null, now));
        Assert.Equal(["All time", "Last 2 days", "Last week"], HistoryFormat.AvailableDateFilters(now.AddDays(-5), now));
        Assert.Equal(6, HistoryFormat.AvailableDateFilters(now.AddDays(-40), now).Count);
        Assert.Equal(now.AddDays(-7), HistoryFormat.DateFilterStart(2, now));
        Assert.Null(HistoryFormat.DateFilterStart(0, now));

        Assert.Equal("TODAY", HistoryFormat.DayHeading(now, now));
        Assert.Equal("YESTERDAY", HistoryFormat.DayHeading(now.AddDays(-1), now));

        var text = new ExpandableText(new string('a', 200));
        Assert.True(text.CanExpand);
        Assert.Equal(161, text.Shown.Length);
        Assert.Equal(3, text.MaxLines);
        text.IsExpanded = true;
        Assert.Equal(200, text.Shown.Length);
        Assert.Equal("Show less", text.ToggleLabel);
        Assert.True(new ExpandableText("a\nb\nc\nd").CanExpand);
        Assert.False(new ExpandableText("short").CanExpand);
    }

    [Fact]
    public void DictationTargetAppsGoWithTheirDictation()
    {
        var root = Path.Combine(Path.GetTempPath(), "muesli-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var notepad = new DictationTargetApp("Notepad", "notepad", null);
            DictationTargetApps.Record(root, "d1", notepad);
            DictationTargetApps.Record(root, "d2", new DictationTargetApp("Google Chrome", "chrome", null));
            DictationTargetApps.Record(root, "d3", notepad with { Name = "Notepad 2" });
            Assert.Equal(["Google Chrome", "Notepad"], DictationTargetApps.Distinct(DictationTargetApps.LoadAll(root).OrderBy(pair => pair.Key).Select(pair => pair.Value)).Select(app => app.Name));
            DictationTargetApps.Remove(root, "d1");
            Assert.False(DictationTargetApps.LoadAll(root).ContainsKey("d1"));
            DictationTargetApps.Clear(root);
            Assert.Empty(DictationTargetApps.LoadAll(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void WindowsPermissionRowsDoNotReuseMacTccLabels()
    {
        var rows = WindowsPermissionStatus.Build(true, true, "Granted", "WASAPI capture verified");
        Assert.Contains(rows, row => row.Name == "Microphone" && row.Granted);
        Assert.DoesNotContain(rows, row => row.Name.Contains("Accessibility", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rows, row => row.Name.Contains("Input Monitoring", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.Name.Contains("Calendar", StringComparison.OrdinalIgnoreCase) && !row.Granted);
    }

    [Fact]
    public void WinUiShellXamlTracksMacOsReferenceSurfacesWithoutInventedIphoneSources()
    {
        var root = FindRepositoryRoot();
        var winUi = Path.Combine(root, "windows-native", "Muesli.Windows.WinUI");
        var settings = File.ReadAllText(Path.Combine(winUi, "Pages", "SettingsPage.xaml"));
        Assert.Contains("Computer Use", settings);
        Assert.Contains("SelectorBar", settings);
        Assert.Contains("ComputerUseThinking", settings);

        var timeline = File.ReadAllText(Path.Combine(winUi, "Pages", "TimelinePage.xaml"));
        // Timeline is one chronological mixed feed with the macOS app and date filters, but no
        // source filter (Windows has one source) and no kind segmented control.
        Assert.Contains("TimelineDateFilter", timeline);
        Assert.Contains("TimelineAppFilter", timeline);
        Assert.DoesNotContain("TimelineFilterAll", timeline);
        Assert.DoesNotContain("From iPhone", timeline);
        Assert.DoesNotContain("This Mac", timeline);

        var meetings = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingsPage.xaml"));
        Assert.Contains("PauseMeetingButton", meetings);
        Assert.Contains("OpenMeetingNotesButton", meetings);
        Assert.Contains("MeetingRecoveryPanel", meetings);
        Assert.DoesNotContain("MeetingsRefreshButton", meetings);

        // The meeting being recorded opens as its notes document (macOS compactQuickNotesHeader).
        var detail = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingDetailPage.xaml"));
        Assert.Contains("MeetingRecordingView", detail);
        Assert.Contains("ShowLiveTranscriptButton", detail);
        Assert.Contains("MeetingRecordingNotesEditor", detail);
        Assert.Contains("MeetingThreadBreadcrumb", detail);
        Assert.DoesNotContain("MeetingSaveButton", detail);

        var main = File.ReadAllText(Path.Combine(winUi, "MainPage.xaml"));
        Assert.Contains("SidebarGreeting", main);
        Assert.Contains("FeatureTourOverlay", main);

        Assert.True(File.Exists(Path.Combine(winUi, "OnboardingWindow.xaml")));
        Assert.True(File.Exists(Path.Combine(winUi, "MeetingLiveTranscriptWindow.xaml")));
        Assert.True(File.Exists(Path.Combine(winUi, "ComputerUseConfirmationWindow.xaml")));

        var about = File.ReadAllText(Path.Combine(winUi, "Pages", "AboutPage.xaml"));
        // macOS AboutView: Support (Donate, GitHub, Report), Spread the Word, Data, Acknowledgements.
        // Setup and the tour live in the tray menu, as macOS keeps them in the menu bar.
        Assert.Contains("DonateButton", about);
        Assert.Contains("GitHubButton", about);
        Assert.Contains("ExportSupportBundleButton", about);
        Assert.Contains("ShareOnXButton", about);
        Assert.Contains("ACKNOWLEDGEMENTS", about);
        Assert.DoesNotContain("ResumeSetupButton", about);
        Assert.DoesNotContain("RuntimeDiagnosticsExpander", about);
        // macOS moved "Spread the Word" out of the sidebar into About.
        Assert.DoesNotContain("TweetNav", main);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
