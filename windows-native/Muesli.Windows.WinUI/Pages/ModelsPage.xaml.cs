using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class ModelsPage : Page
{
    public ModelsPageViewModel ViewModel { get; } = new(App.Models, App.Settings, App.UiDispatcher, App.Dialogs);

    public ModelsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
        ViewModel.PropertyChanged += (_, _) => UpdateCategoryState();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            UpdateCategoryState();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Persists a per-model language choice when the user changes the picker.</summary>
    private void ModelLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: ModelFamilyCardItem card, SelectedItem: ModelLanguageOption option } combo)
        {
            return;
        }

        // Ignore the selection the template sets while binding the persisted value.
        if (!combo.IsLoaded) return;
        if (string.Equals(card.SelectedVariant?.SelectedLanguageCode, option.Code, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ViewModel.SetLanguage(card.SelectedVariant, option.Code);
    }

    private void CategoryBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is SelectorBarItem { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.CategoryIndex = index;
            UpdateCategoryState();
        }
    }

    /// <summary>
    /// Gutters follow the frozen page tokens. The only page-specific decision is the category
    /// group: macOS keeps "Model category" inline to the left of the segments and centres the
    /// pair, which does not survive a compact content width, so there the label stacks above the
    /// segments again. Both the tier test and the padding come from <see cref="MuesliPageMetrics"/>.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);

        var compact = MuesliPageMetrics.IsCompact(width);
        CategoryGroup.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        CategoryGroup.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        CategoryLabel.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        CategoryDescription.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        CategoryDescription.TextAlignment = compact ? TextAlignment.Left : TextAlignment.Center;
    }

    private void UpdateCategoryState()
    {
        StatusBar.Severity = ViewModel.StatusTone switch
        {
            ModelStatusTone.Error => InfoBarSeverity.Error,
            ModelStatusTone.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
        ModelsPanel.Visibility = ViewModel.IsCleanupCategory ? Visibility.Collapsed : Visibility.Visible;
        CleanupPanel.Visibility = ViewModel.IsCleanupCategory ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !ViewModel.IsCleanupCategory && !ViewModel.HasItems
            ? Visibility.Visible
            : Visibility.Collapsed;
        ModelList.Visibility = !ViewModel.IsCleanupCategory && ViewModel.HasItems
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (CategoryBar.Items.Count == 0)
        {
            return;
        }

        var desired = Math.Clamp(ViewModel.CategoryIndex, 0, CategoryBar.Items.Count - 1);
        if (CategoryBar.SelectedItem != CategoryBar.Items[desired])
        {
            CategoryBar.SelectedItem = CategoryBar.Items[desired];
        }
    }
}
