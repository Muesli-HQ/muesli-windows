using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// The single-project MSIX pipeline packs only @(PackagingOutputs); the Swift bridge/runtime is
/// staged after Build and must be added to the payload explicitly.
/// </summary>
public sealed class MsixPayloadContractTests
{
    private static string PackageScript() =>
        File.ReadAllText(TestRepositoryLayout.Combine("scripts", "package-winui-msix.ps1"));

    [Fact]
    public void MsixPackagingAddsTheSwiftBridgeClosureToThePayload()
    {
        var script = PackageScript();
        Assert.Contains("Rebuild-MsixPayload", script, StringComparison.Ordinal);
        Assert.Contains("muesli-swift-bridge-files.txt", script, StringComparison.Ordinal);
        Assert.Contains("makeappx", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MsixPackagingNoLongerShipsASeparateIndicatorCompanion()
    {
        // The floating indicator is drawn in-process; the retired WPF companion bundled a second
        // self-contained runtime (about 223 MB) under Indicator\.
        var script = PackageScript();
        Assert.DoesNotContain("'Indicator'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path $scratch 'Indicator'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixPackagingPrunesForeignRidContentAndShipsDisclosures()
    {
        var script = PackageScript();
        Assert.Contains("Test-ForeignRidPath", script, StringComparison.Ordinal);
        Assert.Contains("win-arm64", script, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("WINDOWS-PRIVACY.md", script, StringComparison.Ordinal);
        Assert.Contains("licenses", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixPackagingFailsLoudlyWhenMakeAppxIsUnavailable()
    {
        var script = PackageScript();
        Assert.Contains("MakeAppx.exe was not found", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MsixPackagingNormalizesTheNewestBuildAndPublishesAStableArtifactName()
    {
        var script = PackageScript();
        Assert.Contains("Sort-Object LastWriteTimeUtc -Descending", script, StringComparison.Ordinal);
        Assert.Contains("Muesli.Windows.WinUI_{0}_x64.msix", script, StringComparison.Ordinal);
        Assert.Contains("Get-MuesliReleaseProperties -Root $repoRoot", script, StringComparison.Ordinal);
        var metadataReader = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "read-release-properties.ps1"));
        Assert.Contains("Directory.Build.props", metadataReader, StringComparison.Ordinal);
    }
}
