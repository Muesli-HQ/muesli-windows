using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Tests;

public sealed class NeutralHotkeyContractTests
{
    [Theory]
    [InlineData("F8", 0x77, HotkeyModifiers.None, "F8")]
    [InlineData("control + shift + space", 0x20, HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift+Space")]
    [InlineData("Win+Alt+D", 0x44, HotkeyModifiers.Windows | HotkeyModifiers.Alt, "Alt+Win+D")]
    public void ParserProducesFrameworkNeutralVirtualKeyContract(
        string value,
        int expectedVirtualKey,
        HotkeyModifiers expectedModifiers,
        string expectedDisplayName)
    {
        var gesture = HotkeyGestureParser.Parse(value);

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(expectedModifiers, gesture.Modifiers);
        Assert.Equal(expectedDisplayName, gesture.DisplayName);
    }

    [Fact]
    public void MatchingUsesExactModifiers()
    {
        var gesture = HotkeyGestureParser.Parse("Ctrl+F8");

        Assert.True(HotkeyGestureParser.Matches(gesture, 0x77, HotkeyModifiers.Control));
        Assert.False(HotkeyGestureParser.Matches(gesture, 0x77, HotkeyModifiers.Control | HotkeyModifiers.Shift));
        Assert.False(HotkeyGestureParser.Matches(gesture, 0x76, HotkeyModifiers.Control));
    }

    [Fact]
    public void EscapeIsRecognizedAsReservedCancellationKey()
    {
        var gesture = HotkeyGestureParser.Parse("Esc");

        Assert.True(HotkeyGestureParser.IsEscape(gesture.VirtualKey));
        Assert.Equal("Escape", gesture.DisplayName);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("Ctrl+F8+D")]
    [InlineData("NotAKey")]
    public void ParserRejectsMissingOrAmbiguousKeys(string value)
    {
        Assert.Throws<InvalidOperationException>(() => HotkeyGestureParser.Parse(value));
    }

    [Theory]
    [InlineData("Right Ctrl", 0xA3, "Right Ctrl")]
    [InlineData("RightCtrl", 0xA3, "Right Ctrl")]
    [InlineData("rctrl", 0xA3, "Right Ctrl")]
    [InlineData("Right Alt", 0xA5, "Right Alt")]
    [InlineData("Right Shift", 0xA1, "Right Shift")]
    [InlineData("Right Win", 0x5C, "Right Win")]
    public void A_right_side_modifier_is_a_hold_key_on_its_own(string value, int expectedVirtualKey, string expectedDisplayName)
    {
        var gesture = HotkeyGestureParser.Parse(value);

        Assert.Equal(expectedVirtualKey, gesture.VirtualKey);
        Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        Assert.Equal(expectedDisplayName, gesture.DisplayName);
        Assert.True(HotkeyGestureParser.IsModifierOnly(gesture));
        // The saved display name parses back to the same gesture.
        Assert.Equal(gesture, HotkeyGestureParser.Parse(gesture.DisplayName));
    }

    [Theory]
    [InlineData("Ctrl+Right Alt")]
    [InlineData("Right Ctrl+C")]
    [InlineData("Right Ctrl+Right Shift")]
    public void A_right_side_modifier_is_refused_in_a_combination(string value)
    {
        Assert.Throws<InvalidOperationException>(() => HotkeyGestureParser.Parse(value));
    }

    [Fact]
    public void Ordinary_shortcuts_are_not_modifier_only()
    {
        Assert.False(HotkeyGestureParser.IsModifierOnly(HotkeyGestureParser.Parse("F8")));
        Assert.False(HotkeyGestureParser.IsModifierOnly(HotkeyGestureParser.Parse("Ctrl+Shift+Space")));
        Assert.True(HotkeyGestureParser.IsRightAlt(HotkeyGestureParser.Parse("Right Alt")));
        Assert.False(HotkeyGestureParser.IsRightAlt(HotkeyGestureParser.Parse("Right Ctrl")));
    }
}
