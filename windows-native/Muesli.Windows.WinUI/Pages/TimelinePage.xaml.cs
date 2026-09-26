using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.Services;
using Windows.UI.Text;
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

    public TimelinePageViewModel ViewModel { get; } = new(App.Library);

    public TimelinePage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, _) => UpdateEmptyState();
        Loaded += (_, _) => UpdateEmptyState();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Meeting titles are named things and read bold; a dictation row is its own text.</summary>
    public static FontWeight TitleWeight(bool isMeeting) =>
        isMeeting ? FontWeights.SemiBold : FontWeights.Normal;

    public static FontFamily TitleFont(bool isMeeting) =>
        (FontFamily)Application.Current.Resources[isMeeting ? "MuesliFontFamilySemiBold" : "MuesliFontFamily"];

    private void UpdateEmptyState()
    {
        // A refresh or filter change rebuilds every inner list, so the tracked selection owner is
        // no longer part of the tree.
        _selectedRowList = null;

        EmptyState.Visibility = ViewModel.HasEntries ? Visibility.Collapsed : Visibility.Visible;
        TimelineList.Visibility = ViewModel.HasEntries ? Visibility.Visible : Visibility.Collapsed;

        EmptyStateTitle.Text = "No timeline activity yet";
        EmptyStateBody.Text = "Saved dictations and completed meetings will appear here.";
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

    /// <summary>Prompt 9: real accessible names for record-backed list containers (<see cref="ListItemAutomationNames"/>).</summary>
    private void NamedListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ListItemAutomationNames.Apply(args);
}
