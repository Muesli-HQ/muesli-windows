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
        Assert.Equal("Welcome to muesli", OnboardingStepCatalog.Get(0).Title);
        Assert.Equal("You're all set", OnboardingStepCatalog.Get(99).Title);
        // macOS OnboardingFlow.Step order: welcome, model, hotkey, permissions, test, summaries.
        Assert.Equal(
            ["Welcome to muesli", "Choose your transcription model", "Dictation Shortcut", "Microphone", "Test Dictation", "Meeting Summaries"],
            OnboardingStepCatalog.Steps.Take(6).Select(step => step.Title));
        Assert.All(OnboardingStepCatalog.Steps, step => Assert.Equal(step, OnboardingStepCatalog.Steps[step.Step]));
    }

    [Theory]
    [InlineData(OnboardingUseCases.Dictation, new[] { 0, 1, 2, 3, 4, 6 })]
    [InlineData(OnboardingUseCases.Meetings, new[] { 0, 1, 3, 5, 6 })]
    [InlineData(OnboardingUseCases.Everything, new[] { 0, 1, 2, 3, 4, 5, 6 })]
    [InlineData("voice_notes", new[] { 0, 1, 2, 3, 4, 5, 6 })]
    public void The_use_case_decides_which_setup_steps_follow(string useCase, int[] expected)
    {
        Assert.Equal(expected, OnboardingUseCases.Steps(useCase));
    }

    [Fact]
    public void Use_case_toggles_keep_one_workflow_and_skip_to_the_next_kept_step()
    {
        Assert.Equal(OnboardingUseCases.Meetings, OnboardingUseCases.Toggle(OnboardingUseCases.Everything, OnboardingUseCases.Dictation));
        Assert.Equal(OnboardingUseCases.Everything, OnboardingUseCases.Toggle(OnboardingUseCases.Meetings, OnboardingUseCases.Dictation));
        Assert.Equal(OnboardingUseCases.Dictation, OnboardingUseCases.Toggle(OnboardingUseCases.Dictation, OnboardingUseCases.Dictation));
        Assert.Equal(OnboardingSteps.Summaries, OnboardingUseCases.Normalize(OnboardingSteps.Test, OnboardingUseCases.Meetings));
        Assert.Equal(OnboardingSteps.Ready, OnboardingUseCases.Normalize(OnboardingSteps.Summaries, OnboardingUseCases.Dictation));
    }

    [Fact]
    public void ConfirmationPreviewIsLocalAndNeverLooksLikePersistence()
    {
        var action = new ComputerUseAction(
            ComputerUseActionKind.SetText,
            new ComputerUseTarget("notepad.exe", "editor", null, null, null),
            "type this exactly",
            ComputerUseRisk.None);
        var preview = ComputerUseConfirmationPreview.Build(action);
        Assert.Contains("notepad.exe", preview);
        Assert.Contains("editor", preview);
        Assert.Contains("type this exactly", preview);
        Assert.DoesNotContain("trace.json", preview, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("your microphone only", recording);
        Assert.Contains("isn't being recorded", recording);

        // The disclosure names both sources in plain words and keeps capture mechanics out of it.
        var both = MeetingCaptureStatusFormatter.Describe(new MeetingCaptureSnapshot(
            MeetingSessionState.Recording, "EndpointLoopback", true, true, ["loopback dropped"], false, false));
        Assert.Equal("Recording your microphone and all sound playing on this computer.", both);
        var targeted = MeetingCaptureStatusFormatter.Describe(new MeetingCaptureSnapshot(
            MeetingSessionState.Recording, "ProcessTreeLoopback", true, true, [], false, false));
        Assert.Equal("Recording your microphone and sound from the meeting app.", targeted);
        foreach (var text in new[] { recording, both, targeted })
        {
            Assert.DoesNotContain("loopback", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("endpoint", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("process", text, StringComparison.OrdinalIgnoreCase);
        }

        var paused = MeetingCaptureStatusFormatter.Describe(new MeetingCaptureSnapshot(
            MeetingSessionState.RecoverableInterruption,
            "processtreeloopback",
            true,
            true,
            ["loopback dropped"],
            true,
            true));
        Assert.Equal("Paused. Nothing is being recorded.", paused);

        Assert.Contains(
            "Calendar join is unavailable",
            MeetingCaptureStatusFormatter.DescribeDetection("ms-teams", MeetingEvidenceStrength.Strong, true, false));
        Assert.Contains(
            "Upcoming calendar meetings are not available",
            MeetingCaptureStatusFormatter.DescribeDetection(null, MeetingEvidenceStrength.None, false, false));
    }

    [Fact]
    public void ComputerUseConfigurationFailClosedAndWinUiHostNoticeAreHonest()
    {
        var settings = new MuesliSettings { ComputerUseEnabled = true, ComputerUsePlannerProvider = "none" };
        Assert.False(ComputerUseConfiguration.TryValidate(settings, null, out var error));
        Assert.Contains("OpenAI", error);
        Assert.Equal("OpenAI", ComputerUseConfiguration.ProviderLabel("openai"));
        Assert.Contains("shared Windows UI Automation", ComputerUseConfiguration.WinUiExecutionHostNotice);
        Assert.EndsWith(Path.Combine("computer-use", "trace.json"), ComputerUseConfiguration.TracePath(@"C:\tmp\profile"));
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
        Assert.Contains("ComputerUsePreviewConfirmationButton", settings);

        var timeline = File.ReadAllText(Path.Combine(winUi, "Pages", "TimelinePage.xaml"));
        // Timeline is one chronological mixed feed: no source filter and no kind segmented control.
        Assert.Contains("TimelineSort", timeline);
        Assert.DoesNotContain("TimelineFilterAll", timeline);
        Assert.DoesNotContain("From iPhone", timeline);
        Assert.DoesNotContain("This Mac", timeline);

        var meetings = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingsPage.xaml"));
        var recordingPage = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingRecordingPage.xaml"));
        Assert.Contains("PauseMeetingButton", recordingPage);
        Assert.Contains("ShowLiveTranscriptButton", recordingPage);
        Assert.Contains("MeetingRecoveryPanel", meetings);

        var main = File.ReadAllText(Path.Combine(winUi, "MainPage.xaml"));
        Assert.Contains("SidebarGreeting", main);
        Assert.Contains("FeatureTourOverlay", main);

        Assert.True(File.Exists(Path.Combine(winUi, "OnboardingWindow.xaml")));
        Assert.True(File.Exists(Path.Combine(winUi, "MeetingLiveTranscriptWindow.xaml")));
        Assert.True(File.Exists(Path.Combine(winUi, "ComputerUseConfirmationWindow.xaml")));

        var about = File.ReadAllText(Path.Combine(winUi, "Pages", "AboutPage.xaml"));
        Assert.Contains("ResumeSetupButton", about);
        Assert.Contains("ReplayFeatureTourButton", about);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
