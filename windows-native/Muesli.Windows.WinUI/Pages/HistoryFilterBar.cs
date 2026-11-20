using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// The macOS history filter bar shared by Timeline and Dictations: the date menu
/// (<c>dateFilterMenu</c>) and the destination-app menu (<c>TargetApplicationFilterMenu</c>), each a
/// quiet tertiary label at its default and an accent capsule once something is chosen.
/// </summary>
internal static class HistoryFilterBar
{
    public static void FillDateMenu(MenuFlyout menu, IReadOnlyList<string> labels, int selected, Action<int> select)
    {
        menu.Items.Clear();
        for (var index = 0; index < labels.Count; index++)
        {
            var chosen = index;
            var item = new ToggleMenuFlyoutItem { Text = labels[index], IsChecked = index == selected };
            item.Click += (_, _) => select(chosen);
            menu.Items.Add(item);
        }
    }

    public static void FillAppMenu(MenuFlyout menu, IReadOnlyList<DictationTargetApp> apps, DictationTargetApp? selected, Action<DictationTargetApp?> select)
    {
        menu.Items.Clear();
        var all = new ToggleMenuFlyoutItem { Text = "All apps", IsChecked = selected is null };
        all.Click += (_, _) => select(null);
        menu.Items.Add(all);
        if (apps.Count == 0) return;
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var app in apps)
        {
            var item = new ToggleMenuFlyoutItem { Text = app.Name, IsChecked = DictationTargetApps.Same(app, selected) };
            if (ComputerUseRowStyle.AppIcon(app) is { } icon) item.Icon = new ImageIcon { Source = icon };
            item.Click += (_, _) => select(app);
            menu.Items.Add(item);
        }
    }

    /// <summary>A bare funnel at "All time"; otherwise an accent capsule naming the range.</summary>
    public static void StyleDatePill(Button button, FontIcon icon, TextBlock label, string? range)
    {
        var active = range is not null;
        label.Text = range ?? "";
        label.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        button.Padding = active ? new Thickness(8, 3, 8, 3) : new Thickness(0, 3, 0, 3);
        Tint(button, active, icon, label);
        button.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HelpTextProperty, range ?? "All time");
    }

    /// <summary>"Apps" with a grid glyph by default; the chosen app's icon and name in accent otherwise.</summary>
    public static void StyleAppPill(Button button, Image image, FontIcon glyph, TextBlock label, DictationTargetApp? app)
    {
        var icon = app is null ? null : ComputerUseRowStyle.AppIcon(app);
        image.Source = icon;
        image.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        glyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        label.Text = app?.Name ?? "Apps";
        Tint(button, app is not null, glyph, label);
        ToolTipService.SetToolTip(button, app is null ? "Filter by destination app" : $"Showing dictations for {app.Name}");
        button.SetValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HelpTextProperty, app?.Name ?? "All apps");
    }

    private static void Tint(Button button, bool active, FontIcon icon, TextBlock label)
    {
        var tone = (Brush)Application.Current.Resources[active ? "MuesliAccentBrush" : "MuesliTextTertiaryBrush"];
        icon.Foreground = tone;
        label.Foreground = tone;
        if (active) button.Background = (Brush)Application.Current.Resources["MuesliAccentMutedBrush"];
        else button.ClearValue(Control.BackgroundProperty);
    }
}
