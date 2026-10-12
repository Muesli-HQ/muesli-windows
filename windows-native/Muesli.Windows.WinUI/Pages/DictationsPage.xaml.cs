using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class DictationsPage : Page
{
    public DictationsPageViewModel ViewModel { get; } = new(
        App.Library,
        App.Clipboard,
        App.Dialogs,
        App.Dictation,
        App.UiDispatcher);

    private bool _compact;

    /// <summary>Dictation id handed over by a Timeline row click, revealed once the list is built.</summary>
    private string? _pendingRevealId;

    /// <summary>The inner day list that currently owns the row selection.</summary>
    private ListView? _selectedRowList;

    public DictationsPage()
    {
        InitializeComponent();
        // No Refresh button (macOS has none): the list reloads when a Computer Use run lands.
        Unloaded += (_, _) =>
        {
            ViewModel.Dispose();
            App.ComputerUse.Changed -= OnComputerUseChanged;
        };
        ViewModel.PropertyChanged += (_, _) =>
        {
            UpdateEmptyState();
            UpdateFilterPills();
        };
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            App.ComputerUse.Changed += OnComputerUseChanged;
            UpdateEmptyState();
            UpdateFilterPills();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    private void OnComputerUseChanged(object? sender, EventArgs e) => ViewModel.Refresh();

    /// <summary>Builds the next page of older days once the reader is within a screen of the end.</summary>
    private void PageScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is ScrollViewer scroller && ViewModel.Groups.HasMore &&
            scroller.VerticalOffset >= scroller.ScrollableHeight - scroller.ViewportHeight)
            ViewModel.Groups.ShowMore();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>macOS StatsHeaderView prints large word counts as "20.8k".</summary>
    public static string CompactCount(string label) =>
        int.TryParse(label, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.CurrentCulture, out var count)
            ? TimelinePage.CompactCount(count)
            : label;

    /// <summary>macOS voiceNoteButton: accent at rest, the recording red while capturing.</summary>
    public static Microsoft.UI.Xaml.Media.Brush RecordBackground(bool isRecording) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[isRecording ? "MuesliErrorBrush" : "MuesliAccentBrush"];

    public static string RecordGlyph(bool isRecording) => isRecording ? "" : "";

    /// <summary>macOS StatsHeaderView.onSelect: each card opens its Insights section.</summary>
    private void StatCard_Click(object sender, RoutedEventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.OpenInsights((sender as FrameworkElement)?.Tag as string ?? "words", "dictations");

    private void DateFilterMenu_Opening(object sender, object e) =>
        HistoryFilterBar.FillDateMenu(DateFilterMenu, ViewModel.DateFilters, ViewModel.DateFilterIndex, index => ViewModel.DateFilterIndex = index);

    private void AppFilterMenu_Opening(object sender, object e) =>
        HistoryFilterBar.FillAppMenu(AppFilterMenu, ViewModel.Apps, ViewModel.AppFilter, app => ViewModel.AppFilter = app);

    private void UpdateFilterPills()
    {
        HistoryFilterBar.StyleDatePill(DateFilterButton, DateFilterIcon, DateFilterLabel,
            ViewModel.DateFilterIndex > 0 && ViewModel.DateFilterIndex < ViewModel.DateFilters.Count ? ViewModel.DateFilters[ViewModel.DateFilterIndex] : null);
        HistoryFilterBar.StyleAppPill(AppFilterButton, AppFilterIcon, AppFilterGlyph, AppFilterLabel, ViewModel.AppFilter);
    }

    private void ToggleText_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } item) item.Body.IsExpanded = !item.Body.IsExpanded;
    }

    /// <summary>
    /// P2-04: the shell passes its nav tag ("dictations") when the rail navigates here, and a
    /// dictation id when a Timeline row is activated. Only an id that actually exists in the
    /// loaded history is treated as a reveal request, so an unknown parameter can never leave the
    /// page pointing at the wrong record.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var id = e.Parameter as string;
        _pendingRevealId = ViewModel.Contains(id) ? id : null;
        // An older dictation's day may not be built yet.
        if (_pendingRevealId is { } reveal)
            ViewModel.Groups.ShowThrough(group => group.Items.Any(item => item.Id == reveal));
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        DictationList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

        // The list is rebuilt on every reload, so the tracked selection owner is stale.
        _selectedRowList = null;

        // macOS DictationsView empty state: the filter hint, else the push-to-talk prompt.
        if (ViewModel.HasItems) return;
        EmptyStateTitle.Text = ViewModel.EmptyStateTitle;
        var hotkey = App.Settings.Load().Hotkey;
        EmptyStateBody.Text = ViewModel.IsFiltered
            ? "Try another source, app, or time range"
            : $"Hold {(string.IsNullOrWhiteSpace(hotkey) ? "your shortcut" : hotkey)} to dictate";
    }

    /// <summary>
    /// Below ~700 DIP of page width (the 720-DIP window with the compact rail) the header runs
    /// out of slack, so the page falls back to the shared compact gutter. The gutter itself is
    /// never hand-built here — <see cref="MuesliPageMetrics.ContentPadding"/> owns the three tiers.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        var compact = MuesliPageMetrics.IsCompact(width);
        if (compact == _compact && ContentStack.Padding.Left > 0)
        {
            return;
        }

        _compact = compact;
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);
    }

    /// <summary>
    /// Wires the hover / focus reveal for one row's actions (P2-08) and replaces the record's
    /// <c>ToString()</c> with a readable accessible name. Handlers are attached to the container
    /// rather than to the template root so that arrow-key selection — which focuses the
    /// <see cref="ListViewItem"/> itself, not its content — also reveals the actions.
    /// </summary>
    private void DictationRow_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = args.ItemContainer;
        container.PointerEntered -= Row_Reveal;
        container.PointerExited -= Row_Hide;
        container.GotFocus -= Row_Reveal;
        container.LostFocus -= Row_Hide;

        if (args.InRecycleQueue || args.Item is not DictationListItem item)
        {
            return;
        }

        container.PointerEntered += Row_Reveal;
        container.PointerExited += Row_Hide;
        container.GotFocus += Row_Reveal;
        container.LostFocus += Row_Hide;
        SetRowActionsVisible(container, item.HasRun);

        var duration = string.IsNullOrEmpty(item.DurationLabel) ? "" : $", {item.DurationLabel}";
        AutomationProperties.SetName(container, $"{item.TimeLabel}{duration}. {item.Text}");

        if (_pendingRevealId is { } pending &&
            string.Equals(item.Id, pending, StringComparison.Ordinal) &&
            sender is ListView list)
        {
            _pendingRevealId = null;
            _selectedRowList = list;
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            container.StartBringIntoView();
        }
    }

    private void Row_Reveal(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl container)
        {
            SetRowActionsVisible(container, true);
        }
    }

    /// <summary>
    /// Hides the row actions again, but never while the row still holds keyboard focus: moving
    /// focus from the container into one of its own buttons raises <c>LostFocus</c> on the
    /// container first, and hiding there would pull the focused button out from under the user.
    /// </summary>
    private void Row_Hide(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl container && !ContainsFocus(container))
        {
            SetRowActionsVisible(container, false);
        }
    }

    private bool ContainsFocus(DependencyObject container)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, container))
            {
                return true;
            }

            focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private static void SetRowActionsVisible(ContentControl container, bool visible)
    {
        if ((container.ContentTemplateRoot as FrameworkElement)?.FindName("RowActions") is not FrameworkElement actions)
        {
            return;
        }

        // macOS keeps the actions of a row with expandable details visible.
        visible |= container.Content is DictationListItem { HasRun: true };
        actions.Opacity = visible ? 1 : 0;
        actions.IsHitTestVisible = visible;
    }

    private async void CopyDictation_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } item)
        {
            await ViewModel.CopyCommand.ExecuteAsync(item);
        }
    }

    private async void CopyTrace_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } item)
        {
            await ViewModel.CopyTraceCommand.ExecuteAsync(item);
        }
    }

    private void ToggleRun_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender)?.Run is { } run) run.IsExpanded = !run.IsExpanded;
    }

    /// <summary>macOS DictationRowView: tapping a Computer Use row opens its steps; tapping any
    /// other dictation copies it.</summary>
    private async void Row_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (ComputerUseRowStyle.IsOwnTap(e.OriginalSource, sender) ||
            (sender as FrameworkElement)?.DataContext is not DictationListItem item) return;
        if (item.Run is { } run) run.IsExpanded = !run.IsExpanded;
        else await ViewModel.CopyCommand.ExecuteAsync(item);
    }

    private async void DeleteDictation_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } item)
        {
            await ViewModel.DeleteCommand.ExecuteAsync(item);
        }
    }

    private static DictationListItem? Tagged(object sender) =>
        sender is FrameworkElement element
            ? element.Tag as DictationListItem ?? element.DataContext as DictationListItem
            : null;

    /// <summary>Prompt 9: real accessible names for record-backed list containers (<see cref="ListItemAutomationNames"/>).</summary>
    private void NamedListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ListItemAutomationNames.Apply(args);
}
