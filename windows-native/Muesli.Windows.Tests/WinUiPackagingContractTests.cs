using System.Xml.Linq;

namespace Muesli.Windows.Tests;

/// <summary>
/// Static contracts for the WinUI MSIX package. Each of these was a real packaging failure:
/// an extension category declared in the wrong namespace and a token that is only substituted
/// for the application executable both stopped <c>MakeAppx</c> before a package existed, and the
/// template's trimming default silently removed reflection-based JSON serialization the
/// persistence layer depends on.
/// </summary>
public sealed class WinUiPackagingContractTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap5 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";

    [Fact]
    public void StartupTaskExtensionUsesTheUap5NamespaceAndARealExecutableName()
    {
        var manifest = XDocument.Load(WinUiPath("Package.appxmanifest"));
        var application = manifest
            .Descendants(Foundation + "Application")
            .Single();
        var extension = application
            .Descendants(Uap5 + "Extension")
            .Single(element => (string?)element.Attribute("Category") == "windows.startupTask");

        // desktop6:Extension does not accept windows.startupTask; deployment rejects the manifest.
        Assert.Equal(Uap5, extension.Name.Namespace);

        // $targetnametoken$ is only substituted for Application/@Executable. Inside an extension it
        // reaches MakeAppx verbatim and fails payload validation.
        var executable = (string?)extension.Attribute("Executable");
        Assert.Equal("Muesli.Windows.WinUI.exe", executable);
        Assert.Equal("Windows.FullTrustApplication", (string?)extension.Attribute("EntryPoint"));

        var startupTask = extension.Elements(Uap5 + "StartupTask").Single();
        Assert.Equal("MuesliWinUiStartup", (string?)startupTask.Attribute("TaskId"));
    }

    [Fact]
    public void PackagedShellDoesNotTrimReflectionBasedSerialization()
    {
        var project = File.ReadAllText(WinUiPath("Muesli.Windows.WinUI.csproj"));
        var document = XDocument.Parse(project);
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName == "PublishTrimmed")
            .Select(element => element.Value.Trim())
            .ToList();

        Assert.NotEmpty(values);
        Assert.All(
            values,
            value => Assert.False(
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                "The packaged WinUI shell must not be trimmed while settings and history are " +
                "serialized reflectively; trimming removes the required metadata silently."));
    }

    [Fact]
    public void PackageIconIsARealWindowsIconRatherThanTheTemplatePlaceholder()
    {
        var packaged = WinUiPath(Path.Combine("Assets", "AppIcon.ico"));
        Assert.True(File.Exists(packaged), $"The WinUI package icon is missing at {packaged}.");
        var bytes = File.ReadAllBytes(packaged);
        Assert.True(bytes.Length > 1024, "The product icon is unexpectedly small.");
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
    }

    [Fact]
    public void CoreRepairsTheMislinkedLLamaSharpNativeRuntimes()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "windows-native",
            "Muesli.Windows.Core",
            "Muesli.Windows.Core.csproj"));

        // Without this target the backend package globs every platform's natives and links them
        // under "runtimes\\<rid>", which MakeAppx rejects and which ships unusable payload.
        Assert.Contains("NormalizeLLamaSharpNativeRuntimes", project, StringComparison.Ordinal);
    }

    private static string WinUiPath(string relativePath) =>
        Path.Combine(RepositoryRoot(), "windows-native", "Muesli.Windows.WinUI", relativePath);

    private static string RepositoryRoot() => TestRepositoryLayout.Root;
}
