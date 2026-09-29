using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Stage 3 shared-core pinning gate. A release build must resolve the shared Swift package from an
/// immutable 40-character commit SHA; empty revisions, branch names, and short revisions fail, and
/// the development override is recorded as non-release evidence.
/// </summary>
public sealed class SharedCoreRevisionGateTests
{
    private static string RepositoryRoot => TestRepositoryLayout.Root;

    private static (string Root, string LockPath) CreateRoot(string revision, bool bridgeRequired = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "muesli-pin-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "windows-native");
        Directory.CreateDirectory(dir);
        var lockPath = Path.Combine(dir, "shared-core.lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            repository = "Muesli-HQ/muesli",
            packagePath = "native/MuesliNative",
            revision,
            requiredAbiCapabilities = 3,
            bridgeRequired
        }));
        return (root, lockPath);
    }

    private static (int ExitCode, string Output) InvokeGate(
        string root,
        bool allowDevOverride,
        string? sharedCorePackage = null)
    {
        var common = Path.Combine(RepositoryRoot, "scripts", "release-common.ps1");
        var runner = Path.Combine(root, "run-gate.ps1");
        File.WriteAllText(runner, $$"""
            param([string]$Root, [switch]$AllowUnpinnedDevOverride)
            . '{{common}}'
            try {
              $r = Assert-MuesliSharedCoreRevision -Root $Root -AllowUnpinnedDevOverride:$AllowUnpinnedDevOverride
              Write-Output ("PINNED={0} REV={1} DEV={2} MODE={3} BRIDGE={4}" -f $r.Pinned, $r.Revision, $r.DevOverride, $r.Mode, $r.BridgeRequired)
            } catch {
              Write-Output ("ERROR: " + $_.Exception.Message)
              exit 1
            }
            """);

        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{runner}\" -Root \"{root}\"";
        if (allowDevOverride) arguments += " -AllowUnpinnedDevOverride";
        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot
        };
        if (sharedCorePackage is not null)
            start.Environment["MUESLI_SHARED_CORE_PACKAGE"] = sharedCorePackage;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh failed to start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    [Fact]
    public void A_full_commit_sha_is_pinned_clean()
    {
        var (root, _) = CreateRoot("a5414b301742c968d8063683bc04e858a48e6a77");
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.True(exitCode == 0, output);
            Assert.Contains("PINNED=True", output, StringComparison.Ordinal);
            Assert.Contains("a5414b301742c968d8063683bc04e858a48e6a77", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_empty_revision_fails_without_an_explicit_development_override()
    {
        var (root, _) = CreateRoot("");
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("empty revision", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_unpinned_lock_that_ships_the_managed_fallback_does_not_require_the_bridge()
    {
        var (root, _) = CreateRoot("", bridgeRequired: false);
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.True(exitCode == 0, output);
            Assert.Contains("PINNED=False", output, StringComparison.Ordinal);
            Assert.Contains("MODE=ManagedFallback", output, StringComparison.Ordinal);
            Assert.Contains("BRIDGE=False", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Managed_fallback_rejects_a_pinned_revision()
    {
        var (root, _) = CreateRoot("a5414b301742c968d8063683bc04e858a48e6a77", bridgeRequired: false);
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("bridgeRequired=false but still pins revision", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_pinned_lock_reports_the_bridge_as_required()
    {
        var (root, _) = CreateRoot("a5414b301742c968d8063683bc04e858a48e6a77");
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.True(exitCode == 0, output);
            Assert.Contains("MODE=Pinned", output, StringComparison.Ordinal);
            Assert.Contains("BRIDGE=True", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("main")]
    [InlineData("v1.2.3")]
    [InlineData("a5414b3")]
    public void A_moving_branch_or_short_revision_fails(string revision)
    {
        var (root, _) = CreateRoot(revision);
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: false);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("40-character commit SHA", output, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_development_override_is_recorded_as_nonrelease_evidence()
    {
        var (root, _) = CreateRoot("");
        try
        {
            var (exitCode, output) = InvokeGate(root, allowDevOverride: true,
                sharedCorePackage: @"C:\shared\MuesliNative");
            Assert.True(exitCode == 0, output);
            Assert.Contains("PINNED=False", output, StringComparison.Ordinal);
            Assert.Contains(@"C:\shared\MuesliNative", output, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Pinned_lock_matches_the_bridge_capability_mask()
    {
        using var lockDocument = JsonDocument.Parse(File.ReadAllText(
            TestRepositoryLayout.Combine("windows-native", "shared-core.lock.json")));
        Assert.Equal(3, lockDocument.RootElement.GetProperty("requiredAbiCapabilities").GetInt32());
        Assert.Equal("Muesli-HQ/muesli", lockDocument.RootElement.GetProperty("repository").GetString());
        // The lock records the deliberate shipping decision: no approved upstream ABI commit exists,
        // so the release ships the parity-tested managed text processor rather than a moving branch.
        Assert.False(lockDocument.RootElement.GetProperty("bridgeRequired").GetBoolean());
        Assert.Equal("managed-fallback", lockDocument.RootElement.GetProperty("shippingMode").GetString());
    }
}
