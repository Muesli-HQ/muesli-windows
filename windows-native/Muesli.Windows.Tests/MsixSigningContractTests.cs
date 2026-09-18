using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Phase 5 MSIX signing contracts. Production signing must fail closed without an explicit
/// certificate that matches the manifest publisher and a trusted timestamp; fixture rehearsal must
/// never produce a record that could be mistaken for a production signature.
/// </summary>
public sealed class MsixSigningContractTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(TestRepositoryLayout.Combine(parts));

    [Fact]
    public void SignedMsixGateRequiresValidityPublisherMatchAndTimestamp()
    {
        var common = Read("scripts", "release-common.ps1");
        Assert.Contains("function Assert-MuesliSignedMsix", common, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", common, StringComparison.Ordinal);
        Assert.Contains("does not match manifest publisher", common, StringComparison.Ordinal);
        Assert.Contains("has no trusted timestamp", common, StringComparison.Ordinal);
        Assert.Contains("function Assert-MuesliMsixPublisher", common, StringComparison.Ordinal);
        Assert.Contains("development publisher", common, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSigningForbidsAutomaticSelectionAndRequiresTimestamp()
    {
        var sign = Read("scripts", "sign-windows-release.ps1");
        Assert.Contains("automatic certificate selection is forbidden", sign, StringComparison.Ordinal);
        Assert.Contains("must exactly match the MSIX manifest publisher", sign, StringComparison.Ordinal);
        Assert.Contains("requires a -TimestampUrl", sign, StringComparison.Ordinal);
        Assert.Contains("signtool", sign, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/tr", sign, StringComparison.Ordinal);
        Assert.Contains("/td", sign, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliSignedMsix", sign, StringComparison.Ordinal);
        Assert.Contains("metadata-only fixture record", sign, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedOrchestratorAndQualificationOperateOnTheMsix()
    {
        var orchestrator = Read("scripts", "build-signed-release.ps1");
        Assert.Contains("package-winui-msix.ps1", orchestrator, StringComparison.Ordinal);
        Assert.Contains("sign-windows-release.ps1", orchestrator, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliSignedMsix", orchestrator, StringComparison.Ordinal);
        Assert.Contains("Assert-MuesliProductionPublisher", orchestrator, StringComparison.Ordinal);

        var qualify = Read("scripts", "qualify-windows-release.ps1");
        Assert.Contains("Assert-MuesliSignedMsix", qualify, StringComparison.Ordinal);
        Assert.Contains("test-winui-msix.ps1", qualify, StringComparison.Ordinal);
        Assert.DoesNotContain("Muesli.exe", qualify, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerRefusesUnsignedMsixWithoutExplicitDevelopmentOptIn()
    {
        var install = Read("scripts", "install-windows.ps1");
        Assert.Contains("Add-AppxPackage", install, StringComparison.Ordinal);
        Assert.Contains("AllowUnsignedDevelopment", install, StringComparison.Ordinal);
        Assert.Contains("Refusing to install the uncertified MSIX", install, StringComparison.Ordinal);
        Assert.DoesNotContain("WScript.Shell", install, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunSigningProducesAFixtureRecordThatIsNotAuthenticode()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "muesli-msix-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var msix = Path.Combine(scratch, "fixture.msix");
            using (var archive = ZipFile.Open(msix, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("AppxManifest.xml");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="Muesli.Windows" Publisher="CN=FixturePublisher" Version="0.3.0.0" />
                    </Package>
                    """);
            }

            var output = Path.Combine(scratch, "sig");
            var script = TestRepositoryLayout.Combine("scripts", "sign-windows-release.ps1");
            var start = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -MsixPath \"{msix}\" -DryRun -SignatureOutputDirectory \"{output}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = TestRepositoryLayout.Root
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh failed to start.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"dry-run signing failed ({process.ExitCode}): {stdout}{stderr}");

            var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "Muesli.Windows.WinUI.msix.signature.json"))).RootElement;
            Assert.Equal("dry-run", record.GetProperty("mode").GetString());
            Assert.Equal("Simulated", record.GetProperty("status").GetString());
            Assert.False(record.GetProperty("authenticode").GetBoolean());
            Assert.False(record.GetProperty("timestamped").GetBoolean());
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch (IOException) { }
        }
    }
}
