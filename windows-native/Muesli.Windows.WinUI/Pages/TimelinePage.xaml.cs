using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.Services;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class TimelinePage : Page
{
    /// <summary>
    /// The inner day list that currently owns the selection. Each day renders its own
    /// <see cref="ListView"/>, so selecting a row in one day has to clear the previous day's
    /// selection or two rows would look selected at once.
    /// </summary>
    private ListView? _selectedRowList;

    public TimelinePageViewModel ViewModel { get; } = new(App.Library, App.Clipboard, App.Dialogs);

    public TimelinePage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, _) =>
        {
            UpdateEmptyState();
            UpdateFilterPills();
        };
        Loaded += (_, _) =>
        {
            UpdateEmptyState();
            UpdateFilterPills();
        };
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>macOS StatsHeaderView prints large word counts as "20.8k".</summary>
    public static string CompactCount(int count) =>
        count >= 1000 ? $"{count / 1000.0:0.0}k" : count.ToString();

    /// <summary>macOS TimelineMeetingRow duration: "40s", "3m 5s", "1h 12m".</summary>
    public static string MeetingDuration(int durationMs)
    {
        var seconds = Math.Max(0, (int)Math.Round(durationMs / 1000.0));
        if (seconds >= 3600) return $"{seconds / 3600}h {seconds % 3600 / 60}m";
        if (seconds >= 60) return seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds / 60}m {seconds % 60}s";
        return $"{seconds}s";
    }

    /// <summary>Notes as plain text, as macOS MeetingPreviewText does, instead of raw markdown.</summary>
    public static string MeetingPreview(string detail)
    {
        var snippet = MeetingPreviewText.Snippet(detail);
        return snippet == "No notes yet" ? "No transcript or notes yet" : snippet;
    }

    /// <summary>macOS MeetingStatusDisplay colours: recording red, processing accent, completed green.</summary>
    public static Brush StatusBrush(string status) => (Brush)Application.Current.Resources[status.ToLowerInvariant() switch
    {
        var s when s.Contains("record") => "MuesliErrorBrush",
        var s when s.Contains("process") || s.Contains("transcrib") => "MuesliAccentBrush",
        var s when s.Contains("fail") || s.Contains("attention") => "MuesliWarningBrush",
        var s when s.Contains("complete") => "MuesliSuccessBrush",
        _ => "MuesliTextTertiaryBrush"
    }];

    /// <summary>The status colour at 12%, the capsule's fill.</summary>
    public static Brush StatusTint(string status)
    {
        var color = StatusBrush(status) is SolidColorBrush solid ? solid.Color : Microsoft.UI.Colors.Gray;
        return new SolidColorBrush(global::Windows.UI.Color.FromArgb(0x1F, color.R, color.G, color.B));
    }

    /// <summary>macOS StatsHeaderView.onSelect: each card opens its Insights section.</summary>
    private void StatCard_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.OpenInsights((sender as FrameworkElement)?.Tag as string ?? "words", "timeline");

    private void DateFilterMenu_Opening(object sender, object e) =>
        HistoryFilterBar.FillDateMenu(DateFilterMenu, HistoryFormat.DateFilters, ViewModel.DateFilterIndex, index => ViewModel.DateFilterIndex = index);

    private void AppFilterMenu_Opening(object sender, object e) =>
        HistoryFilterBar.FillAppMenu(AppFilterMenu, ViewModel.Apps, ViewModel.AppFilter, app => ViewModel.AppFilter = app);

    private void UpdateFilterPills()
    {
        HistoryFilterBar.StyleDatePill(DateFilterButton, DateFilterIcon, DateFilterLabel,
            ViewModel.DateFilterIndex > 0 ? HistoryFormat.DateFilters[ViewModel.DateFilterIndex] : null);
        HistoryFilterBar.StyleAppPill(AppFilterButton, AppFilterIcon, AppFilterGlyph, AppFilterLabel, ViewModel.AppFilter);
    }

    private void ToggleText_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } entry) entry.Body.IsExpanded = !entry.Body.IsExpanded;
    }

    private void UpdateEmptyState()
    {
        // A refresh or filter change rebuilds every inner list, so the tracked selection owner is
        // no longer part of the tree.
        _selectedRowList = null;

        EmptyState.Visibility = ViewModel.HasEntries ? Visibility.Collapsed : Visibility.Visible;
        TimelineList.Visibility = ViewModel.HasEntries ? Visibility.Visible : Visibility.Collapsed;

        // macOS TimelineView.emptyState.
        EmptyStateTitle.Text = ViewModel.EmptyStateTitle;
    }

    /// <summary>
    /// P3-01-class defect inside this surface: without this the row's accessible name is the
    /// record's <c>ToString()</c> debug dump. Screen readers announce the time, the kind and the
    /// title instead.
    /// </summary>
    private void TimelineRow_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not WinUiTimelineEntry entry)
        {
            return;
        }

        var container = args.ItemContainer;
        container.PointerEntered -= Row_Reveal;
        container.PointerExited -= Row_Hide;
        if (!args.InRecycleQueue)
        {
            container.PointerEntered += Row_Reveal;
            container.PointerExited += Row_Hide;
        }
        SetRowActionsVisible(container, entry.HasRun);

        var detail = entry.HasDetail ? ". " + entry.DetailLabel : "";
        AutomationProperties.SetName(
            args.ItemContainer,
            $"{entry.KindLabel} at {entry.TimeLabel}. {entry.TitleLabel}{detail}");
    }

    /// <summary>
    /// P2-04: the timeline was inert — <c>SelectionMode="None"</c> with
    /// <c>IsItemClickEnabled="False"</c>, so a meeting could not be opened from the page macOS
    /// opens it from. Meetings open their detail page by id; dictations open the Dictations page
    /// scrolled to and selecting that same record, which is the closest Windows equivalent of the
    /// macOS row expansion.
    /// </summary>
    private void TimelineRow_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not WinUiTimelineEntry entry)
        {
            return;
        }

        // macOS TimelineView rows are DictationRowView: a Computer Use row opens its steps and any
        // other dictation is copied. Only meetings open a page.
        if (!entry.IsMeeting)
        {
            if (entry.Run is { } run) run.IsExpanded = !run.IsExpanded;
            else _ = ViewModel.CopyCommand.ExecuteAsync(entry);
            return;
        }

        if (sender is ListView list)
        {
            if (_selectedRowList is not null && !ReferenceEquals(_selectedRowList, list))
            {
                _selectedRowList.SelectedIndex = -1;
            }

            _selectedRowList = list;
        }

        // The rail follows the content: opening a meeting from here lands on the meeting detail
        // page, whose own back link says "Back to Meetings", so leaving "Timeline" highlighted
        // would point at a route that is no longer on screen.
        (App.Window as MainWindow)?.ShellPage?.SyncRailSelection(entry.IsMeeting ? "meetings" : "dictations");
        Frame?.Navigate(
            entry.IsMeeting ? typeof(MeetingDetailPage) : typeof(DictationsPage),
            entry.Id);
    }

    private void Row_Reveal(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ContentControl container) SetRowActionsVisible(container, true);
    }

    private void Row_Hide(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is ContentControl container) SetRowActionsVisible(container, false);
    }

    /// <summary>Hover-revealed like Dictations; a Computer Use row keeps its actions visible.</summary>
    private static void SetRowActionsVisible(ContentControl container, bool visible)
    {
        if ((container.ContentTemplateRoot as FrameworkElement)?.FindName("RowActions") is not FrameworkElement actions) return;
        visible |= container.Content is WinUiTimelineEntry { HasRun: true };
        actions.Opacity = visible ? 1 : 0;
        actions.IsHitTestVisible = visible;
    }

    private static WinUiTimelineEntry? Tagged(object sender) => (sender as FrameworkElement)?.Tag as WinUiTimelineEntry;

    private void ToggleRun_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender)?.Run is { } run) run.IsExpanded = !run.IsExpanded;
    }

    private async void CopyTrace_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } entry) await ViewModel.CopyTraceCommand.ExecuteAsync(entry);
    }

    private async void CopyDictation_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } entry) await ViewModel.CopyCommand.ExecuteAsync(entry);
    }

    private async void DeleteDictation_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } entry) await ViewModel.DeleteCommand.ExecuteAsync(entry);
    }

    /// <summary>Prompt 9: real accessible names for record-backed list containers (<see cref="ListItemAutomationNames"/>).</summary>
    private void NamedListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ListItemAutomationNames.Apply(args);
}
