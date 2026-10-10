using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Muesli.Windows.WinUI.Themes;

/// <summary>
/// Applies the user's accent colour app-wide, as macOS does with <c>MuesliTheme.accentOverrideHex</c>.
/// The theme brushes are retinted in place, so every element already bound to them updates live.
/// "1e1e2e" (Default) restores the original Muesli blues. High Contrast keeps its system brushes.
/// </summary>
public static class AccentPalette
{
    // ponytail: only brushes are retinted; the SystemAccentColor* Color ramp keeps Muesli blue, which
    // only shows in the few generic.xaml templates that read the ramp directly (e.g. TextBox focus underline).
    private static readonly string[] BaseKeys =
    [
        "MuesliAccentBrush", "MuesliBorderStrongBrush", "MuesliFocusInnerBrush",
        "AccentTextFillColorPrimaryBrush", "ControlStrongStrokeColorDefaultBrush", "FocusStrokeColorInnerBrush"
    ];
    private static readonly string[] StrongKeys =
        ["MuesliAccentStrongBrush", "AccentFillColorDefaultBrush", "SelectorBarItemBackgroundSelected"];

    private static readonly Dictionary<SolidColorBrush, Color> Originals = [];

    public static void Apply(string? hex)
    {
        var accent = Parse(hex);
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var (themeKey, value) in dictionary.ThemeDictionaries)
            {
                if (value is not ResourceDictionary theme || Equals(themeKey, "HighContrast")) continue;
                var light = Equals(themeKey, "Light");
                foreach (var key in BaseKeys) Set(theme, key, accent, c => c);
                foreach (var key in StrongKeys) Set(theme, key, accent, c => Mix(c, Black, 0.15));
                Set(theme, "MuesliAccentMutedBrush", accent, c => Color.FromArgb((byte)(light ? 0x1F : 0x22), c.R, c.G, c.B));
                Set(theme, "AccentFillColorSecondaryBrush", accent, c => Mix(c, Black, 0.08));
                Set(theme, "AccentFillColorTertiaryBrush", accent, c => Mix(c, Black, 0.22));
                // Secondary/tertiary accent text steps away from the background: lighter on dark, darker on light.
                var away = light ? Black : White;
                Set(theme, "AccentTextFillColorSecondaryBrush", accent, c => Mix(c, away, light ? 0.15 : 0.25));
                Set(theme, "AccentTextFillColorTertiaryBrush", accent, c => Mix(c, away, light ? 0.30 : 0.45));
            }
        }
    }

    private static void Set(ResourceDictionary theme, string key, Color? accent, Func<Color, Color> derive)
    {
        if (!theme.TryGetValue(key, out var value) || value is not SolidColorBrush brush) return;
        if (!Originals.TryGetValue(brush, out var original)) Originals[brush] = original = brush.Color;
        brush.Color = accent is { } color ? derive(color) : original;
    }

    private static Color? Parse(string? hex)
    {
        var value = (hex ?? "").Trim().TrimStart('#');
        if (value.Length != 6 || value.Equals("1e1e2e", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return null;
        return Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static readonly Color Black = Color.FromArgb(0xFF, 0, 0, 0);
    private static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));
}
