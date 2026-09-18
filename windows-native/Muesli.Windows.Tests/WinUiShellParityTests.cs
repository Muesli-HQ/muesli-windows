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
        Assert.Contains("This PC", timeline);
        Assert.DoesNotContain("From iPhone", timeline);
        Assert.DoesNotContain("This Mac", timeline);

        var meetings = File.ReadAllText(Path.Combine(winUi, "Pages", "MeetingsPage.xaml"));
        Assert.Contains("PauseMeetingButton", meetings);
        Assert.Contains("ShowLiveTranscriptButton", meetings);
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
