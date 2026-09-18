using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class ShellCharacterizationTests
{
    [Fact]
    public void Navigation_preserves_the_last_content_page_while_search_is_open()
    {
        var navigation = new NavigationService();

        navigation.Navigate(AppPage.Meetings);
        navigation.Navigate(AppPage.Search);

        Assert.Equal(AppPage.Search, navigation.State.CurrentPage);
        Assert.Equal(AppPage.Meetings, navigation.State.LastContentPage);

        navigation.Navigate(AppPage.Settings);

        Assert.Equal(AppPage.Settings, navigation.State.CurrentPage);
        Assert.Equal(AppPage.Settings, navigation.State.LastContentPage);
    }

    [Fact]
    public void Navigation_detail_entry_and_return_keep_meeting_selection_explicit()
    {
        var navigation = new NavigationService();
        var states = new List<NavigationState>();
        navigation.Changed += (_, args) => states.Add(args.State);

        navigation.Navigate(AppPage.Meetings);
        navigation.OpenMeetingDetail("meeting-42");

        Assert.Equal(AppPage.MeetingDetail, navigation.State.CurrentPage);
        Assert.Equal(AppPage.Meetings, navigation.State.LastContentPage);
        Assert.Equal("meeting-42", navigation.State.SelectedMeetingId);

        navigation.ReturnToMeetings();

        Assert.Equal(AppPage.Meetings, navigation.State.CurrentPage);
        Assert.Null(navigation.State.SelectedMeetingId);
        Assert.Equal(3, states.Count);
    }

    [Fact]
    public void Navigation_expansion_is_page_state_and_does_not_change_the_current_page()
    {
        var navigation = new NavigationService();

        navigation.SetMeetingsExpanded(false);

        Assert.False(navigation.State.MeetingsExpanded);
        Assert.Equal(AppPage.Dashboard, navigation.State.CurrentPage);
    }

    [Fact]
    public void Settings_tab_state_survives_page_navigation_and_is_observable_without_wpf()
    {
        var navigation = new NavigationService();

        navigation.Navigate(AppPage.Settings);
        navigation.SetSettingsTab("Computer Use");
        navigation.Navigate(AppPage.Meetings);
        navigation.Navigate(AppPage.Settings);

        Assert.Equal(AppPage.Settings, navigation.State.CurrentPage);
        Assert.Equal("Computer Use", navigation.State.SettingsTab);
    }

    [Fact]
    public void Navigation_covers_every_shell_page_without_losing_the_last_content_page()
    {
        var navigation = new NavigationService();
        var pages = new[]
        {
            AppPage.Dashboard,
            AppPage.Search,
            AppPage.Meetings,
            AppPage.Dictionary,
            AppPage.Models,
            AppPage.Shortcuts,
            AppPage.Settings,
            AppPage.About
        };

        foreach (var page in pages)
        {
            navigation.Navigate(page);
            Assert.Equal(page, navigation.State.CurrentPage);
            if (page != AppPage.Search)
                Assert.Equal(page, navigation.State.LastContentPage);
        }
    }

    [Fact]
    public void Theme_choices_are_both_supported_by_settings_snapshots()
    {
        Assert.False(SettingsSnapshot.Capture(new MuesliSettings { Theme = "dark" }).IsLightTheme);
        Assert.True(SettingsSnapshot.Capture(new MuesliSettings { Theme = "light" }).IsLightTheme);
    }

    [Fact]
    public void Settings_snapshot_is_immutable_and_preserves_role_ownership_choices()
    {
        var settings = new MuesliSettings
        {
            Theme = "light",
            DictationModelId = "whisper-small-en",
            FinalMeetingModelId = "whisper-medium-en",
            LiveMeetingModelId = "nemotron-3.5-560m",
            LiveTranscriptOwnership = "unified-live-final"
        };

        var snapshot = SettingsSnapshot.Capture(settings);

        Assert.NotSame(settings, snapshot.Value);
        Assert.True(snapshot.IsLightTheme);
        Assert.Equal("whisper-small-en", snapshot.DictationModelId);
        Assert.Equal("whisper-medium-en", snapshot.FinalMeetingModelId);
        Assert.Equal("nemotron-3.5-560m", snapshot.LiveMeetingModelId);
        Assert.Equal("unified-live-final", snapshot.Value.LiveTranscriptOwnership);
    }

    [Fact]
    public void Workflow_entry_points_cover_each_existing_user_visible_start_path()
    {
        Assert.Equal(
            [
                WorkflowEntryPoint.HoldToDictate,
                WorkflowEntryPoint.GlobalHotkey,
                WorkflowEntryPoint.ManualMeeting,
                WorkflowEntryPoint.DetectedMeeting,
                WorkflowEntryPoint.MediaImport,
                WorkflowEntryPoint.ComputerUseVoice
            ],
            WorkflowEntryPoints.All);

        Assert.True(WorkflowEntryPoints.CanStartDictation(false, false));
        Assert.False(WorkflowEntryPoints.CanStartDictation(true, false));
        Assert.False(WorkflowEntryPoints.CanStartDictation(false, true));
        Assert.True(WorkflowEntryPoints.CanToggleMeeting(false));
        Assert.False(WorkflowEntryPoints.CanToggleMeeting(true));
        Assert.True(WorkflowEntryPoints.CanStartComputerUse(true, true, false, false, false, false));
        Assert.False(WorkflowEntryPoints.CanStartComputerUse(true, true, false, true, false, false));
    }
}
