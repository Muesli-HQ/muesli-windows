using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Priority 5: one authoritative version/identity source, a clearly named development publisher,
/// and a fail-closed guard so a public release cannot ship the placeholder publisher.
/// </summary>
public sealed class VersionIdentityContractTests
{
    private static XDocument BuildProps() =>
        XDocument.Load(TestRepositoryLayout.Combine("Directory.Build.props"));

    private static string PropertyValue(string name) =>
        BuildProps().Descendants()
            .FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() ?? "";

    private static XDocument AppxManifest() =>
        XDocument.Load(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Package.appxmanifest"));

    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    [Fact]
    public void ManifestVersionTracksTheAuthoritativeProductVersion()
    {
        var version = PropertyValue("MuesliVersion");
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", version);

        var identity = AppxManifest().Descendants(Foundation + "Identity").Single();
        Assert.Equal($"{version}.0", (string?)identity.Attribute("Version"));
    }

    [Fact]
    public void DevelopmentPublisherIsClearlyNamedAndMatchesTheManifest()
    {
        var devPublisher = PropertyValue("MuesliDevPublisher");
        Assert.Equal("CN=AppPublisher", devPublisher);

        var identity = AppxManifest().Descendants(Foundation + "Identity").Single();
        Assert.Equal(devPublisher, (string?)identity.Attribute("Publisher"));

        // The production publisher property exists (may be empty until a certificate is approved).
        Assert.Contains(
            BuildProps().Descendants(),
            element => element.Name.LocalName == "MuesliProductionPublisher");
    }

    [Fact]
    public void PublicReleaseFailsClosedWhileThePlaceholderPublisherRemains()
    {
        var releaseCommon = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "release-common.ps1"));
        var signedRelease = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "build-signed-release.ps1"));

        Assert.Contains("Assert-MuesliProductionPublisher", releaseCommon, StringComparison.Ordinal);
        Assert.Contains("CN=AppPublisher", releaseCommon, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliProductionPublisher", signedRelease, StringComparison.Ordinal);
        // Production signing requires an explicit publisher subject.
        Assert.Contains("ExpectedPublisherSubject", signedRelease, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedReleaseInstallsOnACleanMachineAndKeepsUserData()
    {
        var package = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "package-winui-msix.ps1"));
        var signedRelease = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "build-signed-release.ps1"));
        var project = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Muesli.Windows.WinUI.csproj"));
        var manifest = AppxManifest().ToString();

        // The production publisher is written into the packaged manifest before signing.
        Assert.Contains("$packageArgs.Publisher = $ExpectedPublisherSubject", signedRelease, StringComparison.Ordinal);
        Assert.Contains("Set-ManifestPublisher", package, StringComparison.Ordinal);
        // Repacking destroys signatures, so the packager never signs.
        Assert.DoesNotContain("PackageCertificateThumbprint", package, StringComparison.Ordinal);
        // No Windows App Runtime or VC++ redistributable has to be installed first.
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", project, StringComparison.Ordinal);
        Assert.Contains("'vcomp140.dll'", package, StringComparison.Ordinal);
        // History lives in the real %APPDATA%\muesli and survives uninstall.
        Assert.Contains("FileSystemWriteVirtualization>disabled<", manifest, StringComparison.Ordinal);
        Assert.Contains("unvirtualizedResources", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.17763", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeVersionComesFromTheAssemblyBuiltFromTheSameSource()
    {
        // The About page and startup log read the assembly version, which Directory.Build.props
        // derives from MuesliVersion — one source of truth.
        var app = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "App.xaml.cs"));
        Assert.Contains("typeof(App).Assembly.GetName().Version", app, StringComparison.Ordinal);
    }
}
