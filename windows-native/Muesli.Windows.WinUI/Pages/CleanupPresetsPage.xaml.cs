using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>One preset row: the built-in default or one of the user's own.</summary>
public sealed record CleanupPresetRow(string Id, string Name, string Prompt, bool IsBuiltIn, bool IsActive, Brush BorderBrush)
{
    public bool IsCustom => !IsBuiltIn;
    public bool CanUse => !IsActive;
    public string Glyph => IsBuiltIn ? "" : "";
}

/// <summary>
/// macOS TranscriptCleanupPromptsManagerView: built-in and custom presets with Use / Duplicate /
/// Edit / Delete, and an editor that only appears while creating or editing. Every change is saved
/// straight to settings, as macOS updateConfig does.
/// </summary>
public sealed partial class CleanupPresetsPage : Page
{
    private string? _editingId;
    private bool _editing;

    public CleanupPresetsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ContentRoot.Padding = MuesliPageMetrics.ContentPadding(ActualWidth);
            Reload();
        };
        SizeChanged += (_, args) => ContentRoot.Padding = MuesliPageMetrics.ContentPadding(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void Reload()
    {
        var settings = App.Settings.Load();
        var accent = (SolidColorBrush)Application.Current.Resources["MuesliAccentBrush"];
        var activeBorder = new SolidColorBrush(accent.Color) { Opacity = 0.35 };
        var border = (Brush)Application.Current.Resources["MuesliBorderSoftBrush"];
        CleanupPresetRow Row(CleanupPromptPreset preset, bool builtIn)
        {
            var active = preset.Id == settings.ActiveCleanupPromptId;
            return new(preset.Id, preset.Name, preset.Prompt, builtIn, active, active ? activeBorder : border);
        }
        BuiltInList.ItemsSource = CleanupPrompts.BuiltIns.Select(preset => Row(preset, true)).ToList();
        CustomList.ItemsSource = settings.CustomCleanupPrompts.Select(preset => Row(preset, false)).ToList();
        EmptyState.Visibility = BoolToVisibility(settings.CustomCleanupPrompts.Count == 0);
    }

    private static void Save(Func<MuesliSettings, MuesliSettings> change) => App.Settings.Save(change(App.Settings.Load()));

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(SettingsPage), "dictation");
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } row) return;
        Save(settings => settings with { ActiveCleanupPromptId = row.Id });
        Reload();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } row) return;
        OpenEditor(null, CleanupPrompts.SuggestUniqueName($"{row.Name} Copy", App.Settings.Load().CustomCleanupPrompts), row.Prompt);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is { } row) OpenEditor(row.Id, row.Name, row.Prompt);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Tagged(sender) is not { } row) return;
        var choice = await App.Dialogs.ConfirmAsync(
            "This prompt preset will be permanently removed. Existing dictations are not affected.",
            $"Delete \"{row.Name}\"?");
        if (choice != AppDialogChoice.Primary) return;
        Save(settings => CleanupPrompts.Delete(settings, row.Id));
        if (_editingId == row.Id) CloseEditor();
        Reload();
    }

    private void New_Click(object sender, RoutedEventArgs e) => OpenEditor(null, "", "");

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void Save_Click(object sender, RoutedEventArgs e) => SaveEditor();

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_editing) SaveEditor();
        args.Handled = true;
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_editing) return;
        CloseEditor();
        args.Handled = true;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameBox.Text)) NameError.Visibility = Visibility.Collapsed;
    }

    private void OpenEditor(string? id, string name, string prompt)
    {
        _editingId = id;
        _editing = true;
        NameBox.Text = name;
        PromptBox.Text = prompt;
        NameError.Visibility = PromptError.Visibility = Visibility.Collapsed;
        EditorTitle.Text = id is null ? "New preset" : "Edit preset";
        SaveLabel.Text = id is null ? "Create preset" : "Save changes";
        SaveGlyph.Glyph = id is null ? "" : "";
        EditorCard.Visibility = Visibility.Visible;
        NewPresetButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Visible;
        EditorCard.StartBringIntoView();
        NameBox.Focus(FocusState.Programmatic);
    }

    private void CloseEditor()
    {
        _editingId = null;
        _editing = false;
        EditorCard.Visibility = Visibility.Collapsed;
        NewPresetButton.Visibility = Visibility.Visible;
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void SaveEditor()
    {
        var name = NameBox.Text.Trim();
        var prompt = PromptBox.Text.Trim();
        var nameError = CleanupPrompts.NameError(name, _editingId, App.Settings.Load().CustomCleanupPrompts);
        NameError.Text = nameError ?? "";
        NameError.Visibility = BoolToVisibility(nameError is not null);
        PromptError.Visibility = BoolToVisibility(prompt.Length == 0);
        if (nameError is not null || prompt.Length == 0) return;

        var id = _editingId;
        Save(settings => id is null ? CleanupPrompts.Create(settings, name, prompt) : CleanupPrompts.Update(settings, id, name, prompt));
        CloseEditor();
        Reload();
    }

    private static CleanupPresetRow? Tagged(object sender) => (sender as FrameworkElement)?.Tag as CleanupPresetRow;
}
