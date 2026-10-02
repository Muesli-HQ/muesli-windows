using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class MeetingDetailPage : Page
{
    public MeetingDetailViewModel ViewModel { get; } = new(
        App.Library,
        App.Settings,
        App.Meetings,
        App.Clipboard,
        App.FilePickers,
        App.Dialogs,
        App.UiDispatcher);

    public MeetingDetailPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsStatusError)
                or nameof(ViewModel.IsWorking)
                or nameof(ViewModel.IsStatusOpen)
                or null
                or "")
            {
                UpdateStatusSeverity();
            }

            if (args.PropertyName == nameof(ViewModel.Transcript)) SavedTranscriptFeed.Update(null, ViewModel.Transcript);

            if (args.PropertyName is nameof(ViewModel.ShowNotesTab) or null or "")
            {
                SyncTabBar();
            }
        };
        // A folder move or a delete changes what the shell rail's folder rows count. The rail
        // recomputes through the shared builder; this page never counts anything itself.
        ViewModel.LibraryChanged += OnLibraryChanged;
        // Deleting removes the whole page chrome, and with it the control that had focus, so
        // WinUI fell back to the first focusable element in the window — the sidebar's collapse
        // toggle. Put focus on the one action the page still offers instead.
        ViewModel.MeetingRemoved += OnMeetingRemoved;
        ViewModel.RecordingStarted += OnRecordingStarted;
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            SyncTabBar();
            UpdateStatusSeverity();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        Unloaded += (_, _) =>
        {
            ViewModel.LibraryChanged -= OnLibraryChanged;
            ViewModel.MeetingRemoved -= OnMeetingRemoved;
            ViewModel.RecordingStarted -= OnRecordingStarted;
            ViewModel.Dispose();
        };
    }

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The same rule, reachable from a DataTemplate where <c>ViewModel</c> is not in scope.</summary>
    public static Visibility StaticBoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id && id == App.Meetings.ActiveMeetingId)
        {
            DispatcherQueue.TryEnqueue(() => Frame.Navigate(typeof(MeetingsPage)));
            return;
        }
        ViewModel.Load(e.Parameter as string ?? "");
        SyncTabBar();
        UpdateStatusSeverity();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OnRecordingStarted(object? sender, EventArgs e) => Frame.Navigate(typeof(MeetingsPage));
    private void RelatedMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) Frame.Navigate(typeof(MeetingDetailPage), id);
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.RefreshMeetingFolders();

    private void OnMeetingRemoved(object? sender, EventArgs e) =>
        BackButton.Focus(FocusState.Programmatic);

    private void PlaybackSlider_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ViewModel.SeekTo(PlaybackSlider.Value);
    }

    private void DetailTabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.ShowNotesTab = sender.SelectedItem == NotesTabItem;
    }

    private void SyncTabBar()
    {
        var desired = ViewModel.ShowNotesTab ? NotesTabItem : TranscriptTabItem;
        if (DetailTabBar.SelectedItem != desired)
        {
            DetailTabBar.SelectedItem = desired;
        }
    }

    private void UpdateStatusSeverity()
    {
        StatusBar.Severity = ViewModel.IsWorking
            ? InfoBarSeverity.Informational
            : ViewModel.IsStatusError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Success;
    }

    /// <summary>
    /// Compact is the 720-wide window with the icon rail (~648 DIP of page). Action rows stack
    /// instead of clipping. Medium follows the 1008 window; large is 1280 with the expanded rail.
    /// The tier comes from <see cref="MuesliPageMetrics"/> rather than a private 760 threshold,
    /// and the reading cap is the shared <c>MuesliPageContentMaxWidth</c> rather than a local 980
    /// — the two things P1-04 froze.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var compact = MuesliPageMetrics.IsCompact(width);
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);
        ContentStack.MaxWidth = compact ? double.PositiveInfinity : ContentMaxWidth;
        ContentStack.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        HeaderCommands.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        HeaderCommands.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ChromeActions.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        DetailTabWell.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        MoreActionsLabel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        if (compact)
        {
            Grid.SetRow(ChromeActions, 1);
            Grid.SetColumn(ChromeActions, 0);
            Grid.SetColumnSpan(ChromeActions, 2);
            foreach (var button in new[] { SaveButton, CopyTranscriptButton, ExportButton, MoreActionsButton })
            {
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            Grid.SetRow(TrackPicker, 0);
            Grid.SetColumn(TrackPicker, 0);
            Grid.SetColumnSpan(TrackPicker, 4);
            PlaybackToolbar.ColumnDefinitions[0].Width = new GridLength(0, GridUnitType.Auto);
            Grid.SetRow(PlayButton, 1);
            Grid.SetColumn(PlayButton, 0);
            Grid.SetColumnSpan(PlayButton, 1);
            Grid.SetRow(PauseButton, 1);
            Grid.SetColumn(PauseButton, 1);
            Grid.SetRow(StopButton, 1);
            Grid.SetColumn(StopButton, 2);

            Grid.SetRow(SummaryTemplatePicker, 1);
            Grid.SetColumn(SummaryTemplatePicker, 0);
            Grid.SetColumnSpan(SummaryTemplatePicker, 2);
            SummaryTemplatePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
            SummaryTemplatePicker.MaxWidth = double.PositiveInfinity;
            Grid.SetRow(GenerateNotesButton, 1);
            Grid.SetColumn(GenerateNotesButton, 2);
            Grid.SetColumnSpan(GenerateNotesButton, 1);
            GenerateNotesButton.HorizontalAlignment = HorizontalAlignment.Stretch;

            Grid.SetRow(SaveTranscriptButton, 1);
            Grid.SetColumn(SaveTranscriptButton, 0);
            Grid.SetColumnSpan(SaveTranscriptButton, 1);
            SaveTranscriptButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(RetranscribeButton, 1);
            Grid.SetColumn(RetranscribeButton, 1);
            Grid.SetColumnSpan(RetranscribeButton, 2);
            RetranscribeButton.HorizontalAlignment = HorizontalAlignment.Stretch;

            Grid.SetRow(AliasSourceBox, 0);
            Grid.SetColumn(AliasSourceBox, 0);
            Grid.SetColumnSpan(AliasSourceBox, 3);
            Grid.SetRow(AliasReplacementBox, 1);
            Grid.SetColumn(AliasReplacementBox, 0);
            Grid.SetColumnSpan(AliasReplacementBox, 2);
            Grid.SetRow(AddAliasButton, 1);
            Grid.SetColumn(AddAliasButton, 2);
            Grid.SetColumnSpan(AddAliasButton, 1);

            FolderPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
            FolderPicker.MaxWidth = double.PositiveInfinity;
            CandidateActions.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(ChromeActions, 0);
            Grid.SetColumn(ChromeActions, 1);
            Grid.SetColumnSpan(ChromeActions, 1);
            foreach (var button in new[] { SaveButton, CopyTranscriptButton, ExportButton, MoreActionsButton })
            {
                button.HorizontalAlignment = HorizontalAlignment.Left;
            }

            PlaybackToolbar.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(TrackPicker, 0);
            Grid.SetColumn(TrackPicker, 0);
            Grid.SetColumnSpan(TrackPicker, 1);
            Grid.SetRow(PlayButton, 0);
            Grid.SetColumn(PlayButton, 1);
            Grid.SetColumnSpan(PlayButton, 1);
            Grid.SetRow(PauseButton, 0);
            Grid.SetColumn(PauseButton, 2);
            Grid.SetRow(StopButton, 0);
            Grid.SetColumn(StopButton, 3);

            Grid.SetRow(SummaryTemplatePicker, 0);
            Grid.SetColumn(SummaryTemplatePicker, 1);
            Grid.SetColumnSpan(SummaryTemplatePicker, 1);
            SummaryTemplatePicker.HorizontalAlignment = HorizontalAlignment.Left;
            SummaryTemplatePicker.MaxWidth = 260;
            Grid.SetRow(GenerateNotesButton, 0);
            Grid.SetColumn(GenerateNotesButton, 2);
            Grid.SetColumnSpan(GenerateNotesButton, 1);
            GenerateNotesButton.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(SaveTranscriptButton, 0);
            Grid.SetColumn(SaveTranscriptButton, 1);
            Grid.SetColumnSpan(SaveTranscriptButton, 1);
            SaveTranscriptButton.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(RetranscribeButton, 0);
            Grid.SetColumn(RetranscribeButton, 2);
            Grid.SetColumnSpan(RetranscribeButton, 1);
            RetranscribeButton.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(AliasSourceBox, 0);
            Grid.SetColumn(AliasSourceBox, 0);
            Grid.SetColumnSpan(AliasSourceBox, 1);
            Grid.SetRow(AliasReplacementBox, 0);
            Grid.SetColumn(AliasReplacementBox, 1);
            Grid.SetColumnSpan(AliasReplacementBox, 1);
            Grid.SetRow(AddAliasButton, 0);
            Grid.SetColumn(AddAliasButton, 2);
            Grid.SetColumnSpan(AddAliasButton, 1);

            FolderPicker.HorizontalAlignment = HorizontalAlignment.Left;
            FolderPicker.MaxWidth = 280;
            CandidateActions.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    /// <summary>The shared reading-width cap; the literal is only the fallback if the key is gone.</summary>
    private static double ContentMaxWidth =>
        Application.Current.Resources["MuesliPageContentMaxWidth"] is double value ? value : 1040;
}
