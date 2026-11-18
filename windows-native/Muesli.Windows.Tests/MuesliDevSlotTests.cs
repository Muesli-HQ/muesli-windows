using Muesli.Windows.Core.Profiles;

namespace Muesli.Windows.Tests;

public sealed class MuesliDevSlotTests
{
    [Theory]
    [InlineData("MuesliDevA", "MuesliDevA")]
    [InlineData("MuesliDevB2", "MuesliDevB2")]
    // The shipping and Store identities, and an unpackaged run, are never a slot.
    [InlineData("Muesli.Windows", null)]
    [InlineData(null, null)]
    [InlineData("MuesliDev", null)]
    [InlineData("MuesliDev.A", null)]
    [InlineData("MuesliDevTooLongName", null)]
    public void OnlyDevelopmentPackageNamesAreSlots(string? packageName, string? expected) =>
        Assert.Equal(expected, MuesliDevSlot.Resolve(packageName));
}
