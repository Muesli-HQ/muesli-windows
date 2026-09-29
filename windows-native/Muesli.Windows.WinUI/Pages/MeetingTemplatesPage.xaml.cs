using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class MeetingTemplatesPage : Page
{
    public MeetingTemplatesViewModel ViewModel { get; } = new(App.Library, App.Dialogs);

    private bool _columnsStacked;

    public MeetingTemplatesPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsStatusError)
                or nameof(ViewModel.IsStatusOpen)
                or nameof(ViewModel.StatusMessage)
                or null)
            {
                StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            }

            if (args.PropertyName is nameof(ViewModel.HasItems) or nameof(ViewModel.Templates) or null)
            {
                UpdateEmptyState();
                ApplyResponsiveLayout(ActualWidth);
            }
        };
        Loaded += (_, _) =>
        {
            ViewModel.Load(selectFirstIfNone: true);
            ApplyResponsiveLayout(ActualWidth);
            UpdateEmptyState();
            StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(MeetingsPage));
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        NameBox.Focus(FocusState.Programmatic);
    }

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        PromptBox.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void TemplateList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || !ViewModel.DeleteCommand.CanExecute(null)) return;
        ViewModel.DeleteCommand.Execute(null);
        e.Handled = true;
    }

    private void TemplateList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is null || args.Item is not PersistedMeetingTemplate template) return;
        var name = string.IsNullOrWhiteSpace(template.Name) ? "Untitled template" : template.Name.Trim();
        AutomationProperties.SetName(args.ItemContainer, name);
        var prompt = template.Prompt.ReplaceLineEndings(" ").Trim();
        if (prompt.Length > 160) prompt = prompt[..157] + "…";
        if (prompt.Length > 0) AutomationProperties.SetFullDescription(args.ItemContainer, prompt);
    }

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SaveCommand.CanExecute(null)) ViewModel.SaveCommand.Execute(null);
        args.Handled = true;
    }

    private void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.NewTemplateCommand.CanExecute(null)) ViewModel.NewTemplateCommand.Execute(null);
        NameBox.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CancelCommand.CanExecute(null)) return;
        ViewModel.CancelCommand.Execute(null);
        args.Handled = true;
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = ViewModel.HasItems ? Visibility.Collapsed : Visibility.Visible;
        TemplateList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Compact uses the 720-wide window (icon rail, page width typically ~650–700 DIP): stack the
    /// list above the editor. 1008-wide windows keep an expanded sidebar, so page width is still
    /// above this threshold and stays master-detail. The editor pane is starred so Save/Delete
    /// stay on-screen instead of clipping below the fold. The tier comes from
    /// <see cref="MuesliPageMetrics"/> rather than a private 720 threshold (P1-04).
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var compact = MuesliPageMetrics.IsCompact(width);
        var headerCompact = compact;
        ContentRoot.Padding = MuesliPageMetrics.ContentPadding(width);

        NewTemplateButton.HorizontalAlignment = headerCompact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TitleRow.ColumnSpacing = headerCompact ? 0 : 16;
        if (headerCompact)
        {
            Grid.SetRow(NewTemplateButton, 2);
            Grid.SetColumn(NewTemplateButton, 0);
            Grid.SetColumnSpan(NewTemplateButton, 2);
        }
        else
        {
            Grid.SetRow(NewTemplateButton, 0);
            Grid.SetColumn(NewTemplateButton, 1);
            Grid.SetColumnSpan(NewTemplateButton, 1);
        }

        EditorActions.Orientation = compact && width < 560 ? Orientation.Vertical : Orientation.Horizontal;
        EditorActions.HorizontalAlignment = EditorActions.Orientation == Orientation.Vertical
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        PromptBox.MinHeight = compact ? 96 : 180;

        // Wide layouts are master-detail and fill the page exactly, so the page itself must not
        // scroll. Stacked layouts cannot: at 720x720 the list, the editor header, the name field
        // and the instructions box do not fit, and forcing them to made the instructions box —
        // the field the page exists for — collapse behind the action row. Compact therefore lets
        // the content take its natural height and gives the page a scrollbar.
        PageScroller.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        PageScroller.VerticalScrollMode = compact ? ScrollMode.Enabled : ScrollMode.Disabled;
        ContentRoot.MinHeight = compact ? 0 : Math.Max(0, PageScroller.ActualHeight);
        ContentRoot.RowDefinitions[2].Height = compact
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
        ApplyListCardHeights(compact);

        if (compact == _columnsStacked && TemplateColumns.ColumnDefinitions.Count > 0)
        {
            return;
        }

        _columnsStacked = compact;
        TemplateColumns.ColumnDefinitions.Clear();
        TemplateColumns.RowDefinitions.Clear();
        if (compact)
        {
            TemplateColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TemplateColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Auto, not star: the editor takes the height it needs and the page scrolls to it.
            TemplateColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(TemplateListCard, 0);
            Grid.SetRow(TemplateListCard, 0);
            Grid.SetColumnSpan(TemplateListCard, 1);
            Grid.SetColumn(TemplateEditorCard, 0);
            Grid.SetRow(TemplateEditorCard, 1);
            Grid.SetColumnSpan(TemplateEditorCard, 1);
            TemplateListCard.VerticalAlignment = VerticalAlignment.Top;
            TemplateEditorCard.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            TemplateColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            TemplateColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TemplateColumns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(TemplateListCard, 0);
            Grid.SetRow(TemplateListCard, 0);
            Grid.SetColumnSpan(TemplateListCard, 1);
            Grid.SetColumn(TemplateEditorCard, 1);
            Grid.SetRow(TemplateEditorCard, 0);
            Grid.SetColumnSpan(TemplateEditorCard, 1);
            TemplateListCard.VerticalAlignment = VerticalAlignment.Stretch;
            TemplateEditorCard.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private void ApplyListCardHeights(bool compact)
    {
        if (!compact)
        {
            TemplateListCard.MaxHeight = double.PositiveInfinity;
            TemplateListCard.MinHeight = 240;
            return;
        }

        TemplateListCard.MinHeight = 0;
        TemplateListCard.MaxHeight = ViewModel.HasItems ? 188 : 236;
    }
}
