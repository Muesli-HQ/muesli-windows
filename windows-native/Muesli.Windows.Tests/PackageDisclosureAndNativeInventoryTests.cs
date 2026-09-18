using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Tests;

public sealed class PackageDisclosureAndNativeInventoryTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;

    private static NativeCatalog LoadCatalog()
    {
        var path = Path.Combine(RepositoryRoot, PublicNativePackageContract.CatalogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return JsonSerializer.Deserialize<NativeCatalog>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Native catalog deserialized to null.");
    }

    [Fact]
    public void NoticesAndReleaseCopyAgreeThatPublicPackageIsCpuOnly()
    {
        var notices = Read("THIRD-PARTY-NOTICES.md");
        var readme = Read("README.md");
        var packageScript = Read("scripts/package-windows-v1.ps1");
        // The CPU-only disclosure flag lives in the shared release configuration.
        var releaseCommon = Read("scripts/release-common.ps1");
        var releaseNotes = Read("docs/WINDOWS_V1_RELEASE.md");
        var catalog = LoadCatalog();

        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, notices, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, releaseNotes, StringComparison.Ordinal);
        Assert.Contains("CPU Sherpa provider only", notices, StringComparison.Ordinal);
        Assert.Contains("not shipped", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packaged CPU provider", readme, StringComparison.Ordinal);
        Assert.Contains("cudaProviderIncluded = $false", releaseCommon, StringComparison.Ordinal);
        Assert.Contains("does not claim NVIDIA acceleration", readme, StringComparison.Ordinal);
        Assert.Contains("CPU Sherpa provider only", releaseNotes, StringComparison.Ordinal);
        Assert.False(catalog.PublicPackage.CudaProviderIncluded);
        Assert.True(catalog.PublicPackage.CpuProviderIncluded);
        Assert.Equal(PublicNativePackageContract.PublicPackageDisclosure, catalog.PublicPackage.Disclosure);
        Assert.Equal("L11", catalog.PublicPackage.CudaQualificationModule);
    }

    [Fact]
    public void CatalogLicensesAndNoticesCoverEveryRequiredNativeComponent()
    {
        var catalog = LoadCatalog();
        var notices = Read("THIRD-PARTY-NOTICES.md");
        foreach (var component in catalog.Components.Where(item => item.RequiredInPublicWinX64))
        {
            Assert.False(string.IsNullOrWhiteSpace(component.NoticesHeading), component.Id);
            Assert.Contains(component.NoticesHeading, notices, StringComparison.Ordinal);
            AssertLicense(component.LicenseFile);
            foreach (var extra in component.AdditionalLicenseFiles ?? [])
            {
                AssertLicense(extra);
            }
        }
    }

    [Fact]
    public void AppOutputRejectsCudaProviderAndInventoriesNativeDlls()
    {
        var catalog = LoadCatalog();
        var appDirectory = Path.GetDirectoryName(typeof(PublicNativePackageContract).Assembly.Location)
            ?? throw new DirectoryNotFoundException("Application output directory was not found.");
        var inventory = NativeRuntimeInventory.Scan(appDirectory, catalog, allowDebugRidExtras: true);

        Assert.Empty(inventory.Failures);
        Assert.DoesNotContain(
            inventory.Files,
            file => catalog.ForbiddenPublicCudaFiles.Contains(file.FileName, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(inventory.Files, file => file.FileName.Equals("sherpa-onnx-c-api.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Files, file => file.FileName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Files, file => file.FileName.Equals("llama.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Files, file => file.FileName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Files, file => file.FileName.Equals("QuestPdfSkia.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnmanifestedCudaBundleIsRejected()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "onnxruntime_providers_cuda.dll"), [1]);
        var result = PublicNativePackageContract.ValidateCudaBundle(directory.Path);
        Assert.False(result.IsAccepted);
        Assert.Contains("missing native-sherpa-cuda-runtime.json", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteCudaBundleMissingProviderDllIsRejected()
    {
        using var directory = new TestDirectory();
        var manifest = PublicNativePackageContract.ReadRepositoryCudaManifest(RepositoryRoot);
        File.WriteAllText(
            Path.Combine(directory.Path, "native-sherpa-cuda-runtime.json"),
            File.ReadAllText(Path.Combine(RepositoryRoot, PublicNativePackageContract.CudaManifestRelativePath.Replace('/', Path.DirectorySeparatorChar))));
        foreach (var file in manifest.RequiredRuntimeFiles.Where(name =>
                     !name.Equals("onnxruntime_providers_cuda.dll", StringComparison.OrdinalIgnoreCase)))
        {
            File.WriteAllBytes(Path.Combine(directory.Path, file), [1]);
        }

        var result = PublicNativePackageContract.ValidateCudaBundle(directory.Path);
        Assert.False(result.IsAccepted);
        Assert.Contains("onnxruntime_providers_cuda.dll", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incomplete", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CudaBundleWithRuntimeDllsButMissingNvidiaDependenciesIsRejected()
    {
        using var directory = new TestDirectory();
        var manifest = PublicNativePackageContract.ReadRepositoryCudaManifest(RepositoryRoot);
        File.WriteAllText(
            Path.Combine(directory.Path, "native-sherpa-cuda-runtime.json"),
            File.ReadAllText(Path.Combine(RepositoryRoot, PublicNativePackageContract.CudaManifestRelativePath.Replace('/', Path.DirectorySeparatorChar))));
        foreach (var file in manifest.RequiredRuntimeFiles)
        {
            File.WriteAllBytes(Path.Combine(directory.Path, file), [1]);
        }

        var result = PublicNativePackageContract.ValidateCudaBundle(directory.Path);
        Assert.False(result.IsAccepted);
        Assert.Contains("NVIDIA dependencies are incomplete", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("cudnn64_9.dll", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrongVersionCudaManifestIsRejected()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "native-sherpa-cuda-runtime.json"),
            """{"runtimeVersion":"0.0.0","requiredRuntimeFiles":[],"requiredNvidiaFiles":[]}""");
        var result = PublicNativePackageContract.ValidateCudaBundle(directory.Path);
        Assert.False(result.IsAccepted);
        Assert.Contains("runtime version '0.0.0'", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryFailsWhenAPackagedNativeFileIsMissingFromCatalog()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, "mystery-native.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        var inventory = NativeRuntimeInventory.Scan(directory.Path, LoadCatalog(), allowDebugRidExtras: false);
        Assert.Contains(
            inventory.Failures,
            failure => failure.Contains("mystery-native.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InventoryFailsWhenDisclosureClaimsCudaButRuntimeIsCpuOnly()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "THIRD-PARTY-NOTICES.md"),
            "The primary Muesli package includes the version-matched sherpa-onnx CUDA provider.");
        File.WriteAllBytes(Path.Combine(directory.Path, "sherpa-onnx-c-api.dll"), [1]);
        var inventory = NativeRuntimeInventory.Scan(directory.Path, LoadCatalog(), allowDebugRidExtras: true);
        Assert.Contains(
            inventory.Failures,
            failure => failure.Contains("claims the public package includes a CUDA provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QuestPDFCommunityLicenseIsSelectedWithoutAnEligibilityConclusion()
    {
        var exporter = Read("windows-native/Muesli.Windows.Platform/Services/MeetingDocumentWriter.cs");
        var notices = Read("THIRD-PARTY-NOTICES.md");
        var ledger = Read("docs/WINDOWS_LAUNCH_LEDGER.md");
        Assert.Contains("QuestPDF.Settings.License = LicenseType.Community", exporter, StringComparison.Ordinal);
        Assert.Contains("QuestPDF 2026.5.0", notices, StringComparison.Ordinal);
        Assert.Contains("No eligibility conclusion is", notices, StringComparison.Ordinal);
        Assert.Contains("does not declare that the distributing entity is eligible", notices, StringComparison.Ordinal);
        Assert.Contains("community-license eligibility remains a Phase 13 release-owner check", ledger, StringComparison.Ordinal);
        var expRow = ledger.Split('\n').First(line => line.Contains("| EXP-01 |", StringComparison.Ordinal));
        Assert.Contains("Implemented with verification debt", expRow, StringComparison.Ordinal);
        Assert.DoesNotContain("Complete and verified", expRow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeDiagnosticsInspectDisclosesCpuOnlyPublicPackage()
    {
        var diagnostics = await new RuntimeDiagnosticsService().InspectAsync(
            false,
            TranscriptionModelCatalog.DefaultModelId,
            TranscriptionModelCatalog.DefaultModelId,
            null);
        Assert.Contains("Package truth:", diagnostics.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, diagnostics.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, diagnostics.Summary, StringComparison.Ordinal);
        Assert.True(
            diagnostics.Detail.Contains(PublicNativePackageContract.PublicPackageDisclosure, StringComparison.Ordinal) ||
            diagnostics.Detail.Contains("The public Wave 0 package still does not ship NVIDIA libraries.", StringComparison.Ordinal),
            diagnostics.Detail);
        if (!NativeSherpaRuntime.IsCudaCapable)
        {
            Assert.Equal("CPU", diagnostics.Acceleration);
        }

        var source = Read("windows-native/Muesli.Windows.Core/Services/RuntimeDiagnosticsService.cs");
        Assert.Contains("PublicNativePackageContract.PublicPackageDisclosure", source, StringComparison.Ordinal);
        Assert.Contains("Package truth:", source, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, source, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallCudaScriptIsDocumentedAsOptionalAndNotShipped()
    {
        var script = Read("scripts/install-parakeet-cuda-runtime.ps1");
        Assert.Contains("NOT part of the public Wave 0 package", script, StringComparison.Ordinal);
        Assert.Contains("L11", script, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void AssertLicense(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing license file: {relativePath}");
        Assert.True(new FileInfo(path).Length > 0, $"Empty license file: {relativePath}");
    }
}

internal static class NativeRuntimeInventory
{
    public static NativeInventoryReport Scan(string packageDirectory, NativeCatalog catalog, bool allowDebugRidExtras)
    {
        var failures = new List<string>();
        var files = new List<NativeInventoryFile>();
        var forbiddenNames = new HashSet<string>(catalog.ForbiddenPublicCudaFiles, StringComparer.OrdinalIgnoreCase);
        var forbiddenDirs = new HashSet<string>(catalog.ForbiddenPublicCudaDirectoryNames, StringComparer.OrdinalIgnoreCase);
        var fileMap = catalog.Components
            .SelectMany(component => (component.NativeFiles ?? []).Select(name => (name, component)))
            .ToDictionary(item => item.name, item => item.component, StringComparer.OrdinalIgnoreCase);
        var seenRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(packageDirectory, "*.dll", SearchOption.AllDirectories))
        {
            if (IsManaged(path))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            var relative = Path.GetRelativePath(packageDirectory, path).Replace('\\', '/');
            var inForbiddenDir = relative.Split('/').Any(forbiddenDirs.Contains);
            if (forbiddenNames.Contains(name) || inForbiddenDir)
            {
                failures.Add($"Public CPU package contains a forbidden CUDA artifact: {relative}");
                files.Add(new NativeInventoryFile(name, relative, "forbidden-cuda", null));
                continue;
            }

            NativeCatalogComponent? component = null;
            if (fileMap.TryGetValue(name, out var mapped))
            {
                component = mapped;
            }
            else
            {
                component = catalog.Components.FirstOrDefault(candidate =>
                    (candidate.NativeFilePatterns ?? []).Any(pattern => Regex.IsMatch(name, pattern)));
            }

            if (component is null)
            {
                failures.Add($"Packaged native file is missing from the inventory catalog/notices: {relative}");
                files.Add(new NativeInventoryFile(name, relative, "unmanifested", null));
                continue;
            }

            if (component.RequiredInPublicWinX64)
            {
                seenRequired.Add(name);
            }

            if (!component.AllowedInPublicWinX64 && !allowDebugRidExtras)
            {
                failures.Add($"Public win-x64 package includes a native file that is not part of the CPU catalog: {relative}");
            }

            files.Add(new NativeInventoryFile(name, relative, "native", component.Id));
        }

        foreach (var component in catalog.Components.Where(item => item.RequiredInPublicWinX64))
        {
            foreach (var name in component.NativeFiles ?? [])
            {
                if (!seenRequired.Contains(name) && files.All(file => !file.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"Required public CPU native file is missing: {name} ({component.Id})");
                }
            }
        }

        var noticesPath = Path.Combine(packageDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(noticesPath))
        {
            var notices = File.ReadAllText(noticesPath);
            if (notices.Contains(PublicNativePackageContract.FalseCudaIncludedClaim, StringComparison.Ordinal))
            {
                failures.Add("Packaged THIRD-PARTY-NOTICES.md still claims the public package includes a CUDA provider.");
            }
        }

        return new NativeInventoryReport(files, failures);
    }

    private static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return true;
        }
    }
}

internal sealed record NativeInventoryReport(List<NativeInventoryFile> Files, List<string> Failures);

internal sealed record NativeInventoryFile(string FileName, string RelativePath, string Kind, string? ComponentId);

internal sealed class NativeCatalog
{
    public NativeCatalogPublicPackage PublicPackage { get; set; } = new();
    public string[] ForbiddenPublicCudaFiles { get; set; } = [];
    public string[] ForbiddenPublicCudaDirectoryNames { get; set; } = [];
    public List<NativeCatalogComponent> Components { get; set; } = [];
}

internal sealed class NativeCatalogPublicPackage
{
    public bool CpuProviderIncluded { get; set; }
    public bool CudaProviderIncluded { get; set; }
    public string CudaQualificationModule { get; set; } = "";
    public string Disclosure { get; set; } = "";
}

internal sealed class NativeCatalogComponent
{
    public string Id { get; set; } = "";
    public string NoticesHeading { get; set; } = "";
    public string LicenseFile { get; set; } = "";
    public string[]? AdditionalLicenseFiles { get; set; }
    public bool AllowedInPublicWinX64 { get; set; } = true;
    public bool RequiredInPublicWinX64 { get; set; }
    public string[]? NativeFiles { get; set; }
    public string[]? NativeFilePatterns { get; set; }
}
