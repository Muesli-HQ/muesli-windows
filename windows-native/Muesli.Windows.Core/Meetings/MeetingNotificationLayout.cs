using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Services;

/// <summary>
/// Framework-neutral geometry for the meeting-notification panel. Values mirror the shipping macOS
/// <c>MeetingNotificationController</c>; the HTML design system disagrees on several (fade-in,
/// corner radius, colours), and those discrepancies are recorded in docs/WINDOWS_UI_QUALIFICATION.md.
/// All values are DIPs in a top-left origin.
/// </summary>
public static class MeetingNotificationLayout
{
    public const double MinCardWidth = 344;
    public const double MaxSingleActionCardWidth = 420;
    public const double CardHeight = 60;
    public const double CardCornerRadius = 10;

    public const double CloseButtonSize = 22;
    public const double CloseButtonRadius = CloseButtonSize / 2;

    /// <summary>The card's left inset equals half the close button plus one, so the button overlaps the corner.</summary>
    public const double CardLeftInset = CloseButtonSize / 2 + 1;
    public const double TopGutter = CloseButtonSize / 2 + 1;
    public const double ScreenMargin = 16;

    public const double IconSize = 26;
    public const double IconLeftInset = 14;
    public const double IconTextGap = 9;
    public const double NoIconTextX = 14;

    public const double SplitButtonWidth = 126;
    public const double ChevronWidth = 24;
    public const double ActionHeight = 30;
    public const double ActionRadius = 6;
    public const double ActionTop = 15;
    public const double TrailingInset = 12;
    public const double ActionGap = 8;

    public const double ProgressBarHeight = 3;

    public const double TitleTop = 10;
    public const double SubtitleTop = 30;
    public const double BadgeTop = (CardHeight - IconSize) / 2 + 1;

    public const double SplitLabelHorizontalPadding = 12;

    public static double PanelWidth(double cardWidth) => cardWidth + CardLeftInset;

    public static double PanelHeight => CardHeight + TopGutter;

    public static double TextX(bool hasPlatformIcon) =>
        hasPlatformIcon ? IconLeftInset + IconSize + IconTextGap : NoIconTextX;

    public static double SplitButtonX(double cardWidth) =>
        cardWidth - (SplitButtonWidth + ChevronWidth) - TrailingInset;

    public static double SingleActionButtonX(double cardWidth) =>
        cardWidth - SplitButtonWidth - TrailingInset;

    /// <summary>Width available to the title/subtitle when a single action button shares the card.</summary>
    public static double SingleActionTextWidth(double cardWidth, double textX) =>
        Math.Max(0, cardWidth - TrailingInset - SplitButtonWidth - ActionGap - textX);

    /// <summary>Card width that fits the text and the single action button, clamped to [344, 420].</summary>
    public static double SingleActionCardWidth(double requiredTextWidth, double textX)
    {
        var required = Math.Ceiling(
            textX + requiredTextWidth + ActionGap + SplitButtonWidth + TrailingInset);
        return Math.Min(MaxSingleActionCardWidth, Math.Max(MinCardWidth, required));
    }

    /// <summary>Split-action cards are always the minimum width, matching macOS.</summary>
    public static double SplitActionCardWidth() => MinCardWidth;

    /// <summary>True when an action label fits its 126-DIP split segment.</summary>
    public static bool SplitButtonLabelFits(
        string label,
        double measuredWidthDip,
        double horizontalPadding = SplitLabelHorizontalPadding) =>
        measuredWidthDip + horizontalPadding <= SplitButtonWidth;

    /// <summary>
    /// Places the panel 16 DIPs from the top-right work-area edges, clamped so it stays fully visible
    /// even at negative monitor coordinates or on a work area narrower than the panel.
    /// </summary>
    public static DipPoint PlaceTopRight(DipRect workArea, double widthDip, double heightDip,
        double margin = ScreenMargin)
    {
        var maxLeft = workArea.Right - widthDip;
        var minLeft = workArea.Left + margin;
        var left = maxLeft >= minLeft ? Math.Clamp(maxLeft - margin, minLeft, maxLeft) : workArea.Left;

        var top = workArea.Top + margin;
        if (top + heightDip > workArea.Bottom)
        {
            top = Math.Max(workArea.Top, workArea.Bottom - heightDip);
        }

        return new DipPoint(left, top);
    }

    /// <summary>True when the whole panel lies inside the work area.</summary>
    public static bool FitsWithin(DipRect workArea, DipPoint topLeft, double widthDip, double heightDip) =>
        topLeft.X >= workArea.Left - 0.5 &&
        topLeft.Y >= workArea.Top - 0.5 &&
        topLeft.X + widthDip <= workArea.Right + 0.5 &&
        topLeft.Y + heightDip <= workArea.Bottom + 0.5;
}

/// <summary>
/// Deterministic, dependency-free text-width approximation used by the layout tests and as a
/// pre-measurement guard. The WinUI window still measures text with the real font and ellipsizes;
/// this exists so the "every action label fits" contract is testable without a UI thread.
/// </summary>
public static class MeetingNotificationTextMetrics
{
    private const double NarrowRatio = 0.32;
    private const double WideRatio = 0.94;
    private const double UpperRatio = 0.70;
    private const double DefaultRatio = 0.56;

    public static double EstimateWidthDip(string? text, double fontSizeDip)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var total = 0.0;
        foreach (var ch in text)
        {
            total += Ratio(ch);
        }

        return total * fontSizeDip;
    }

    private static double Ratio(char ch)
    {
        if (char.IsWhiteSpace(ch)) return 0.30;
        if (ch is 'i' or 'l' or 'j' or 't' or 'f' or 'r' or 'I' or '.' or ',' or '\'' or '!' or '|') return NarrowRatio;
        if (ch is 'm' or 'w' or 'M' or 'W' or '@' or '%') return WideRatio;
        if (char.IsUpper(ch)) return UpperRatio;
        return DefaultRatio;
    }
}
