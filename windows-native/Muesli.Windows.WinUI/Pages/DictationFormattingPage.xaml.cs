using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// macOS DictationFormattingSettingsView (MAC-15). Every change is saved straight to settings, as macOS
/// updateConfig does. An app added here but left on Original is shown until the page closes, never saved.
/// </summary>
public sealed partial class DictationFormattingPage : Page
{
    private readonly List<DictationAppFormattingRule> _pending = [];

    public DictationFormattingPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ContentRoot.Padding = MuesliPageMetrics.ContentPadding(ActualWidth);
            Reload();
        };
        SizeChanged += (_, args) => ContentRoot.Padding = MuesliPageMetrics.ContentPadding(args.NewSize.Width);
    }

    private static MuesliSettings Settings => App.Settings.Load();

    private static void Save(Func<MuesliSettings, MuesliSettings> change) => App.Settings.Save(change(App.Settings.Load()));

    private void SaveRules(Func<IReadOnlyList<DictationAppFormattingRule>, IReadOnlyList<DictationAppFormattingRule>> change)
    {
        Save(settings => settings with { DictationAppFormattingRules = change(settings.DictationAppFormattingRules) });
        Reload();
    }

    private void Reload()
    {
        var settings = Settings;
        var saved = settings.DictationAppFormattingRules;
        _pending.RemoveAll(pending => saved.Any(rule => string.Equals(rule.AppId, pending.AppId, StringComparison.OrdinalIgnoreCase)));
        var visible = saved.Concat(_pending).ToList();

        RulesPanel.Children.Clear();
        foreach (var rule in visible) RulesPanel.Children.Add(RuleCard(rule, settings.DictationFormattingTemplates));

        var suggestions = Suggestions(visible, out var loadFailed);
        SuggestionsList.Children.Clear();
        foreach (var suggestion in suggestions) SuggestionsList.Children.Add(SuggestionRow(suggestion));
        SuggestionsPanel.Visibility = Visible(suggestions.Count > 0);
        EmptyText.Text = loadFailed
            ? "App usage couldn't be loaded. You can still choose any installed app."
            : "Choose an app to get started. Suggestions appear here as you dictate into more apps.";
        EmptyText.Visibility = Visible(suggestions.Count == 0 && visible.Count == 0);

        TemplatesList.Children.Clear();
        foreach (var template in settings.DictationFormattingTemplates) TemplatesList.Children.Add(TemplateRow(template));
        TemplatesExpander.Header = $"My templates ({settings.DictationFormattingTemplates.Count})";
        TemplatesExpander.Visibility = Visible(settings.DictationFormattingTemplates.Count > 0);
        LiveCaption.Visibility = Visible(DictationAppFormattingRule.ActiveRules(saved).Count > 0);
    }

    /// <summary>Apps dictated into over the last 30 days, from <c>dictation-apps.json</c> (ids carry their time).</summary>
    private static IReadOnlyList<DictationFormattingSuggestion> Suggestions(IReadOnlyList<DictationAppFormattingRule> excluding, out bool loadFailed)
    {
        loadFailed = false;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
            var recent = DictationTargetApps.LoadAll(App.Library.Profile.RootDirectory)
                .Where(entry => entry.Key.Split('_') is [_, var ms, ..] && long.TryParse(ms, out var at) && at >= cutoff)
                .Select(entry => (entry.Value.AppId, entry.Value.Name));
            return DictationFormattingSuggestion.Ranked(recent, excluding);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadFailed = true;
            return [];
        }
    }

    private FrameworkElement RuleCard(DictationAppFormattingRule rule, IReadOnlyList<DictationFormattingTemplate> saved)
    {
        var templates = DictationFormattingTemplate.BuiltIns.Concat(saved).ToList();
        var selected = DictationFormattingTemplate.Selected(rule, saved);
        var panel = new StackPanel { Spacing = 10 };

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = rule.AppName, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var badge = Secondary(selected?.Name ?? "Custom", 13);
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);
        panel.Children.Add(header);

        var templatePicker = Picker($"Template for {rule.AppName}", templates.Select(template => template.Name).Append("Custom"),
            selected is null ? templates.Count : templates.IndexOf(selected));
        templatePicker.SelectionChanged += (_, _) =>
        {
            if (templatePicker.SelectedIndex is var index && index >= 0 && index < templates.Count && templates[index] != selected)
                ApplyTemplate(rule, templates[index]);
        };
        panel.Children.Add(Row("Template", templatePicker));
        panel.Children.Add(Secondary(DictationFormattingTemplate.Describe(rule.Formatting), 12));
        panel.Children.Add(new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            Background = Brush("MuesliBackgroundBrush"),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Secondary("PREVIEW", 11),
                    new TextBlock { Text = rule.Formatting.Apply(DictationFormattingTemplate.PreviewText), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
            },
        });

        var casePicker = Picker($"Capitalization for {rule.AppName}", ["Keep capitalization", "All lowercase"],
            rule.Formatting.LetterCase == DictationAppFormatting.Lowercase ? 1 : 0);
        casePicker.SelectionChanged += (_, _) => Update(rule, formatting => formatting with
        {
            LetterCase = casePicker.SelectedIndex == 1 ? DictationAppFormatting.Lowercase : DictationAppFormatting.Unchanged,
        });
        var periodPicker = Picker($"Final period for {rule.AppName}", ["Keep final period", "Remove final period"],
            rule.Formatting.FinalPeriod == DictationAppFormatting.Remove ? 1 : 0);
        periodPicker.SelectionChanged += (_, _) => Update(rule, formatting => formatting with
        {
            FinalPeriod = periodPicker.SelectedIndex == 1 ? DictationAppFormatting.Remove : DictationAppFormatting.Keep,
        });
        var saveTemplate = new Button { Content = "Save as Template…", IsEnabled = !rule.Formatting.IsOriginal };
        saveTemplate.Click += async (_, _) => await SaveTemplateAsync(rule);
        panel.Children.Add(new Expander
        {
            Header = "Customize style",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Row("Capitalization", casePicker),
                    Row("Final period", periodPicker),
                    Secondary("All lowercase includes names, acronyms and links. Removing a final period keeps other punctuation. Changes apply to this app only.", 12),
                    saveTemplate,
                },
            },
        });

        var remove = new Button { Content = "Remove App", Foreground = Brush("MuesliErrorBrush") };
        AutomationProperties.SetName(remove, "Remove formatting for " + rule.AppName);
        remove.Click += (_, _) =>
        {
            _pending.RemoveAll(pending => pending.AppId == rule.AppId);
            SaveRules(rules => DictationFormattingEdits.RemoveApp(rules, rule.AppId));
        };
        panel.Children.Add(remove);
        return Card(panel, rule.AppName);
    }

    private FrameworkElement SuggestionRow(DictationFormattingSuggestion suggestion)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = suggestion.AppName, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium },
                Secondary(suggestion.Caption, 12),
            },
        });
        var customize = new Button { Content = "Customize", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(customize, "Customize dictation in " + suggestion.AppName);
        customize.Click += (_, _) => BeginEditing(suggestion.AppId, suggestion.AppName);
        Grid.SetColumn(customize, 1);
        grid.Children.Add(customize);
        return grid;
    }

    private FrameworkElement TemplateRow(DictationFormattingTemplate template)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = template.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium },
                Secondary(template.Formatting.Apply(DictationFormattingTemplate.PreviewText), 12),
            },
        });
        var remove = new Button { Content = "Remove", Foreground = Brush("MuesliErrorBrush"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(remove, "Remove template " + template.Name);
        remove.Click += (_, _) =>
        {
            Save(settings => settings with
            {
                DictationFormattingTemplates = settings.DictationFormattingTemplates.Where(item => item.Id != template.Id).ToList(),
                DictationAppFormattingRules = DictationFormattingEdits.DetachTemplate(settings.DictationAppFormattingRules, template.Id),
            });
            Reload();
        };
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        return grid;
    }

    private void BeginEditing(string appId, string name)
    {
        if (!Settings.DictationAppFormattingRules.Any(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase)) &&
            !_pending.Any(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase)))
            _pending.Add(new DictationAppFormattingRule(appId, name));
        Save(settings => settings with { DictationFormattingSetupDismissed = true });
        Reload();
    }

    /// <summary>A pending app becomes a saved rule on its first non-Original change.</summary>
    private IReadOnlyList<DictationAppFormattingRule> WithPending(IReadOnlyList<DictationAppFormattingRule> rules, DictationAppFormattingRule rule) =>
        DictationFormattingEdits.AddApp(rules, rule.AppId, rule.AppName);

    private void Update(DictationAppFormattingRule rule, Func<DictationAppFormatting, DictationAppFormatting> mutate)
    {
        if (mutate(rule.Formatting) == rule.Formatting) return;
        if (mutate(rule.Formatting).IsOriginal) KeepVisible(rule);
        SaveRules(rules => DictationFormattingEdits.Update(WithPending(rules, rule), rule.AppId, mutate));
    }

    private void ApplyTemplate(DictationAppFormattingRule rule, DictationFormattingTemplate template)
    {
        if (template.Formatting.IsOriginal) KeepVisible(rule);
        SaveRules(rules => DictationFormattingEdits.ApplyTemplate(WithPending(rules, rule), rule.AppId, template));
    }

    private void KeepVisible(DictationAppFormattingRule rule)
    {
        if (!_pending.Any(pending => pending.AppId == rule.AppId)) _pending.Add(new DictationAppFormattingRule(rule.AppId, rule.AppName));
    }

    private async Task SaveTemplateAsync(DictationAppFormattingRule rule)
    {
        var name = new TextBox { PlaceholderText = "Template name" };
        AutomationProperties.SetName(name, "Template name");
        var error = new TextBlock { Foreground = Brush("MuesliErrorBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save a writing template",
            PrimaryButtonText = "Save Template",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Width = 400,
                Children =
                {
                    Secondary("Reuse this style in other apps. Each app keeps its own copy of the formatting.", 13),
                    name,
                    new TextBlock { Text = rule.Formatting.Apply(DictationFormattingTemplate.PreviewText), FontSize = 13, TextWrapping = TextWrapping.Wrap },
                    error,
                },
            },
        };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var settings = Settings;
            var (template, message) = DictationFormattingEdits.SaveTemplate(name.Text, rule, settings.DictationAppFormattingRules, settings.DictationFormattingTemplates);
            if (template is null)
            {
                error.Text = message ?? "";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            Save(current => current with
            {
                DictationFormattingTemplates = [.. current.DictationFormattingTemplates, template],
                DictationAppFormattingRules = current.DictationAppFormattingRules
                    .Select(item => string.Equals(item.AppId, rule.AppId, StringComparison.OrdinalIgnoreCase) ? item with { TemplateId = template.Id } : item)
                    .ToList(),
            });
            // macOS verifySavedDictationFormattingTemplate: confirm the write landed before closing.
            if (!Settings.DictationFormattingTemplates.Contains(template))
            {
                error.Text = "The template couldn't be saved. Try again.";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        await dialog.ShowAsync();
        Reload();
    }

    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var picked = await App.FilePickers.PickOpenFileAsync(new FilePickerRequest("Choose an app for dictation formatting", [".exe"]));
        if (picked is null) return;
        // The id matches what Muesli records for a paste target: the process name.
        var appId = Path.GetFileNameWithoutExtension(picked.Path);
        string? description = null;
        try { description = FileVersionInfo.GetVersionInfo(picked.Path).FileDescription; } catch { /* unreadable: use the file name */ }
        BeginEditing(appId, string.IsNullOrWhiteSpace(description) ? appId : description.Trim());
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(SettingsPage), "dictation");
    }

    private static ComboBox Picker(string name, IEnumerable<string> items, int selected)
    {
        var picker = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, MinWidth = 200 };
        AutomationProperties.SetName(picker, name);
        return picker;
    }

    private static Grid Row(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Border Card(UIElement child, string name)
    {
        var card = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            Background = Brush("MuesliSurfaceBrush"),
            BorderBrush = Brush("MuesliBorderSoftBrush"),
            BorderThickness = new Thickness(1),
            Child = child,
        };
        AutomationProperties.SetName(card, name);
        return card;
    }

    private static TextBlock Secondary(string text, double size) =>
        new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brush("MuesliTextSecondaryBrush") };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
