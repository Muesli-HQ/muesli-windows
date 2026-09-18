using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class StartupRegistrationServiceTests
{
    private const string CurrentExecutable = @"C:\Users\madhav\AppData\Local\Muesli\Muesli.Windows.WinUI.exe";

    [Fact]
    public void ExactCurrentBackgroundCommandIsValid()
    {
        var command = StartupRegistrationService.BuildBackgroundCommand(CurrentExecutable);

        Assert.True(StartupRegistrationService.IsCommandForExecutable(command, CurrentExecutable));
    }

    [Fact]
    public void StaleExecutableIsNotAcceptedBecauseItHasBackgroundArgument()
    {
        const string stale =
            "\"C:\\Users\\madhav\\projects\\muesli\\windows-native\\Muesli.Windows.WinUI\\bin\\x64\\Debug\\Muesli.Windows.WinUI.exe\" --background";

        Assert.False(StartupRegistrationService.IsCommandForExecutable(stale, CurrentExecutable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Muesli.Windows.WinUI.exe")]
    [InlineData("\"C:\\Users\\madhav\\AppData\\Local\\Muesli\\Muesli.Windows.WinUI.exe\"")]
    [InlineData("\"C:\\Users\\madhav\\AppData\\Local\\Muesli\\Muesli.Windows.WinUI.exe\" --startup")]
    [InlineData("\"C:\\Users\\madhav\\AppData\\Local\\Muesli\\Muesli.Windows.WinUI.exe\" --background --unexpected")]
    public void IncompleteOrUnexpectedCommandsAreRejected(string? command)
    {
        Assert.False(StartupRegistrationService.IsCommandForExecutable(command, CurrentExecutable));
    }

    [Fact]
    public void CommandComparisonIsCaseInsensitiveButNotPathAgnostic()
    {
        var command = StartupRegistrationService.BuildBackgroundCommand(CurrentExecutable).ToUpperInvariant();

        Assert.True(StartupRegistrationService.IsCommandForExecutable(command, CurrentExecutable));
        Assert.False(StartupRegistrationService.IsCommandForExecutable(
            command,
            @"C:\Users\madhav\AppData\Local\Muesli-Preview\Muesli.Windows.WinUI.exe"));
    }
}
