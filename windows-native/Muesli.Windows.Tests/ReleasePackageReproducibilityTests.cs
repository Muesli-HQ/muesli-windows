using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Muesli.Windows.Tests;

public sealed class ReleasePackageReproducibilityTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "THIRD-PARTY-NOTICES.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Muesli repository root.");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void OneCommandRehearsalIsNonSigningAndCpuOnly()
    {
        var rehearsal = Read("scripts/rehearse-windows-release.ps1");
        Assert.Contains("pwsh -ExecutionPolicy Bypass -File .\\scripts\\rehearse-windows-release.ps1", rehearsal, StringComparison.Ordinal);
        Assert.Contains("Does not sign", rehearsal, StringComparison.Ordinal);
        Assert.Contains("cudaProviderIncluded = $false", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-windows-release.ps1", rehearsal, StringComparison.Ordinal);
        Assert.Contains("SIGN-01", rehearsal, StringComparison.Ordinal);
        Assert.Contains("L07", rehearsal, StringComparison.Ordinal);
        Assert.Contains("write-package-content-inventory.ps1", Read("scripts/package-windows-v1.ps1"), StringComparison.Ordinal);
        Assert.Contains("generate-native-runtime-inventory.ps1", Read("scripts/package-windows-v1.ps1"), StringComparison.Ordinal);
    }

    [Fact]
    public void PackageScriptPinsDeterministicReleaseIdentityAndRejectsDirtyInputs()
    {
        var package = Read("scripts/package-windows-v1.ps1");
        var common = Read("scripts/release-common.ps1");
        var assertInputs = Read("scripts/assert-release-inputs.ps1");
        Assert.Contains("Assert-MuesliCleanReleaseInputs", package, StringComparison.Ordinal);
        Assert.Contains("AllowDirty", package, StringComparison.Ordinal);
        Assert.Contains("Deterministic=true", common, StringComparison.Ordinal);
        Assert.Contains("ContinuousIntegrationBuild=true", common, StringComparison.Ordinal);
        Assert.Contains("global.json", common, StringComparison.Ordinal);
        Assert.Contains("Refusing to build a release package from a dirty work tree", common, StringComparison.Ordinal);
        Assert.Contains("dirty-release-override.json", common, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliCleanReleaseInputs", assertInputs, StringComparison.Ordinal);
        Assert.Contains("Get-MuesliReleaseProperties", package, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-windows-release.ps1", package, StringComparison.Ordinal);
        Assert.Contains("cudaProviderIncluded = $false", package, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflowMatchesLocalUnsignedRehearsalAndUploadsReviewArtifacts()
    {
        var workflow = Read(".github/workflows/windows-ci.yml");
        Assert.Contains("runs-on: windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("global-json-file: global.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-version: 10.0.x", workflow, StringComparison.Ordinal);
        Assert.Contains("rehearse-windows-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("choco install innosetup", workflow, StringComparison.Ordinal);
        Assert.Contains("Authenticode signing is L05", workflow, StringComparison.Ordinal);
        Assert.Contains("Do not call scripts/sign-windows-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/muesli-windows-*-win-x64.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/MuesliSetup-*-win-x64.exe", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/test-results/*.trx", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/package-smoke-report.json", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/muesli-win32-manifest.xml", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/release-hashes.json", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/native-runtime-inventory.json", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/package-content-inventory.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-windows-release.ps1", workflow.Replace("Do not call scripts/sign-windows-release.ps1 from this workflow.", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void ContentInventoryIgnoresZipEntryTimestamps()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "muesli-l04-inventory-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(scratch, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            File.WriteAllText(Path.Combine(payload, "readme.txt"), "Muesli CPU-only package");
            File.WriteAllBytes(Path.Combine(payload, "payload.bin"), [0x4D, 0x5A, 0x90, 0x00]);
            var zip1 = Path.Combine(scratch, "a.zip");
            var zip2 = Path.Combine(scratch, "b.zip");
            File.SetLastWriteTimeUtc(Path.Combine(payload, "readme.txt"), new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            ZipFile.CreateFromDirectory(payload, zip1);
            File.SetLastWriteTimeUtc(Path.Combine(payload, "readme.txt"), new DateTime(2024, 6, 3, 12, 0, 0, DateTimeKind.Utc));
            ZipFile.CreateFromDirectory(payload, zip2);

            var inventory1 = Path.Combine(scratch, "inv1.json");
            var inventory2 = Path.Combine(scratch, "inv2.json");
            RunInventory(zip1, inventory1);
            RunInventory(zip2, inventory2);

            using var first = JsonDocument.Parse(File.ReadAllText(inventory1));
            using var second = JsonDocument.Parse(File.ReadAllText(inventory2));
            Assert.True(first.RootElement.GetProperty("ignoresZipEntryTimestamps").GetBoolean());
            Assert.False(first.RootElement.GetProperty("publicPackage").GetProperty("cudaProviderIncluded").GetBoolean());
            Assert.Equal(
                first.RootElement.GetProperty("contentDigest").GetString(),
                second.RootElement.GetProperty("contentDigest").GetString());
            Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip1))),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip2))),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }

    private static void RunInventory(string zipPath, string outputPath)
    {
        var script = Path.Combine(RepositoryRoot, "scripts", "write-package-content-inventory.ps1");
        var start = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ZipPath \"{zipPath}\" -OutputPath \"{outputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepositoryRoot
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh failed to start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"content inventory failed ({process.ExitCode}): {stdout}{stderr}");
        Assert.True(File.Exists(outputPath), outputPath);
    }
}
