using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Services.Appearance;
using Windows.UI;

namespace Muesli.Windows.WinUI.Themes;

/// <summary>
/// Applies the user's accent colour app-wide with the macOS accent roles
/// (<see cref="MuesliAccentRoles"/>): the raw accent fills buttons and toggles, the label ink on it
/// is black or white by contrast, accent text is a same-hue shade with 6:1 contrast, and tints are
/// 15/21/27%. The theme brushes are retinted in place, so every bound element updates live.
/// High Contrast keeps its system brushes.
/// </summary>
public static class AccentPalette
{
    // ponytail: brushes MuesliTheme.xaml declares are retinted; generic.xaml brushes that alias the
    // SystemAccentColor* ramp with StaticResource (toggle on-fill, progress) keep the XAML ramp,
    // which matches the default accent. Redeclare one there to make it follow a custom accent.
    private static readonly string[] FillKeys =
    [
        "MuesliAccentBrush", "MuesliAccentStrongBrush", "AccentFillColorDefaultBrush", "MuesliBorderStrongBrush",
        "ControlStrongStrokeColorDefaultBrush", "AccentFillColorSelectedTextBackgroundBrush",
        "TextControlElevationBorderFocusedBrush", "AccentButtonBackground", "ToggleSwitchFillOn", "ProgressBarForeground"
    ];
    private static readonly string[] HoverKeys =
        ["AccentFillColorSecondaryBrush", "AccentButtonBackgroundPointerOver", "ToggleSwitchFillOnPointerOver"];
    private static readonly string[] PressedKeys =
        ["AccentFillColorTertiaryBrush", "AccentButtonBackgroundPressed", "ToggleSwitchFillOnPressed"];
    private static readonly string[] TextKeys =
    [
        "MuesliAccentTextBrush", "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush", "MuesliFocusInnerBrush", "FocusStrokeColorInnerBrush"
    ];
    private static readonly string[] OnFillKeys =
    [
        "MuesliOnAccentBrush", "TextOnAccentFillColorPrimaryBrush", "AccentButtonForeground",
        "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed"
    ];

    public static void Apply(string? hex)
    {
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var (themeKey, value) in dictionary.ThemeDictionaries)
            {
                if (value is not ResourceDictionary theme || Equals(themeKey, "HighContrast")) continue;
                var roles = MuesliAccentRoles.For(hex, dark: !Equals(themeKey, "Light"));
                var fill = ToColor(roles.Fill);
                var shade = ToColor(roles.StateShade);
                foreach (var key in FillKeys) Set(theme, key, fill);
                foreach (var key in TextKeys) Set(theme, key, ToColor(roles.Text));
                foreach (var key in OnFillKeys) Set(theme, key, ToColor(roles.OnFill));
                Set(theme, "TextOnAccentFillColorSecondaryBrush", WithAlpha(ToColor(roles.OnFill), 0xC8));
                foreach (var key in HoverKeys) Set(theme, key, Over(fill, shade, MuesliAccentRoles.StateShadeHover));
                foreach (var key in PressedKeys) Set(theme, key, Over(fill, shade, MuesliAccentRoles.StateShadePressed));
                Set(theme, "MuesliAccentMutedBrush", Tint(fill, MuesliAccentRoles.TintRest));
                Set(theme, "MuesliAccentTintHoverBrush", Tint(fill, MuesliAccentRoles.TintHover));
                Set(theme, "MuesliAccentTintPressedBrush", Tint(fill, MuesliAccentRoles.TintPressed));
                Set(theme, "MuesliBadgeFillBrush", ToColor(roles.BadgeFill));
            }
        }
    }

    private static void Set(ResourceDictionary theme, string key, Color color)
    {
        if (!theme.TryGetValue(key, out var value)) return;
        switch (value)
        {
            case SolidColorBrush brush:
                brush.Color = color;
                break;
            // A gradient's first stop is the accent; the second is the neutral control stroke.
            case LinearGradientBrush gradient when gradient.GradientStops.Count > 0:
                gradient.GradientStops[0].Color = color;
                break;
        }
    }

    private static Color ToColor(int rgb) => Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Tint(Color color, double opacity) =>
        WithAlpha(color, (byte)Math.Round(opacity * 255, MidpointRounding.AwayFromZero));

    // An opaque fill with a state-shade overlay composited on top (macOS accentStateShade).
    private static Color Over(Color fill, Color overlay, double opacity)
    {
        var mixed = MuesliAccentRoles.Mix(
            (fill.R << 16) | (fill.G << 8) | fill.B,
            (overlay.R << 16) | (overlay.G << 8) | overlay.B,
            opacity);
        return ToColor(mixed);
    }
}
