namespace Muesli.Windows.Services.Appearance;

/// <summary>
/// The accent roles the macOS app derives from one saved accent colour, ported from
/// <c>MuesliAccentPalette.swift</c> so both apps pick the same label ink, accent text and badge
/// fill. Colours are 0xRRGGBB integers; nothing here touches the UI.
/// </summary>
public sealed record MuesliAccentRoles(int Fill, int OnFill, int Text, int StateShade, int BadgeFill)
{
    public const int DefaultLightFill = 0x2563EB;
    public const int DefaultDarkFill = 0x6BA3F7;
    public const int DarkInk = 0x000000;

    /// <summary>Accent tint opacities for rest, hover and pressed (macOS <c>accentTint*</c>).</summary>
    public const double TintRest = 0.15;
    public const double TintHover = 0.21;
    public const double TintPressed = 0.27;

    /// <summary>Overlay opacities of <see cref="StateShade"/> on hover and press.</summary>
    public const double StateShadeHover = 0.08;
    public const double StateShadePressed = 0.16;

    private const double MinimumTextContrast = 6.0;
    // Headroom above the text target for 8-bit blending, as on macOS.
    private const double DerivationTarget = MinimumTextContrast + 0.1;
    private const int ShadeSteps = 50;

    // Opaque surfaces accent text must stay legible on: deep, base, raised, hover, surfacePrimary,
    // surfaceSelected (MuesliTheme.textSurfaceHexes). The first three also sit under accent tints.
    private static readonly int[] DarkSurfaces = [0x0B0C0E, 0x161719, 0x202226, 0x232528, 0x262830, 0x2E3340];
    private static readonly int[] LightSurfaces = [0xF5F5F7, 0xFFFFFF, 0xF0F0F2, 0xE8E8EC, 0xE5E5EA, 0xD6DFFE];

    /// <summary>The roles for a saved accent; null, empty, unparsable or "1e1e2e" mean the adaptive default.</summary>
    public static MuesliAccentRoles For(string? savedHex, bool dark)
    {
        var fill = ParseHex(savedHex) ?? (dark ? DefaultDarkFill : DefaultLightFill);
        return Derive(fill, dark);
    }

    public static MuesliAccentRoles Derive(int fill, bool dark)
    {
        const int white = 0xFFFFFF;
        var onFill = ContrastRatio(white, fill) >= ContrastRatio(DarkInk, fill) ? white : DarkInk;
        // Darken under white labels; lighten under dark ones, except near-white fills.
        var stateShade = onFill == white || Luminance(fill) > 0.6 ? DarkInk : white;
        return new MuesliAccentRoles(fill, onFill, AccentText(fill, dark), stateShade, DeriveBadgeFill(fill));
    }

    /// <summary>Parses a saved accent the way macOS does: any hex digits, low 24 bits.</summary>
    public static int? ParseHex(string? hex)
    {
        var digits = (hex ?? "").Trim().Replace("#", "", StringComparison.Ordinal).ToLowerInvariant();
        if (digits.Length == 0 || digits == "1e1e2e" ||
            !ulong.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return null;
        }

        return (int)(value & 0xFFFFFF);
    }

    /// <summary>WCAG 2.x relative luminance of an sRGB colour.</summary>
    public static double Luminance(int rgb)
    {
        static double Channel(int value)
        {
            var c = value / 255d;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }

    public static double ContrastRatio(int a, int b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Moves each channel <paramref name="amount"/> of the way toward <paramref name="target"/>, rounding half away from zero.</summary>
    public static int Mix(int color, int target, double amount)
    {
        int Channel(int shift)
        {
            double from = (color >> shift) & 0xFF;
            double to = (target >> shift) & 0xFF;
            return (int)Math.Round(from + (to - from) * amount, MidpointRounding.AwayFromZero);
        }

        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static int DeriveBadgeFill(int fill)
    {
        for (var step = 0; step <= ShadeSteps; step++)
        {
            var candidate = Mix(fill, DarkInk, (double)step / ShadeSteps);
            if (ContrastRatio(0xFFFFFF, candidate) >= 4.6) return candidate;
        }

        return DarkInk;
    }

    private static int AccentText(int fill, bool dark)
    {
        var surfaces = dark ? DarkSurfaces : LightSurfaces;
        var backgrounds = new List<int>(surfaces);
        foreach (var baseColor in surfaces.Take(3))
        {
            foreach (var tint in new[] { TintRest, TintHover, TintPressed })
            {
                backgrounds.Add(Mix(baseColor, fill, tint));
            }
        }

        var target = dark ? 0xFFFFFF : DarkInk;
        for (var step = 0; step <= ShadeSteps; step++)
        {
            var candidate = Mix(fill, target, (double)step / ShadeSteps);
            if (backgrounds.All(background => ContrastRatio(candidate, background) >= DerivationTarget)) return candidate;
        }

        return target;
    }
}
