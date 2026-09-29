using Microsoft.UI.Xaml;

namespace Muesli.Windows.WinUI;

/// <summary>
/// The one place that decides how much horizontal gutter and vertical rhythm a shell-hosted
/// page gets at a given content width.
/// </summary>
/// <remarks>
/// <para>
/// P1-04. Setting <c>Padding="{StaticResource MuesliPageContentPadding}"</c> in each page's XAML
/// was not sufficient: nine pages recompute that padding from code-behind on every size change,
/// and they did so with five different breakpoint pairs (700/980, 760/1000, 720/1000, and two
/// that keyed off the *window* width at 840/1008/1280) and four different vertical rhythms
/// (26/40, 26/44, 24/42, 20/32). Measured on the shipping default window (1280x820 DIP, 125 %
/// scale) the content-block left edge landed at 379 px on Meetings and Dictionary, 387 px on
/// Timeline / Dictations / Settings / Insights, 394 px on Models and About and 428 px on
/// Shortcuts — a 49 px (39 DIP) spread, because 1280 DIP of window happens to fall on opposite
/// sides of the 980 and 1000 thresholds.
/// </para>
/// <para>
/// Every page now calls <see cref="ContentPadding"/> instead of building its own
/// <see cref="Thickness"/>. Pages keep their own <c>compact</c> flags for genuinely page-specific
/// decisions (stacking an action row, shortening a label); only the gutter and the top/bottom
/// rhythm are centralised. The three thicknesses live in <c>App.xaml</c> so a later prompt can
/// retune the shell without touching nine files.
/// </para>
/// <para>
/// Breakpoints are expressed in *page content* width, not window width, because the rail width
/// changes with the window: 1280 DIP of window leaves ~990 DIP of content, 1008 leaves ~772 and
/// 720 leaves ~648. The 700 / 900 thresholds put each of the three qualification sizes in the
/// middle of a band rather than on its edge.
/// </para>
/// </remarks>
internal static class MuesliPageMetrics
{
    /// <summary>Content widths below this use the compact gutter.</summary>
    public const double CompactContentWidth = 700;

    /// <summary>Content widths below this (and at or above <see cref="CompactContentWidth"/>) use the medium gutter.</summary>
    public const double MediumContentWidth = 900;

    private static readonly Thickness LargeFallback = new(34, 26, 34, 40);
    private static readonly Thickness MediumFallback = new(28, 26, 28, 40);
    private static readonly Thickness CompactFallback = new(20, 22, 20, 32);

    public static bool IsCompact(double contentWidth) => contentWidth > 0 && contentWidth < CompactContentWidth;

    public static bool IsMedium(double contentWidth) =>
        contentWidth >= CompactContentWidth && contentWidth < MediumContentWidth;

    /// <summary>
    /// Page padding for the given content width. A non-positive width means the page has not
    /// been measured yet; the large value is returned so first layout matches the XAML default.
    /// </summary>
    public static Thickness ContentPadding(double contentWidth)
    {
        if (IsCompact(contentWidth))
            return Token("MuesliPageContentPaddingCompact", CompactFallback);
        if (IsMedium(contentWidth))
            return Token("MuesliPageContentPaddingMedium", MediumFallback);
        return Token("MuesliPageContentPadding", LargeFallback);
    }

    /// <summary>Horizontal gutter only, for pages that need to align a sibling to the page edge.</summary>
    public static double Gutter(double contentWidth) => ContentPadding(contentWidth).Left;

    private static T Token<T>(string key, T fallback)
    {
        var resources = Application.Current?.Resources;
        if (resources is not null && resources.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return fallback;
    }
}
