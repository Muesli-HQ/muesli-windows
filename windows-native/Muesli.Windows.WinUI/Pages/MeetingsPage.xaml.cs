using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class MeetingsPage : Page
{
    public MeetingsPageViewModel ViewModel { get; } = new(
        App.Library,
        App.Meetings,
        App.MeetingDetection,
        App.FilePickers,
        App.Dialogs,
        App.UiDispatcher,
        App.ShowLiveTranscript,
        App.Clipboard);

    public MeetingsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            ViewModel.QuickNoteStarted -= OnQuickNoteStarted;
            ViewModel.Dispose();
        };
        ViewModel.QuickNoteStarted += OnQuickNoteStarted;
        ViewModel.PropertyChanged += (_, args) =>
        {
            UpdateEmptyState();
            if (args.PropertyName is nameof(ViewModel.Folders) or nameof(ViewModel.SelectedFolderId)) SyncShellFolders();
            if (args.PropertyName is nameof(ViewModel.SortIndex) or nameof(ViewModel.TimeRangeIndex) or nameof(ViewModel.TimeRangeOptions))
                UpdatePills();
        };
        Loaded += (_, _) =>
        {
            UpdateEmptyState();
            UpdatePills();
            ApplyResponsiveLayout(RootGrid.ActualWidth);
        };
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility NotToVisibility(bool value) => BoolToVisibility(!value);

    /// <summary>Badge text colour for a macOS status tone.</summary>
    public static Brush StatusInk(string tone) => (Brush)Application.Current.Resources[tone switch
    {
        "success" => "MuesliSuccessBrush",
        "warning" => "MuesliWarningBrush",
        "accent" => "MuesliAccentBrush",
        "secondary" => "MuesliTextSecondaryBrush",
        _ => "MuesliTextTertiaryBrush"
    }];

    /// <summary>The same colour at 12%, the macOS capsule fill.</summary>
    public static Brush StatusFill(string tone) =>
        StatusInk(tone) is SolidColorBrush ink ? new SolidColorBrush(ink.Color) { Opacity = 0.12 } : StatusInk(tone);

    public static Brush FolderGlyphBrush(bool filed) =>
        (Brush)Application.Current.Resources[filed ? "MuesliAccentBrush" : "MuesliTextTertiaryBrush"];

    private void ApplyResponsiveLayout(double width)
    {
        var compact = MuesliPageMetrics.IsCompact(width) || MuesliPageMetrics.IsMedium(width);
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);

        // macOS ViewThatFits: the actions drop under the folder name when the row cannot hold both.
        Grid.SetRow(HeaderActions, compact ? 1 : 0);
        Grid.SetColumn(HeaderActions, compact ? 0 : 1);
        Grid.SetColumnSpan(HeaderActions, compact ? 2 : 1);
        HeaderActions.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var tag = e.Parameter as string ?? "meetings";
        ViewModel.SelectFolder(tag.StartsWith("meetings:", StringComparison.OrdinalIgnoreCase) ? tag["meetings:".Length..] : "");
        UpdateEmptyState();
    }

    /// <summary>Keeps the shell rail's folder rows and highlight in step with this page.</summary>
    private void SyncShellFolders() =>
        (App.Window as MainWindow)?.ShellPage?.RefreshMeetingFolders(
            ViewModel.SelectedFolderId.Length == 0 ? "meetings" : $"meetings:{ViewModel.SelectedFolderId}");

    private void MeetingList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MeetingListItem meeting)
        {
            AutomationProperties.SetName(args.ItemContainer, string.Empty);
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, meeting.AccessibleName);
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        MeetingList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        ActiveMeetingPanel.Visibility = BoolToVisibility(ViewModel.ShowBanner);
        RecoveryPanel.Visibility = BoolToVisibility(ViewModel.HasRecoverable);
        // macOS activeMeetingStatusColor: red recording, amber paused, accent while finalizing.
        ActiveMeetingDot.Fill = ViewModel.IsRecording ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xEF, 0x44, 0x44))
            : (Brush)Application.Current.Resources[ViewModel.IsPaused ? "MuesliWarningBrush" : "MuesliAccentBrush"];
    }

    private void MeetingsSort_Opening(object? sender, object e) =>
        HistoryFilterBar.FillDateMenu(MeetingsSortFlyout, ["Newest first", "Oldest first"], ViewModel.SortIndex, index => ViewModel.SortIndex = index);

    private void MeetingsTimeRange_Opening(object? sender, object e) =>
        HistoryFilterBar.FillDateMenu(MeetingsTimeRangeFlyout, ViewModel.TimeRangeOptions, ViewModel.TimeRangeIndex, index => ViewModel.TimeRangeIndex = index);

    /// <summary>
    /// macOS sortButton: arrows and the order, always labelled, accent once it is not newest-first.
    /// dateFilterButton: a bare funnel at All time, an accent capsule naming the range otherwise.
    /// </summary>
    private void UpdatePills()
    {
        MeetingsSortLabel.Text = ViewModel.SortLabel;
        var sorted = !ViewModel.IsDefaultSort;
        var tone = (Brush)Application.Current.Resources[sorted ? "MuesliAccentBrush" : "MuesliTextSecondaryBrush"];
        MeetingsSortIcon.Foreground = tone;
        MeetingsSortLabel.Foreground = tone;
        MeetingsSortButton.Background = (Brush)Application.Current.Resources[sorted ? "MuesliAccentMutedBrush" : "MuesliHoverBrush"];

        HistoryFilterBar.StyleDatePill(MeetingsTimeRangeButton, MeetingsTimeRangeIcon, MeetingsTimeRangeLabel,
            ViewModel.IsAllTimeRange ? null : ViewModel.TimeRangeLabel);
    }

    private void MeetingList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MeetingListItem meeting) Frame.Navigate(typeof(MeetingDetailPage), meeting.Id);
    }

    private void OpenNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ActiveMeetingId is { } id) Frame.Navigate(typeof(MeetingDetailPage), id);
    }

    /// <summary>macOS startQuickNoteMeeting opens the new meeting's document.</summary>
    private void OnQuickNoteStarted(object? sender, EventArgs e) => OpenNotes_Click(this, new RoutedEventArgs());

    private void ManageTemplates_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(MeetingTemplatesPage));

    // macOS shows the row's delete control only while the pointer is over the row.
    private void MeetingRow_PointerEntered(object sender, PointerRoutedEventArgs e) => SetDeleteVisible(sender, 1);
    private void MeetingRow_PointerExited(object sender, PointerRoutedEventArgs e) => SetDeleteVisible(sender, 0);

    private static void SetDeleteVisible(object row, double opacity)
    {
        if (row is not DependencyObject root) return;
        foreach (var button in Descendants(root).OfType<Button>())
        {
            if (AutomationProperties.GetAutomationId(button) == "MeetingRowDeleteButton") button.Opacity = opacity;
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private async void DeleteMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) await ViewModel.DeleteMeetingAsync(id);
    }

    /// <summary>MeetingListItemView folder popover.</summary>
    private void MoveMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button) return;
        MeetingFolderMenu.Show(button, ViewModel.Items.FirstOrDefault(item => item.Id == id)?.FolderId, includeUnfiled: true,
            target => ViewModel.MoveMeeting(id, target), name => ViewModel.CreateFolderAndMove(id, name));
    }
}
