using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Cotypist;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// macOS cotypistSettingsSection (SettingsView.swift:2012-2350). Shown only when <see cref="App.Cotypist"/>
/// exists: a build with Cotypist and its text service installed. Every control applies at once.
/// </summary>
public sealed partial class SettingsPage
{
    private static readonly CotypistSuggestionLength[] Lengths = Enum.GetValues<CotypistSuggestionLength>();
    private static readonly CotypistFrequency[] Frequencies = Enum.GetValues<CotypistFrequency>();
    private static readonly CotypistWordPause[] Pauses = Enum.GetValues<CotypistWordPause>();
    private bool _renderingCotypist;

    private void InitializeCotypist()
    {
        if (App.Cotypist is not { } cotypist) return;
        CotypistSection.Visibility = Visibility.Visible;
        CotypistLengthPicker.ItemsSource = Lengths.Select(length => length.Label()).ToList();
        CotypistFrequencyPicker.ItemsSource = Frequencies.Select(frequency => frequency.Label()).ToList();
        CotypistWordPausePicker.ItemsSource = Pauses.Select(pause => pause.Label()).ToList();
        void Changed() => DispatcherQueue.TryEnqueue(RenderCotypist);
        void SettingsChanged(object? sender, MuesliSettings settings) => Changed();
        Loaded += (_, _) =>
        {
            cotypist.Changed += Changed;
            App.Settings.Changed += SettingsChanged;
            RenderCotypist();
        };
        Unloaded += (_, _) =>
        {
            cotypist.Changed -= Changed;
            App.Settings.Changed -= SettingsChanged;
        };
    }

    private void RenderCotypist()
    {
        if (App.Cotypist is not { } cotypist) return;
        _renderingCotypist = true;
        try
        {
            var settings = App.Settings.Load();
            var on = cotypist.IsOn;
            CotypistToggle.IsOn = on;
            CotypistStatusText.Text = on ? "Suggests the next words as you type, in any app." : "Local typeahead in every app, running on this PC.";

            var model = cotypist.SelectedModel;
            var status = cotypist.StatusOf(model);
            CotypistModelRow.Visibility = status.State == CotypistSetup.ModelState.Ready ? Visibility.Collapsed : Visibility.Visible;
            CotypistModelTitle.Text = $"Typeahead model: {model.Label}";
            CotypistModelProgress.Visibility = status.State == CotypistSetup.ModelState.Downloading ? Visibility.Visible : Visibility.Collapsed;
            CotypistModelProgress.Value = status.Fraction;
            var size = SizeLabel(model);
            CotypistModelDetail.Text = status.State switch
            {
                CotypistSetup.ModelState.Downloading => $"Downloading… {(int)(status.Fraction * 100)}% of {size}",
                CotypistSetup.ModelState.Failed => $"Download failed: {status.Error}",
                CotypistSetup.ModelState.Unavailable => "Coming soon. Choose another model in Models.",
                _ => $"{size}. Runs on this PC; downloaded when you turn Cotypist on."
            };
            CotypistModelAction.Content = status.State is CotypistSetup.ModelState.Missing or CotypistSetup.ModelState.Failed ? "Download" : "Models";

            CotypistOptionsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            CotypistModelLabel.Text = model.Label;
            CotypistLengthPicker.SelectedIndex = Array.IndexOf(Lengths, CotypistOptions.Parse<CotypistSuggestionLength>(settings.CotypistSuggestionLength));
            CotypistFrequencyPicker.SelectedIndex = Array.IndexOf(Frequencies, CotypistOptions.Parse<CotypistFrequency>(settings.CotypistFrequency));
            CotypistCompleteWordsToggle.IsOn = settings.CotypistCompleteWords;
            CotypistCompleteWordsHelp.Text = settings.CotypistCompleteWords
                ? "Pause inside a word to see the rest of it."
                : "Off: suggestions appear only after a space or punctuation.";
            CotypistWordPauseRow.Visibility = settings.CotypistCompleteWords ? Visibility.Visible : Visibility.Collapsed;
            CotypistWordPausePicker.SelectedIndex = Array.IndexOf(Pauses, CotypistOptions.Parse<CotypistWordPause>(settings.CotypistWordPause));
            RenderQuietApps(settings.CotypistDisabledApps);
        }
        finally
        {
            _renderingCotypist = false;
        }
    }

    private static string SizeLabel(CotypistModel model) => model.Release?.SizeBytes switch
    {
        >= 1_000_000_000 and var bytes => $"{bytes / 1_000_000_000.0:0.0} GB",
        { } bytes => $"{bytes / 1_000_000.0:0} MB",
        null => ""
    };

