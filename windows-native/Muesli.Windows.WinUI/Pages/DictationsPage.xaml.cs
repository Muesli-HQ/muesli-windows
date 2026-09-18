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
        Unloaded += (_, _) => ViewModel.Dispose();
        ViewModel.PropertyChanged += (_, _) => UpdateEmptyState();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            UpdateEmptyState();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

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
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        DictationList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

        // The list is rebuilt on every reload, so the tracked selection owner is stale.
        _selectedRowList = null;

        EmptyStateTitle.Text = "No dictations yet";
        EmptyStateBody.Text = "Your first completed dictation will be stored here.";
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
        SetRowActionsVisible(container, false);

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
