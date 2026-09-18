using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Priority 2: Windows release CI must build the shared Swift bridge from an immutable revision.
/// These contracts fail if CI can silently drift back to a moving branch.
/// </summary>
public sealed class SharedCorePinningContractTests
{
    private static JsonElement Lock()
    {
        var path = TestRepositoryLayout.Combine("windows-native", "shared-core.lock.json");
        Assert.True(File.Exists(path), $"Shared-core lock file is missing at {path}.");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    [Fact]
    public void LockFileNamesTheSharedRepositoryAndPackage()
    {
        var root = Lock();
        Assert.Equal("Muesli-HQ/muesli", root.GetProperty("repository").GetString());
        Assert.Equal("native/MuesliNative", root.GetProperty("packagePath").GetString());
        Assert.True(root.TryGetProperty("revision", out _));
    }

    [Fact]
    public void RequiredAbiCapabilitiesIncludePersistenceAndTextProcessing()
    {
        var capabilities = Lock().GetProperty("requiredAbiCapabilities").GetInt32();
        Assert.True((capabilities & (1 << 0)) != 0, "Persistence capability must be required.");
        Assert.True((capabilities & (1 << 1)) != 0, "Text-processing capability must be required.");
    }

    [Fact]
    public void WindowsCiResolvesTheSharedRepositoryFromTheLockWithAnEmptyRevisionGuard()
    {
        var workflow = File.ReadAllText(TestRepositoryLayout.Combine(
            ".github", "workflows", "windows-ci.yml"));

        Assert.Contains("shared-core.lock.json", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ steps.shared_core_lock.outputs.revision }}", workflow, StringComparison.Ordinal);
        Assert.Contains("has no pinned revision", workflow, StringComparison.Ordinal);
        Assert.Contains("moving branch", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalReleasePackagingRefusesAnEmptyRevisionUnlessDevOverrideIsExplicit()
    {
        var common = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "release-common.ps1"));
        var rehearsal = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "rehearse-windows-release.ps1"));

        Assert.Contains("function Assert-MuesliSharedCoreRevision", common, StringComparison.Ordinal);
        Assert.Contains("MUESLI_SHARED_CORE_PACKAGE", common, StringComparison.Ordinal);
        Assert.Contains("is NOT reproducible release evidence", common, StringComparison.Ordinal);
        Assert.Contains("AllowUnpinnedDevOverride", common, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliSharedCoreRevision", rehearsal, StringComparison.Ordinal);
        Assert.Contains("sharedCoreReleaseEvidence", rehearsal, StringComparison.Ordinal);

        var runbook = File.ReadAllText(TestRepositoryLayout.Combine("docs", "SHARED_CORE_PINNING.md"));
        Assert.Contains("rev-parse HEAD", runbook, StringComparison.Ordinal);
        Assert.Contains("revision", runbook, StringComparison.Ordinal);
    }
}