    private void CotypistToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_renderingCotypist || App.Cotypist is not { } cotypist || cotypist.IsOn == CotypistToggle.IsOn) return;
        cotypist.SetOn(CotypistToggle.IsOn);
    }

    private void CotypistModelAction_Click(object sender, RoutedEventArgs e)
    {
        if (App.Cotypist is not { } cotypist) return;
        if (cotypist.StatusOf(cotypist.SelectedModel).State is CotypistSetup.ModelState.Missing or CotypistSetup.ModelState.Failed)
            cotypist.DownloadIfNeeded();
        else
            CotypistModels_Click(sender, e);
    }

    private void CotypistModels_Click(object sender, RoutedEventArgs e)
    {
        ModelsPage.OpenCotypistNext = true;
        (App.Window as MainWindow)?.ShellPage?.NavigateTo("models");
    }

    private void CotypistOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_renderingCotypist) return;
        UpdateCotypistSettings(settings => settings with
        {
            CotypistSuggestionLength = Pick(CotypistLengthPicker, Lengths, settings.CotypistSuggestionLength),
            CotypistFrequency = Pick(CotypistFrequencyPicker, Frequencies, settings.CotypistFrequency),
            CotypistCompleteWords = CotypistCompleteWordsToggle.IsOn,
            CotypistWordPause = Pick(CotypistWordPausePicker, Pauses, settings.CotypistWordPause)
        });
    }

    private static string Pick<T>(ComboBox picker, T[] values, string current) where T : struct, Enum =>
        picker.SelectedIndex >= 0 && picker.SelectedIndex < values.Length ? values[picker.SelectedIndex].ToString().ToLowerInvariant() : current;

    /// <summary>Saves straight to disk; the page's Save draft starts from disk, so it keeps these.</summary>
    private void UpdateCotypistSettings(Func<MuesliSettings, MuesliSettings> change)
    {
        try
        {
            App.Settings.Save(change(App.Settings.Load()));
        }
        catch (InvalidOperationException error)
        {
            // The settings file couldn't be read cleanly; saving would overwrite it.
            CotypistStatusText.Text = error.Message;
        }
    }

    private void SetQuiet(string appId, bool quiet) => UpdateCotypistSettings(settings => settings with
    {
        CotypistDisabledApps = quiet
            ? [.. settings.CotypistDisabledApps.Where(id => !id.Equals(appId, StringComparison.OrdinalIgnoreCase)), appId]
            : [.. settings.CotypistDisabledApps.Where(id => !id.Equals(appId, StringComparison.OrdinalIgnoreCase))]
    });

    private void RenderQuietApps(IReadOnlyList<string> quiet)
    {
        CotypistQuietEmpty.Visibility = quiet.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CotypistQuietChips.Visibility = quiet.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CotypistQuietChips.Children.Clear();
        foreach (var id in quiet)
        {
            var app = CotypistQuietApps.Describe(id);
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 9 },
                Padding = new Thickness(0),
                Width = 18,
                Height = 18,
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(9),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(remove, $"Turn Cotypist back on in {app.Name}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {app.Name}");
            remove.Click += (_, _) => SetQuiet(id, false);
            var chip = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(10, 0, 8, 0),
                Height = 36,
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MuesliBorderBrush"],
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MuesliSurfacePrimaryBrush"]
            };
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = app.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, app.Id);
            chip.Children.Add(name);
            Grid.SetColumn(remove, 1);
            chip.Children.Add(remove);
            CotypistQuietChips.Children.Add(chip);
        }

        CotypistRecommended.Children.Clear();
        var recommended = CotypistQuietApps.Recommended(quiet);
        CotypistRecommended.Visibility = recommended.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (recommended.Count == 0) return;
        CotypistRecommended.Children.Add(new TextBlock
        {
            Text = "Suggested",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MuesliTextTertiaryBrush"]
        });
        foreach (var app in recommended)
        {
            var add = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children = { new TextBlock { Text = app.Name }, new FontIcon { Glyph = "", FontSize = 9 } }
                },
                FontSize = 12,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(6)
            };
            ToolTipService.SetToolTip(add, $"Keep Cotypist quiet in {app.Name}");
            add.Click += (_, _) => SetQuiet(app.Id, true);
            CotypistRecommended.Children.Add(add);
        }
    }

    /// <summary>Open apps, then any app chosen from disk.</summary>
    private void CotypistAddAppMenu_Opening(object sender, object e)
    {
        CotypistAddAppMenu.Items.Clear();
        var quiet = App.Settings.Load().CotypistDisabledApps;
        foreach (var app in CotypistQuietApps.Running(quiet))
        {
            var item = new MenuFlyoutItem { Text = app.Name };
            ToolTipService.SetToolTip(item, app.Id);
            item.Click += (_, _) => SetQuiet(app.Id, true);
            CotypistAddAppMenu.Items.Add(item);
        }
        if (CotypistAddAppMenu.Items.Count > 0) CotypistAddAppMenu.Items.Add(new MenuFlyoutSeparator());
        var choose = new MenuFlyoutItem { Text = "Choose an app…" };
        choose.Click += async (_, _) =>
        {
            var picker = new global::Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
            if (await picker.PickSingleFileAsync() is { } file) SetQuiet(file.Name.ToLowerInvariant(), true);
        };
        CotypistAddAppMenu.Items.Add(choose);
    }
}
