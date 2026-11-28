using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>The Settings position picker (macOS Appearance > Recording Indicator > Position).</summary>
public sealed class IndicatorPositionPresetTests
{
    [Fact]
    public void Offers_the_macos_positions_without_notch_or_an_unsaved_custom()
    {
        Assert.Equal(
            ["Top Left", "Top Center", "Top Right", "Middle Left", "Middle Right", "Bottom Left", "Bottom Center", "Bottom Right"],
            FloatingIndicatorLayout.PositionChoices(new MuesliSettings()));
        Assert.DoesNotContain("Notch", FloatingIndicatorLayout.PositionChoices(new MuesliSettings { IndicatorLeft = 10, IndicatorTop = 10 }));
    }

    [Fact]
    public void Offers_custom_once_a_dragged_position_is_saved()
    {
        var dragged = new MuesliSettings { IndicatorAnchor = "Custom", IndicatorLeft = 1110, IndicatorTop = 19 };
        Assert.Equal("Custom", FloatingIndicatorLayout.PositionChoices(dragged)[^1]);
        Assert.Equal("Custom", FloatingIndicatorLayout.PositionChoice(dragged));
        // A preset chosen later keeps the dragged spot, so Custom stays on offer.
        Assert.Equal("Top Center", FloatingIndicatorLayout.PositionChoice(dragged with { IndicatorAnchor = "Top Center" }));
        Assert.Contains("Custom", FloatingIndicatorLayout.PositionChoices(dragged with { IndicatorAnchor = "Top Center" }));
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("Notch")]
    [InlineData("somewhere")]
    public void Unknown_or_unusable_values_read_as_the_default(string anchor) =>
        Assert.Equal("Middle Right", FloatingIndicatorLayout.PositionChoice(new MuesliSettings { IndicatorAnchor = anchor }));

    [Fact]
    public void Settings_page_binds_the_picker_and_saves_the_anchor()
    {
        var xaml = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Pages", "SettingsPage.xaml"));
        var viewModel = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "ViewModels", "SettingsPageViewModel.cs"));
        Assert.Contains("IndicatorPositionPicker", xaml, StringComparison.Ordinal);
        Assert.Contains("settings with { IndicatorAnchor = IndicatorPosition }", viewModel, StringComparison.Ordinal);
    }
}
