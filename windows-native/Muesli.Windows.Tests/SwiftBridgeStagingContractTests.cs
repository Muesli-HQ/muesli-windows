using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Priority 2: the bridge staging script must be content-hashed, self-describing, and must not let a
/// skipped build masquerade as a native-enabled one.
/// </summary>
public sealed class SwiftBridgeStagingContractTests
{
    private static string Script() =>
        File.ReadAllText(TestRepositoryLayout.Combine("scripts", "build-swift-core-bridge.ps1"));

    [Fact]
    public void StagingIsContentHashedRatherThanTimestampOnly()
    {
        var script = Script();
        Assert.Contains("Get-SwiftSourceHash", script, StringComparison.Ordinal);
        Assert.Contains("sourceHash", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileSha256", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingWritesAMachineReadableManifestAndReStagesAppx()
    {
        var script = Script();
        Assert.Contains("muesli-swift-bridge-manifest.tsv", script, StringComparison.Ordinal);
        Assert.Contains("re-staged and verified", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SkippedStagingQuarantinesStaleBridgeOutput()
    {
        var script = Script();
        Assert.Contains("Remove-StagedBridge", script, StringComparison.Ordinal);
        Assert.Contains("Quarantined stale bridge output", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredRuntimeDependenciesAreVerifiedAfterStaging()
    {
        var script = Script();
        Assert.Contains("Required native dependency", script, StringComparison.Ordinal);
        Assert.Contains("missing or empty after staging", script, StringComparison.Ordinal);
    }
}
