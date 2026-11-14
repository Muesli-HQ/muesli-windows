using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI.Themes;

/// <summary>
/// Theme brushes for code that cannot use {ThemeResource}. Reading
/// <c>Application.Current.Resources[key]</c> resolves against the application theme (which follows
/// Windows), not the theme muesli is showing, so with muesli in Dark and Windows in Light it
/// returned light brushes (the white Shortcuts key cap). This looks the key up in the theme
/// dictionary that matches what the window actually shows.
/// </summary>
internal static class ThemeBrushes
{
    /// <summary>The brush for <paramref name="key"/> in the main window's current theme.</summary>
    public static Brush Get(string key) => Get(key, CurrentTheme);

    /// <summary>The brush for <paramref name="key"/> in <paramref name="theme"/>.</summary>
    public static Brush Get(string key, ElementTheme theme)
    {
        var themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : theme == ElementTheme.Light ? "Light" : "Dark";

        // Muesli's tokens live in the merged MuesliTheme dictionary; the last merge wins.
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed) &&
                themed is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out var value) &&
                value is Brush brush)
            {
                return brush;
            }
        }

        return Application.Current.Resources.TryGetValue(key, out var fallback) && fallback is Brush plain
            ? plain
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>
    /// Item templates evaluate their brush functions once per realized item, so after a theme
    /// switch the visible rows keep the old colours until they are realized again.
    /// </summary>
    public static void Rerealize(Microsoft.UI.Xaml.Controls.ItemsControl list)
    {
        var source = list.ItemsSource;
        list.ItemsSource = null;
        list.ItemsSource = source;
    }

    /// <summary>
    /// What the main window shows: its root's actual theme, which follows the Appearance choice;
    /// before the window exists, the application theme.
    /// </summary>
    public static ElementTheme CurrentTheme =>
        (App.Window?.Content as FrameworkElement)?.ActualTheme is { } theme && theme != ElementTheme.Default
            ? theme
            : Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
}
