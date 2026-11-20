using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muesli.Windows.Cotypist;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// macOS ModelsView cotypistSection and cotypistModelCard (ModelsView.swift:716-858): one card per
/// Cotypist model with Download, Pause, "Use for Cotypist" and Delete. Only when <see cref="App.Cotypist"/>
/// exists (Cotypist is in this build and its text service is installed).
/// </summary>
public sealed partial class ModelsPage
{
    /// <summary>Set before navigating here to open the Cotypist tab (Settings › Change).</summary>
    public static bool OpenCotypistNext { get; set; }

    private bool _showCotypist;
    private SelectorBarItem? CotypistTab { get; set; }

    private void InitializeCotypist()
    {
        if (App.Cotypist is not { } cotypist) return;
        CotypistTab = new SelectorBarItem { Text = "Cotypist", Tag = "cotypist" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CotypistTab, "Show Cotypist models");
        CategoryBar.Items.Add(CotypistTab);
        _showCotypist = OpenCotypistNext;
        OpenCotypistNext = false;
        void Changed() => DispatcherQueue.TryEnqueue(RenderCotypistCards);
        void SettingsChanged(object? sender, MuesliSettings settings) => Changed();
        Loaded += (_, _) =>
        {
            cotypist.Changed += Changed;
            App.Settings.Changed += SettingsChanged;
            RenderCotypistCards();
        };
        Unloaded += (_, _) =>
        {
            cotypist.Changed -= Changed;
            App.Settings.Changed -= SettingsChanged;
        };
    }

    private void RenderCotypistCards()
    {
        if (App.Cotypist is not { } cotypist) return;
        var selected = App.Settings.Load().CotypistModel;
        CotypistCards.Children.Clear();
        foreach (var model in CotypistModels.All) CotypistCards.Children.Add(CotypistCard(cotypist, model, selected));
    }

    private Border CotypistCard(CotypistSetup cotypist, CotypistModel model, string selectedId)
    {
        var status = cotypist.StatusOf(model);
        var isSelected = model.Id == selectedId;
        var isInUse = isSelected && status.State == CotypistSetup.ModelState.Ready;
        var resources = Application.Current.Resources;

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = model.Label, Style = (Style)Resources["ModelTitleStyle"] });
        if (model.IsRecommended)
            title.Children.Add(new Border
            {
                Style = (Style)Resources["ModelFamilyBadgeStyle"],
                Child = new TextBlock { Text = "Recommended", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
            });
        if (model.Release is { } release)
            title.Children.Add(new TextBlock { Text = SizeLabel(release.SizeBytes), Style = (Style)Resources["ModelMetaLabelStyle"] });
        var text = new StackPanel { Spacing = 4, MinWidth = 0 };
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = model.Summary, Style = (Style)Resources["ModelCaptionStyle"], TextWrapping = TextWrapping.Wrap });

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Logo(model.Logo));
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        if (isInUse || status.State == CotypistSetup.ModelState.Ready)
        {
            var chip = new Border
            {
                Style = (Style)Resources[isInUse ? "ModelActiveChipStyle" : "ModelStatusChipStyle"],
                Child = new TextBlock { Text = isInUse ? "Cotypist Active" : "Downloaded", FontSize = 11 }
            };
            Grid.SetColumn(chip, 2);
            header.Children.Add(chip);
        }

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(header);
        if (status.State == CotypistSetup.ModelState.Downloading)
        {
            body.Children.Add(new ProgressBar { Maximum = 1, Value = status.Fraction, IsIndeterminate = status.Fraction <= 0 });
            body.Children.Add(new TextBlock
            {
                Text = $"Downloading… {(int)(status.Fraction * 100)}%",
                Style = (Style)Resources["ModelMetaLabelStyle"],
                FontSize = 12
            });
        }
        if (status.State == CotypistSetup.ModelState.Failed)
            body.Children.Add(new TextBlock
            {
                Text = $"Download failed: {status.Error}",
                FontSize = 12,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)resources["MuesliErrorBrush"]
            });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        switch (status.State)
        {
            case CotypistSetup.ModelState.Downloading:
                actions.Children.Add(Pill("Pause", prominent: false, () => cotypist.CancelDownload(model)));
                break;
            case CotypistSetup.ModelState.Ready when !isSelected:
                actions.Children.Add(Pill("Use for Cotypist", prominent: true, () => UseForCotypist(model)));
                var delete = new Button { Style = (Style)Resources["ModelDeleteButtonStyle"], Content = new FontIcon { Glyph = "", FontSize = 12 } };
                ToolTipService.SetToolTip(delete, $"Delete {model.Label}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(delete, $"Delete {model.Label}");
                delete.Click += async (_, _) => await ConfirmDeleteAsync(cotypist, model);
                actions.Children.Add(delete);
                break;
            case CotypistSetup.ModelState.Missing or CotypistSetup.ModelState.Failed:
                actions.Children.Add(Pill("Download", prominent: true, () => cotypist.Download(model)));
                break;
            case CotypistSetup.ModelState.Unavailable:
                actions.Children.Add(new TextBlock { Text = "Coming soon", Style = (Style)Resources["ModelMetaLabelStyle"], FontSize = 12 });
                break;
        }
        if (actions.Children.Count > 0) body.Children.Add(actions);

        var card = new Border
        {
            Style = (Style)Resources["ModelCardStyle"],
            Child = body,
            BorderThickness = new Thickness(isInUse ? 1.5 : 1)
        };
        if (isInUse) card.BorderBrush = (Brush)resources["MuesliAccentBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"{model.Label} Cotypist model");
        return card;
    }

    private static FrameworkElement Logo(string logo)
    {
        var box = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top };
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "vendor", logo + ".png");
        if (File.Exists(file))
            box.Child = new Image { Source = new BitmapImage(new Uri($"ms-appx:///Assets/vendor/{logo}.png")), Stretch = Stretch.Uniform };
        else
        {
            // No vendor mark in this build (Gemma): the cleanup cards' generic model glyph.
            box.Background = (Brush)Application.Current.Resources["MuesliAccentMutedBrush"];
            box.Child = new FontIcon { Glyph = "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["MuesliAccentBrush"] };
        }
        return box;
    }

    private Button Pill(string label, bool prominent, Action action)
    {
        var button = new Button { Content = label, Style = (Style)Resources[prominent ? "ModelAccentPillStyle" : "ModelNeutralPillStyle"] };
        button.Click += (_, _) => action();
        return button;
    }

    private static string SizeLabel(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:0.0} GB" : $"{bytes / 1_000_000.0:0} MB";

    /// <summary>macOS setSettingFromUI("cotypist_model"): the new model loads at once while Cotypist is on.</summary>
    private static void UseForCotypist(CotypistModel model)
    {
        try
        {
            App.Settings.Save(App.Settings.Load() with { CotypistModel = model.Id });
        }
        catch (InvalidOperationException)
        {
            return; // the settings file couldn't be read cleanly; saving would overwrite it
        }
        if (App.Cotypist is { IsOn: true } cotypist) cotypist.ModelChanged();
    }

    private async Task ConfirmDeleteAsync(CotypistSetup cotypist, CotypistModel model)
    {
        var dialog = new ContentDialog
        {
            Title = $"Delete {model.Label}?",
            Content = "Its files are removed from this PC. You can download it again later.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await cotypist.DeleteAsync(model);
    }
}
