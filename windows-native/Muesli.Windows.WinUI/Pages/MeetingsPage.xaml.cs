using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class MeetingsPage : Page
{
    /// <summary>Set while this page pushes a selection into the folder list, so restoring the
    /// selection after a reload does not re-enter <see cref="ViewModel"/>'s folder filter.</summary>
    private bool _syncingFolderSelection;

    public MeetingsPageViewModel ViewModel { get; } = new(
        App.Library,
        App.Meetings,
        App.MeetingDetection,
        App.FilePickers,
        App.Dialogs,
        App.UiDispatcher,
        App.ShowLiveTranscript);

    public MeetingsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
        ViewModel.PropertyChanged += (_, args) =>
        {
            UpdateEmptyState();

            // A reload republishes the folder list, which drops the ListView's SelectedItem because
            // it is a different instance. Without this, refreshing or saving a folder silently
            // returned the in-page list to "All Meetings" while the page kept filtering by the
            // folder the user had chosen.
            if (args.PropertyName is nameof(ViewModel.Folders) or nameof(ViewModel.SelectedFolderId))
            {
                RestoreFolderSelection();
                SyncShellFolders();
            }

            if (args.PropertyName is nameof(ViewModel.SortIndex) or nameof(ViewModel.TimeRangeIndex))
            {
                UpdateSortPill();
                UpdateTimeRangePill();
            }

            if (args.PropertyName is nameof(ViewModel.TimeRangeOptions))
            {
                UpdateTimeRangePill();
            }
        };
        Loaded += OnLoaded;
        SizeChanged += OnPageSizeChanged;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateEmptyState();
        UpdateSortPill();
        UpdateTimeRangePill();
        ApplyResponsiveLayout(RootGrid.ActualWidth);
    }

    /// <summary>
    /// The shell already owns the rail breakpoints. This page follows content width: gutters use
    /// the frozen page tokens, the action row stacks when it cannot sit on one line, and the
    /// folder editor becomes a single column so the list and name field are not clipped.
    /// </summary>
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        // Shared thresholds only (App.xaml:29-42). Below 900 DIP of content the action row cannot
        // hold the folder name plus five controls on one line, which is the same band the shared
        // gutter calls compact-or-medium — the page does not invent a breakpoint of its own.
        var compact = MuesliPageMetrics.IsCompact(width) || MuesliPageMetrics.IsMedium(width);
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);

        HeaderActions.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;

        if (compact)
        {
            // The folder name keeps the first row; the actions drop underneath it.
            Grid.SetRow(HeaderActions, 1);
            Grid.SetColumn(HeaderActions, 0);
            Grid.SetColumnSpan(HeaderActions, 2);
            HeaderActions.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(SearchWell, 1);
            Grid.SetColumn(SearchWell, 0);
            Grid.SetColumnSpan(SearchWell, 2);

            FolderListColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(FolderEditorFields, 1);
            Grid.SetColumn(FolderEditorFields, 0);

            Grid.SetRow(ActiveMeetingActions, 1);
            Grid.SetColumn(ActiveMeetingActions, 0);
            Grid.SetColumnSpan(ActiveMeetingActions, 2);
            ActiveMeetingActions.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(ActiveMeetingDetails, 2);
        }
        else
        {
            Grid.SetRow(HeaderActions, 0);
            Grid.SetColumn(HeaderActions, 1);
            Grid.SetColumnSpan(HeaderActions, 1);
            HeaderActions.HorizontalAlignment = HorizontalAlignment.Right;

            Grid.SetRow(SearchWell, 0);
            Grid.SetColumn(SearchWell, 1);
            Grid.SetColumnSpan(SearchWell, 1);

            FolderListColumn.Width = new GridLength(220);
            Grid.SetRow(FolderEditorFields, 0);
            Grid.SetColumn(FolderEditorFields, 1);

            Grid.SetRow(ActiveMeetingActions, 0);
            Grid.SetColumn(ActiveMeetingActions, 1);
            Grid.SetColumnSpan(ActiveMeetingActions, 1);
            ActiveMeetingActions.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(ActiveMeetingDetails, 1);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var tag = e.Parameter as string ?? "meetings";
        if (tag.StartsWith("meetings:", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.SelectFolder(tag["meetings:".Length..]);
        }
        else
        {
            ViewModel.SelectFolder("");
        }
        RestoreFolderSelection();
        UpdateEmptyState();
    }

    /// <summary>
    /// Points the in-page folder list at whichever folder the view model is actually filtering by.
    /// Called after navigation and after every reload so the sidebar, the page subtitle, the folder
    /// list selection and the filtered meetings always name the same folder.
    /// </summary>
    private void RestoreFolderSelection()
    {
        var match = ViewModel.Folders.FirstOrDefault(folder =>
            string.Equals(folder.Id, ViewModel.SelectedFolderId, StringComparison.Ordinal));
        if (ReferenceEquals(MeetingFolderList.SelectedItem, match))
        {
            return;
        }

        _syncingFolderSelection = true;
        try
        {
            MeetingFolderList.SelectedItem = match;
        }
        finally
        {
            _syncingFolderSelection = false;
        }
    }

    /// <summary>
    /// Republishes the shell rail's folder rows and highlight from this page's current state, so a
    /// folder created, renamed, moved or deleted here is reflected in the sidebar immediately and
    /// both surfaces name the same selected folder.
    /// </summary>
    private void SyncShellFolders() =>
        (App.Window as MainWindow)?.ShellPage?.RefreshMeetingFolders(
            ViewModel.SelectedFolderId.Length == 0 ? "meetings" : $"meetings:{ViewModel.SelectedFolderId}");

    /// <summary>
    /// P3-01. Without an explicit name the <c>ListViewItem</c> peer falls back to the data record's
    /// text, so a screen reader read the whole record dump. Names are reset on recycle for the same
    /// reason the Search page resets its row handlers.
    /// </summary>
    private void MeetingList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MeetingListItem meeting)
        {
            AutomationProperties.SetName(args.ItemContainer, string.Empty);
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, meeting.AccessibleName);
    }

    private void MeetingFolderList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MeetingFolderListItem folder)
        {
            AutomationProperties.SetName(args.ItemContainer, string.Empty);
            return;
        }

        AutomationProperties.SetName(args.ItemContainer, folder.AccessibleName);
    }

    /// <summary>Opens the folder editor and puts the caret in the name field, so the shell rail's
    /// "+" reaches a usable control rather than a collapsed expander (P3-02).</summary>
    public void RevealFolderEditor()
    {
        FolderEditorExpander.IsExpanded = true;
        ViewModel.NewFolderCommand.Execute(null);
        DispatcherQueue.TryEnqueue(() => MeetingFolderNameBox.Focus(FocusState.Programmatic));
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        MeetingList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        ActiveMeetingPanel.Visibility = ViewModel.IsActive || ViewModel.IsImporting
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecoveryPanel.Visibility = ViewModel.HasRecoverable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MeetingFolder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFolderSelection)
        {
            return;
        }

        // SelectFolder raises SelectedFolderId, which syncs the rail highlight through
        // SyncShellFolders — picking a folder here and picking it in the sidebar end in the
        // same shell state.
        if (sender is ListView { SelectedItem: MeetingFolderListItem folder })
        {
            ViewModel.SelectFolder(folder.Id);
        }
    }

    /// <summary>
    /// Compact sort pill matching MeetingsView.swift: the flyout lists both orders and checks the
    /// active one; the pill takes the accent tone only when the order is not the newest-first default.
    /// </summary>
    private void MeetingsSort_Click(object sender, RoutedEventArgs e)
    {
        MeetingsSortFlyout.Items.Clear();
        var sorts = new[] { "Newest first", "Oldest first" };
        for (var index = 0; index < sorts.Length; index++)
        {
            var target = index;
            var item = new ToggleMenuFlyoutItem
            {
                Text = sorts[index],
                IsChecked = ViewModel.SortIndex == target
            };
            AutomationProperties.SetAutomationId(item, target == 0 ? "MeetingsSortNewest" : "MeetingsSortOldest");
            AutomationProperties.SetName(item, sorts[index]);
            item.Click += (_, _) =>
            {
                ViewModel.SortIndex = target;
                UpdateSortPill();
            };
            MeetingsSortFlyout.Items.Add(item);
        }
    }

    /// <summary>
    /// Compact date-filter pill matching MeetingsView.swift: neutral (no label) at All time, accent
    /// with the range label when a narrower range is active. The flyout checks the active choice.
    /// </summary>
    private void MeetingsTimeRange_Click(object sender, RoutedEventArgs e)
    {
        MeetingsTimeRangeFlyout.Items.Clear();
        var options = ViewModel.TimeRangeOptions;
        for (var index = 0; index < options.Count; index++)
        {
            var target = index;
            var item = new ToggleMenuFlyoutItem
            {
                Text = options[index],
                IsChecked = ViewModel.TimeRangeIndex == target
            };
            AutomationProperties.SetAutomationId(item, $"MeetingsTimeRangeChoice_{target}");
            AutomationProperties.SetName(item, options[index]);
            item.Click += (_, _) =>
            {
                ViewModel.TimeRangeIndex = target;
                UpdateTimeRangePill();
            };
            MeetingsTimeRangeFlyout.Items.Add(item);
        }
    }

    private void UpdateSortPill()
    {
        MeetingsSortLabel.Text = ViewModel.SortLabel;
        ApplyPillTone(MeetingsSortButton, MeetingsSortIcon, MeetingsSortLabel, active: !ViewModel.IsDefaultSort);
    }

    private void UpdateTimeRangePill()
    {
        MeetingsTimeRangeLabel.Text = ViewModel.IsAllTimeRange ? "" : ViewModel.TimeRangeLabel;
        ApplyPillTone(MeetingsTimeRangeButton, MeetingsTimeRangeIcon, MeetingsTimeRangeLabel, active: !ViewModel.IsAllTimeRange);
    }

    /// <summary>
    /// Applies the macOS pill tones: neutral surfaces when the choice is the default, accent at 12%
    /// with an accent foreground when a non-default choice is active.
    /// </summary>
    private static void ApplyPillTone(Button button, FontIcon icon, TextBlock label, bool active)
    {
        if (active)
        {
            var accent = (Brush)Application.Current.Resources["MuesliAccentBrush"];
            button.Background = (Brush)Application.Current.Resources["MuesliAccentMutedBrush"];
            button.Foreground = accent;
            button.BorderBrush = accent;
            icon.Foreground = accent;
            label.Foreground = accent;
        }
        else
        {
            button.ClearValue(Control.BackgroundProperty);
            button.ClearValue(Control.ForegroundProperty);
            button.ClearValue(Control.BorderBrushProperty);
            icon.ClearValue(IconElement.ForegroundProperty);
            label.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private void MeetingList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MeetingListItem meeting)
        {
            Frame.Navigate(typeof(MeetingDetailPage), meeting.Id);
        }
    }

    private void ManageTemplates_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(MeetingTemplatesPage));
}
