using System.Windows;
using System.Windows.Controls;
using Muesli.Windows.Features;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
    private UIElement DictationsPage => _shell.Views.Dictations;
    private UIElement SearchPage => _shell.Views.Search;
    private UIElement MeetingsPage => _shell.Views.Meetings;
    private UIElement MeetingDetailView => _shell.Views.MeetingDetail;
    private UIElement DictionaryPage => _shell.Views.Dictionary;
    private UIElement ModelsPage => _shell.Views.Models;
    private UIElement ShortcutsPage => _shell.Views.Shortcuts;
    private UIElement SettingsPage => _shell.Views.Settings;
    private UIElement AboutPage => _shell.Views.About;
    private System.Windows.Controls.Button DictationsNav => _shell.Views.DictationsNav;
    private System.Windows.Controls.Button MeetingsNav => _shell.Views.MeetingsNav;
    private System.Windows.Controls.Button AllMeetingsNav => _shell.Views.AllMeetingsNav;
    private System.Windows.Controls.Button DictionaryNav => _shell.Views.DictionaryNav;
    private System.Windows.Controls.Button ModelsNav => _shell.Views.ModelsNav;
    private System.Windows.Controls.Button ShortcutsNav => _shell.Views.ShortcutsNav;
    private System.Windows.Controls.Button SettingsNav => _shell.Views.SettingsNav;
    private System.Windows.Controls.Button AboutNav => _shell.Views.AboutNav;
    private System.Windows.Controls.StackPanel MeetingsChildren => _shell.Views.MeetingsChildren;
    private System.Windows.Controls.Button LightThemeButton => _shell.Views.LightThemeButton;
    private System.Windows.Controls.Button DarkThemeButton => _shell.Views.DarkThemeButton;
    private System.Windows.Threading.Dispatcher Dispatcher => _shell.Dispatcher;
    private Grid MeetingsBrowserView => _shell.Views.Meetings.BrowserRoot;
    private WpfTextBox MainSearchInput => _shell.Views.Search.MainSearchInputControl;
    private System.Windows.Controls.PasswordBox OpenAIApiKeyBox => _shell.Views.Settings.OpenAIApiKeyInput;
    private System.Windows.Controls.PasswordBox OpenRouterApiKeyBox => _shell.Views.Settings.OpenRouterApiKeyInput;
    private Border MeetingNotesTab => _shell.Views.MeetingDetail.MeetingNotesTabControl;
    private TextBlock MeetingNotesTabLabel => _shell.Views.MeetingDetail.MeetingNotesTabLabelControl;
    private Border MeetingTranscriptTab => _shell.Views.MeetingDetail.MeetingTranscriptTabControl;
    private TextBlock MeetingTranscriptTabLabel => _shell.Views.MeetingDetail.MeetingTranscriptTabLabelControl;
    private StackPanel MeetingWarningsPanel => _shell.Views.MeetingDetail.MeetingWarningsPanelControl;
    private ItemsControl MeetingWarningsItems => _shell.Views.MeetingDetail.MeetingWarningsItemsControl;
    private StackPanel MeetingNotesPanel => _shell.Views.MeetingDetail.MeetingNotesPanelControl;
    private StackPanel MeetingNotesContent => _shell.Views.MeetingDetail.MeetingNotesContentControl;
    private StackPanel MeetingTranscriptPanel => _shell.Views.MeetingDetail.MeetingTranscriptPanelControl;
    private StackPanel SpeakerAliasPanel => _shell.Views.MeetingDetail.SpeakerAliasPanelControl;
    private WpfTextBox DictionaryPhraseBox => _shell.Views.Dictionary.PhraseInput;
    private WpfTextBox DictionaryReplacementBox => _shell.Views.Dictionary.ReplacementInput;
    private System.Windows.Controls.Slider DictionaryThresholdSlider => _shell.Views.Dictionary.ThresholdInput;

    private void ShowPage(UIElement activePage, WpfButton activeNav)
    {
        var page = activePage switch
        {
            _ when activePage == DictationsPage => AppPage.Dashboard,
            _ when activePage == SearchPage => AppPage.Search,
            _ when activePage == MeetingsPage => AppPage.Meetings,
            _ when activePage == DictionaryPage => AppPage.Dictionary,
            _ when activePage == ModelsPage => AppPage.Models,
            _ when activePage == ShortcutsPage => AppPage.Shortcuts,
            _ when activePage == SettingsPage => AppPage.Settings,
            _ when activePage == AboutPage => AppPage.About,
            _ => AppPage.Dashboard
        };
        _appServices.Navigation.Navigate(page);
        _shell.ShowPage(page);
    }

    private void ShowPage(AppPage page)
    {
        _appServices.Navigation.Navigate(page);
        _shell.ShowPage(page);
    }

    public void Navigate(AppPage page)
    {
        if (page == AppPage.Meetings)
        {
            SaveActiveSpeakerAliases();
            _selectedMeetingFolderId = null;
            _selectedMeeting = null;
            MeetingsBrowserView.Visibility = Visibility.Visible;
            MeetingDetailView.Visibility = Visibility.Collapsed;
            var alreadyOnMeetings = _appServices.Navigation.State.CurrentPage is AppPage.Meetings or AppPage.MeetingDetail;
            if (!alreadyOnMeetings)
            {
                _meetingsExpanded = true;
                MeetingsChildren.Visibility = Visibility.Visible;
                OnPropertyChanged(nameof(MeetingsChevron));
            }
            RefreshMeetingViews();
        }

        if (page == AppPage.Settings)
        {
            var settingsTab = _appServices.Navigation.State.SettingsTab;
            var settingsTabIndex = Array.FindIndex(
                SettingsTabNames,
                tab => tab.Equals(settingsTab, StringComparison.OrdinalIgnoreCase));
            if (settingsTabIndex < 0)
            {
                settingsTabIndex = 0;
            }
            if (_selectedSettingsTabIndex != settingsTabIndex)
            {
                _selectedSettingsTabIndex = settingsTabIndex;
                OnPropertyChanged(nameof(SelectedSettingsTabIndex));
            }
            OnPropertyChanged(nameof(StartupRegistrationLabel));
            OnPropertyChanged(nameof(SetupNeedsResume));
            OnPropertyChanged(nameof(SetupResumeLabel));
            _trayIconService.Refresh();
        }
        else if (page == AppPage.About)
        {
            _trayIconService.Refresh();
        }

        ShowPage(page);
    }

    public void ClearSearch() => SearchQuery = string.Empty;

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => ClearSearch();

    public void UpdateCompactLayout(bool isCompact)
    {
        IsCompactLayout = isCompact;
    }

    public void OnWindowActivated()
    {
        if (_isVisualPreview)
            return;

        var registered = StartupRegistrationService.IsEnabled();
        if (_startAtLogin != registered)
        {
            _startAtLogin = registered;
            OnPropertyChanged(nameof(StartAtLogin));
        }
        OnPropertyChanged(nameof(StartupRegistrationLabel));
        OnPropertyChanged(nameof(SetupNeedsResume));
        OnPropertyChanged(nameof(SetupResumeLabel));
        _trayIconService.Refresh();
    }

    public void HandleShellAction(string actionName, object sender, RoutedEventArgs args)
    {
        var handler = GetType().GetMethod(
            actionName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (handler is null)
        {
            _logService.Info($"Shell action handler was not found: {actionName}");
            return;
        }

        try
        {
            _ = handler.Invoke(this, [sender, args]);
        }
        catch (System.Reflection.TargetInvocationException exception)
        {
            _logService.Error($"Shell action failed: {actionName}", exception.InnerException ?? exception);
        }
    }

    private object FindResource(object key) => _shell.FindResource(key);

    private void Focus() => _shell.FocusWindow();

    private void ShowFeatureTour()
    {
        new FeatureTourWindow(
            () =>
            {
                _lastCompletedFeatureTourVersion = FeatureTourWindow.CurrentVersion;
                SaveSettings();
            },
            onStep: step =>
            {
                if (step == 1)
                {
                    _toastNotificationService.SetIdleIndicatorVisible(true, showNow: true);
                    Navigate(AppPage.Dashboard);
                    return;
                }

                Navigate(step switch
                {
                    0 => AppPage.Dashboard,
                    2 => AppPage.Models,
                    3 => AppPage.Meetings,
                    4 => AppPage.Settings,
                    _ => AppPage.About
                });
            })
        { Owner = _shell.Window }.Show();
    }

    public void SetTheme(string theme)
    {
        try
        {
            _theme = theme;
            ApplyTheme(theme);
            _shell.ShowPage(_appServices.Navigation.State.CurrentPage);
            SaveSettings();
            DictationStatus = theme.Equals("light", StringComparison.OrdinalIgnoreCase)
                ? "Light mode enabled"
                : "Dark mode enabled";
            OnPropertyChanged(nameof(SelectedTheme));
        }
        catch (Exception exception)
        {
            _logService.Error("Theme switch failed.", exception);
            DictationStatus = $"Theme switch failed: {exception.Message}";
            _toastNotificationService.Show("Theme switch failed", exception.Message, ToastState.Error, 4200);
        }
    }

    private void RepairStartup_Click(object sender, RoutedEventArgs e)
    {
        if (_isVisualPreview)
        {
            DictationStatus = "Preview-only: startup repair is disabled and no registry state was changed.";
            return;
        }

        try
        {
            StartupRegistrationService.SetEnabled(true);
            StartAtLogin = true;
            DictationStatus = "Startup registration repaired";
        }
        catch (Exception exception)
        {
            DictationStatus = $"Startup repair failed: {ConciseUiError(exception)}";
        }
        finally
        {
            OnPropertyChanged(nameof(StartupRegistrationLabel));
        }
    }

    private void ResumeSetup_Click(object sender, RoutedEventArgs e) =>
        ShowOnboardingIfNeeded(explicitResume: true);

    private void FeatureTour_Click(object sender, RoutedEventArgs e) => ShowFeatureTour();

    private void AttachFeatureViews()
    {
        foreach (var view in new FeatureViewBase[]
                 {
                     _shell.Views.Dictations,
                     _shell.Views.Search,
                     _shell.Views.Meetings,
                     _shell.Views.MeetingDetail,
                     _shell.Views.Dictionary,
                     _shell.Views.Models,
                     _shell.Views.Shortcuts,
                     _shell.Views.Settings,
                     _shell.Views.About
                 })
        {
            view.ActionRequested += FeatureView_ActionRequested;
        }
    }

    private void FeatureView_ActionRequested(object? sender, FeatureActionEventArgs e)
    {
        var handler = GetType().GetMethod(
            e.ActionName,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (handler is null)
        {
            _logService.Info($"Feature action handler was not found: {e.ActionName}");
            return;
        }

        try
        {
            _ = handler.Invoke(this, [e.Sender, e.EventArgs]);
        }
        catch (System.Reflection.TargetInvocationException exception)
        {
            _logService.Error($"Feature action failed: {e.ActionName}", exception.InnerException ?? exception);
        }
    }

    private void DetachFeatureViews()
    {
        foreach (var view in new FeatureViewBase[]
                 {
                     _shell.Views.Dictations,
                     _shell.Views.Search,
                     _shell.Views.Meetings,
                     _shell.Views.MeetingDetail,
                     _shell.Views.Dictionary,
                     _shell.Views.Models,
                     _shell.Views.Shortcuts,
                     _shell.Views.Settings,
                     _shell.Views.About
                 })
        {
            view.ActionRequested -= FeatureView_ActionRequested;
        }
    }
}
