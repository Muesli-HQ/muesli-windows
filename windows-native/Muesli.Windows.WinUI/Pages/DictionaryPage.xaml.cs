using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class DictionaryPage : Page
{
    public DictionaryPageViewModel ViewModel { get; } = new(App.Library, App.Settings, App.FilePickers, App.Dialogs, App.DictionarySuggestions);

    /// <summary>Per-row threshold drafts, keyed by entry id, until the row is saved.</summary>
    private readonly Dictionary<string, double> _thresholdDrafts = new(StringComparer.Ordinal);

    private bool _adding;

    public DictionaryPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.Items)) _thresholdDrafts.Clear();
            UpdateState();
        };
        // A post-paste prompt or the switch elsewhere changes the suggestions while this page is open.
        Loaded += (_, _) => App.Settings.Changed += OnSettingsChanged;
        Unloaded += (_, _) => App.Settings.Changed -= OnSettingsChanged;
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ApplyResponsiveLayout(ActualWidth);
            UpdateState();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
    }

    public static double MinimumThreshold => DictionaryCorrectionService.MinimumThreshold;

    public static double MaximumThreshold => DictionaryCorrectionService.MaximumThreshold;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>macOS ThresholdEditor label: "85%".</summary>
    public static string Percent(double value) => $"{Math.Round(value * 100):0}%";

    private void UpdateState()
    {
        var showEmpty = !ViewModel.HasItems && !_adding;
        EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        DictionaryList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
        SuggestionPanel.Visibility = ViewModel.HasSuggestions ? Visibility.Visible : Visibility.Collapsed;
        StatusBar.Severity = InfoBarSeverity.Error;
    }

    private void OnSettingsChanged(object? sender, MuesliSettings settings) =>
        DispatcherQueue.TryEnqueue(ViewModel.Reload);

    private void SetAdding(bool adding)
    {
        _adding = adding;
        AddRow.Visibility = adding ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void AddNew_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewEntryCommand.Execute(null);
        SetAdding(true);
        AddWordBox.Focus(FocusState.Programmatic);
    }

    private void AddSave_Click(object sender, RoutedEventArgs e) => CommitAdd();

    private void AddCancel_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.NewEntryCommand.Execute(null);
        SetAdding(false);
    }

    private void AddRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { CommitAdd(); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { AddCancel_Click(sender, e); e.Handled = true; }
    }

    private void CommitAdd()
    {
        ViewModel.SaveEntryCommand.Execute(null);
        if (ViewModel.HasValidationMessage) return;
        ViewModel.NewEntryCommand.Execute(null);
        SetAdding(false);
    }

    private void RowDraft_Changed(object sender, TextChangedEventArgs e) =>
        RefreshRowSave((sender as FrameworkElement)?.Tag as DictionaryListItem);

    private void RowThreshold_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DictionaryListItem item }) return;
        _thresholdDrafts[item.Id] = e.NewValue;
        if (Row(item)?.FindName("RowThresholdLabel") is TextBlock label) label.Text = Percent(e.NewValue);
        RefreshRowSave(item);
    }

    private void Row_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || (sender as FrameworkElement)?.Tag is not DictionaryListItem item) return;
        e.Handled = true;
        SaveRow(item);
    }

    private void RowSave_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DictionaryListItem item) SaveRow(item);
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DictionaryListItem item) return;
        SetAdding(false);
        ViewModel.Select(item);
        ViewModel.DeleteEntryCommand.Execute(null);
        ViewModel.NewEntryCommand.Execute(null);
    }

    /// <summary>
    /// Saves one row through the view model's single editor: load the row, apply its drafts, and
    /// save. The add row is closed first because it shares that editor state.
    /// </summary>
    private void SaveRow(DictionaryListItem item)
    {
        if (Draft(item) is not { } draft || !draft.Changed) return;
        SetAdding(false);
        ViewModel.Select(item);
        ViewModel.Phrase = draft.Phrase;
        ViewModel.Replacement = draft.Replacement;
        ViewModel.MatchingThreshold = draft.Threshold;
        ViewModel.SaveEntryCommand.Execute(null);
        ViewModel.NewEntryCommand.Execute(null);
    }

    /// <summary>macOS tints the row's checkmark accent only once the row differs from what is saved.</summary>
    private void RefreshRowSave(DictionaryListItem? item)
    {
        if (item is null || Row(item) is not { } row || Draft(item) is not { } draft) return;
        var canSave = draft.Changed && draft.Phrase.Length > 0;
        if (row.FindName("RowSave") is Button save) save.IsEnabled = canSave;
        if (row.FindName("RowSaveIcon") is FontIcon icon)
        {
            icon.Foreground = (Brush)Application.Current.Resources[canSave ? "MuesliAccentBrush" : "MuesliTextTertiaryBrush"];
        }
    }

    private (string Phrase, string Replacement, double Threshold, bool Changed)? Draft(DictionaryListItem item)
    {
        if (Row(item) is not { } row ||
            row.FindName("RowWord") is not TextBox word ||
            row.FindName("RowReplacement") is not TextBox replacement)
        {
            return null;
        }

        var phrase = word.Text.Trim();
        var replace = replacement.Text.Trim();
        var threshold = _thresholdDrafts.TryGetValue(item.Id, out var value) ? value : item.MatchingThreshold;
        var changed = phrase != item.Phrase ||
                      replace != (item.Replacement ?? "") ||
                      Math.Abs(threshold - item.MatchingThreshold) > 0.001;
        return (phrase, replace, threshold, changed);
    }

    private FrameworkElement? Row(DictionaryListItem item) =>
        (DictionaryList.ContainerFromItem(item) as ListViewItem)?.ContentTemplateRoot as FrameworkElement;

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);

        // Below ~900 DIP the switch and buttons drop under the title instead of clipping it.
        var compact = width < 900;
        Grid.SetRow(HeaderBar, compact ? 1 : 0);
        Grid.SetColumn(HeaderBar, compact ? 0 : 1);
        Grid.SetColumnSpan(HeaderBar, compact ? 2 : 1);
    }

    /// <summary>Prompt 9: real accessible names for record-backed list containers (<see cref="ListItemAutomationNames"/>).</summary>
    private void NamedListItem_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ListItemAutomationNames.Apply(args);
}
