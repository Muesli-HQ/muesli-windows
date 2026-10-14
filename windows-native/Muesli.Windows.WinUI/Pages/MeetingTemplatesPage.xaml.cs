using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;
using Windows.System;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// macOS MeetingTemplatesManagerView: a list of template rows with Edit / Delete, and an editor
/// that only appears while creating or editing. The view model keeps one editor state, so the
/// page decides when that editor is open.
/// </summary>
public sealed partial class MeetingTemplatesPage : Page
{
    public MeetingTemplatesViewModel ViewModel { get; } = new(App.Library, App.Dialogs);

    private bool _editing;

    public MeetingTemplatesPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsStatusError) or nameof(ViewModel.StatusMessage) or null)
            {
                StatusBar.Severity = ViewModel.IsStatusError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            }

            if (args.PropertyName is nameof(ViewModel.HasItems) or nameof(ViewModel.Templates) or null)
            {
                UpdateState();
            }
        };
        Loaded += (_, _) =>
        {
            ViewModel.Load();
            ContentRoot.Padding = MuesliPageMetrics.ContentPadding(ActualWidth);
            UpdateState();
        };
        SizeChanged += (_, args) => ContentRoot.Padding = MuesliPageMetrics.ContentPadding(args.NewSize.Width);
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(MeetingsPage));
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e) => BeginNew();

    private void CancelEdit_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PersistedMeetingTemplate template) return;
        ViewModel.IsStatusOpen = false;
        ViewModel.SelectedTemplate = template;
        SetEditing(true);
        NameBox.Focus(FocusState.Programmatic);
    }

    private async void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PersistedMeetingTemplate template) return;
        // The delete runs through the shared editor state, so any open edit is closed first.
        SetEditing(false);
        ViewModel.SelectedTemplate = template;
        await ViewModel.DeleteCommand.ExecuteAsync(null);
        ViewModel.SelectedTemplate = null;
    }

    private void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        PromptBox.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_editing) Save();
        args.Handled = true;
    }

    private void NewAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        BeginNew();
        args.Handled = true;
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_editing) return;
        CloseEditor();
        args.Handled = true;
    }

    private void BeginNew()
    {
        ViewModel.NewTemplateCommand.Execute(null);
        SetEditing(true);
        NameBox.Focus(FocusState.Programmatic);
    }

    private void Save()
    {
        ViewModel.SaveCommand.Execute(null);
        if (ViewModel.HasNameError || ViewModel.HasPromptError)
        {
            // macOS reports validation inline under each field only; the banner would repeat it.
            ViewModel.IsStatusOpen = false;
            return;
        }
        SetEditing(false);
        ViewModel.SelectedTemplate = null;
    }

    private void CloseEditor()
    {
        ViewModel.NewTemplateCommand.Execute(null);
        SetEditing(false);
    }

    private void SetEditing(bool editing)
    {
        _editing = editing;
        TemplateEditorCard.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        NewTemplateButton.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        CancelEditButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        var existing = ViewModel.IsEditingExisting;
        SaveLabel.Text = existing ? "Save changes" : "Create template";
        SaveGlyph.Glyph = existing ? "" : "";
        UpdateState();
    }

    private void UpdateState()
    {
        EmptyState.Visibility = !ViewModel.HasItems && !_editing ? Visibility.Visible : Visibility.Collapsed;
        TemplateList.Visibility = ViewModel.HasItems ? Visibility.Visible : Visibility.Collapsed;
    }
}
