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
}
