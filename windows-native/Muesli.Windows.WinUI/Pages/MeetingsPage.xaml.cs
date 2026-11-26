using Muesli.Windows.WinUI.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
        App.UiDispatcher);

    public MeetingsPage()
    {
        InitializeComponent();
        // Code-set colours and item templates resolve brushes once; redo them on a theme switch.
        ActualThemeChanged += (_, _) =>
        {
            UpdateSortPill();
            UpdateTimeRangePill();
            ThemeBrushes.Rerealize(MeetingList);
        };
        Unloaded += (_, _) => ViewModel.Dispose();
        ViewModel.PropertyChanged += (_, args) =>
        {
            UpdateEmptyState();

            // Folders are managed in the shell rail. Keep its rows and highlight in step with the
            // folder this page is filtering by.
            if (args.PropertyName is nameof(ViewModel.Folders) or nameof(ViewModel.SelectedFolderId))
            {
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

    public static Visibility NotToVisibility(bool value) => BoolToVisibility(!value);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateEmptyState();
        UpdateSortPill();
        UpdateTimeRangePill();
        ApplyResponsiveLayout(RootGrid.ActualWidth);
    }

    /// <summary>
    /// The shell already owns the rail breakpoints. This page follows content width: gutters use
    /// the frozen page tokens, and the action row stacks when it cannot sit on one line.
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

            Grid.SetRow(ActiveMeetingActions, 1);
            Grid.SetColumn(ActiveMeetingActions, 1);
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

            Grid.SetRow(ActiveMeetingActions, 0);
            Grid.SetColumn(ActiveMeetingActions, 2);
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
        UpdateEmptyState();
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

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        MeetingList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        ActiveMeetingPanel.Visibility = ViewModel.IsActive || ViewModel.IsImporting
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecoveryPanel.Visibility = ViewModel.HasRecoverable ? Visibility.Visible : Visibility.Collapsed;
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
            // macOS: accent at 12% with accent text, no border.
            var accentText = ThemeBrushes.Get("MuesliAccentTextBrush");
            button.Background = ThemeBrushes.Get("MuesliAccentMutedBrush");
            button.Foreground = accentText;
            icon.Foreground = accentText;
            label.Foreground = accentText;
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

    /// <summary>macOS source badge: "Imported" in accent text on accent 12%, others textSecondary at 12%.</summary>
    public static Brush SourceTint(string label)
    {
        var imported = label.Contains("import", StringComparison.OrdinalIgnoreCase);
        var key = imported ? "MuesliAccentBrush" : "MuesliTextSecondaryBrush";
        var color = ThemeBrushes.Get(key) is SolidColorBrush solid ? solid.Color : Microsoft.UI.Colors.Gray;
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x1F, color.R, color.G, color.B));
    }

    public static Brush SourceForeground(string label) => ThemeBrushes.Get(
        label.Contains("import", StringComparison.OrdinalIgnoreCase) ? "MuesliAccentTextBrush" : "MuesliTextSecondaryBrush");

    private void MeetingList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MeetingListItem meeting)
        {
            if (meeting.Id == App.Meetings.ActiveMeetingId)
            {
                OpenRecording();
                return;
            }
            Frame.Navigate(typeof(MeetingDetailPage), meeting.Id);
        }
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e) => OpenRecording();

    /// <summary>Opens the meeting in progress on its own page.</summary>
    private void OpenRecording() => Frame.Navigate(typeof(MeetingRecordingPage));

    private void ManageTemplates_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(MeetingTemplatesPage));
}
