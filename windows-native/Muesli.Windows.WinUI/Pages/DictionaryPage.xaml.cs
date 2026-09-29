using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class DictionaryPage : Page
{
    public DictionaryPageViewModel ViewModel { get; } = new(App.Library, App.Settings, App.FilePickers, App.Dialogs);

    private bool _columnsStacked;
    private bool _headerCompact;

    public DictionaryPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, _) => UpdateState();
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ApplyResponsiveLayout(ActualWidth);
            UpdateState();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void DictionaryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is DictionaryListItem item) ViewModel.Select(item);
    }

    private void UpdateState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        DictionaryList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        SuggestionPanel.Visibility = ViewModel.HasSuggestions ? Visibility.Visible : Visibility.Collapsed;
        PaginationBar.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

        if (string.IsNullOrWhiteSpace(ViewModel.EditingId))
        {
            DictionaryList.SelectedItem = null;
        }
        else
        {
            DictionaryList.SelectedItem = ViewModel.Items.FirstOrDefault(item =>
                string.Equals(item.Id, ViewModel.EditingId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Compact (&lt;760 DIP of page width) is the 720-wide window with the icon rail: stack the
    /// header commands and place the editor under the list. Medium/large keep title + actions on
    /// one row and the list/editor side by side.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var headerCompact = width < 900;
        var compact = width < 760;
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);

        HeaderBar.Orientation = headerCompact ? Orientation.Vertical : Orientation.Horizontal;
        HeaderBar.HorizontalAlignment = headerCompact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        HeaderCommands.HorizontalAlignment = headerCompact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TitleRow.ColumnSpacing = headerCompact ? 0 : 16;

        if (headerCompact)
        {
            Grid.SetRow(HeaderBar, 2);
            Grid.SetColumn(HeaderBar, 0);
            Grid.SetColumnSpan(HeaderBar, 2);
        }
        else
        {
            Grid.SetRow(HeaderBar, 0);
            Grid.SetColumn(HeaderBar, 1);
            Grid.SetColumnSpan(HeaderBar, 1);
        }

        _headerCompact = headerCompact;
        EditorActions.Orientation = compact && width < 520 ? Orientation.Vertical : Orientation.Horizontal;

        if (compact == _columnsStacked && DictionaryColumns.ColumnDefinitions.Count > 0)
        {
            return;
        }

        _columnsStacked = compact;
        DictionaryColumns.ColumnDefinitions.Clear();
        if (compact)
        {
            DictionaryColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(DictionaryListCard, 0);
            Grid.SetRow(DictionaryListCard, 0);
            Grid.SetColumn(DictionaryEditorPanel, 0);
            Grid.SetRow(DictionaryEditorPanel, 1);
        }
        else
        {
            DictionaryColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.22, GridUnitType.Star) });
            DictionaryColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(DictionaryListCard, 0);
            Grid.SetRow(DictionaryListCard, 0);
            Grid.SetColumn(DictionaryEditorPanel, 1);
            Grid.SetRow(DictionaryEditorPanel, 0);
        }
    }

    /// <summary>Prompt 9: real accessible names for record-backed list containers (<see cref="ListItemAutomationNames"/>).</summary>
    private void NamedListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ListItemAutomationNames.Apply(args);
}
