using Muesli.Windows.Services.Appearance;

namespace Muesli.Windows.Tests;

/// <summary>
/// The accent roles must match the macOS app (MuesliAccentPalette.swift) for every preset, so a
/// saved accent looks the same on both platforms: label ink on fills, accent text, badge fill.
/// </summary>
public sealed class MuesliAccentRolesTests
{
    [Theory]
    // saved hex, dark, fill, onFill, text, stateShade, badgeFill
    [InlineData(null, false, 0x2563EB, 0xFFFFFF, 0x163B8D, 0x000000, 0x2563EB)]
    [InlineData(null, true, 0x6BA3F7, 0x000000, 0xB2CFFB, 0xFFFFFF, 0x4D75B2)]
    [InlineData("1e1e2e", true, 0x6BA3F7, 0x000000, 0xB2CFFB, 0xFFFFFF, 0x4D75B2)]
    [InlineData("2563eb", true, 0x2563EB, 0xFFFFFF, 0x9BB7F6, 0x000000, 0x2563EB)]
    [InlineData("ef4444", false, 0xEF4444, 0x000000, 0x7C2323, 0xFFFFFF, 0xD23C3C)]
    [InlineData("ef4444", true, 0xEF4444, 0x000000, 0xF8A9A9, 0xFFFFFF, 0xD23C3C)]
    [InlineData("#F59E0B", false, 0xF59E0B, 0x000000, 0x6C4605, 0xFFFFFF, 0xA26807)]
    [InlineData("10b981", true, 0x10B981, 0x000000, 0x88DCC0, 0xFFFFFF, 0x0C855D)]
    [InlineData("8b5cf6", false, 0x8B5CF6, 0x000000, 0x4E348A, 0xFFFFFF, 0x8356E7)]
    [InlineData("ec4899", true, 0xEC4899, 0x000000, 0xF6A7CE, 0xFFFFFF, 0xCB3E84)]
    public void Roles_match_the_macos_palette(string? hex, bool dark, int fill, int onFill, int text, int shade, int badge)
    {
        var roles = MuesliAccentRoles.For(hex, dark);

        Assert.Equal(fill.ToString("X6"), roles.Fill.ToString("X6"));
        Assert.Equal(onFill.ToString("X6"), roles.OnFill.ToString("X6"));
        Assert.Equal(text.ToString("X6"), roles.Text.ToString("X6"));
        Assert.Equal(shade.ToString("X6"), roles.StateShade.ToString("X6"));
        Assert.Equal(badge.ToString("X6"), roles.BadgeFill.ToString("X6"));
    }

    [Fact]
    public void Accent_text_clears_six_to_one_on_every_surface()
    {
        foreach (var dark in new[] { false, true })
        {
            foreach (var hex in new[] { "2563eb", "ef4444", "f59e0b", "10b981", "8b5cf6", "ec4899", "abcdef" })
            {
                var roles = MuesliAccentRoles.For(hex, dark);
                var surface = dark ? 0x161719 : 0xFFFFFF;
                Assert.True(MuesliAccentRoles.ContrastRatio(roles.Text, surface) >= 6.0, $"{hex} dark={dark}");
            }
        }
    }
}
