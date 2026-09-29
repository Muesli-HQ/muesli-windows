using System.Xml.Linq;

namespace Muesli.Windows.Tests;

public sealed class CpuCatalogInventoryTests
{
    private static string RepositoryRoot => TestRepositoryLayout.Root;

    private static string CatalogPath =>
        Path.Combine(RepositoryRoot, "qualification", "cpu-catalog", "advertised-cpu-models.json");

    private static string TemplatePath =>
        Path.Combine(RepositoryRoot, "qualification", "cpu-catalog", "windows-cpu-catalog-qualification.template.json");

    private static JsonDocument LoadCatalog() => JsonDocument.Parse(File.ReadAllText(CatalogPath));

    [Fact]
    public void AdvertisedCpuCatalogMatchesRuntimeCatalogExactly()
    {
        using var document = LoadCatalog();
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("inventory-not-qualified", root.GetProperty("status").GetString());
        Assert.Equal("L10", root.GetProperty("qualificationModule").GetString());
        Assert.Equal("MOD-02", root.GetProperty("capabilityId").GetString());
        Assert.True(root.GetProperty("publicPackageCpuOnly").GetBoolean());
        Assert.False(root.GetProperty("cudaProviderIncluded").GetBoolean());

        var contract = root.GetProperty("qualificationContract");
        Assert.Equal(TranscriptionQualificationContract.InventoryStatus, contract.GetProperty("status").GetString());
        Assert.Equal(TranscriptionQualificationContract.RequiredProvider, contract.GetProperty("requiredProvider").GetString());
        Assert.Equal(TranscriptionQualificationContract.MaxWordErrorRate, contract.GetProperty("maxWordErrorRate").GetDouble());
        Assert.Equal(TranscriptionQualificationContract.MaxCharacterErrorRate, contract.GetProperty("maxCharacterErrorRate").GetDouble());
        Assert.Equal(TranscriptionQualificationContract.MaxRealtimeFactor, contract.GetProperty("maxRealtimeFactor").GetDouble());
        Assert.Equal(3, contract.GetProperty("requiredRuns").GetInt32());
        Assert.True(contract.GetProperty("requiresHumanReference").GetBoolean());
        Assert.True(contract.GetProperty("requiresDeterministicOutput").GetBoolean());
        Assert.True(contract.GetProperty("requiresModelReuse").GetBoolean());
        Assert.Equal(
            TranscriptionQualificationContract.RequiredCorpusCategories,
            contract.GetProperty("requiredCorpusCategories").EnumerateArray().Select(item => item.GetString() ?? "").ToArray());

        var advertised = root.GetProperty("models").EnumerateArray().ToArray();
        var runtime = TranscriptionModelCatalog.Models;
        Assert.Equal(12, runtime.Count);
        Assert.Equal(runtime.Count, advertised.Length);

        for (var index = 0; index < runtime.Count; index++)
        {
            var model = runtime[index];
            var row = advertised[index];
            Assert.Equal(model.Id, row.GetProperty("id").GetString());
            Assert.Equal(model.DisplayName, row.GetProperty("displayName").GetString());
            Assert.Equal(model.Kind.ToString(), row.GetProperty("kind").GetString());
            Assert.Equal(model.Languages, row.GetProperty("languages").GetString());
            Assert.Equal(model.Language, row.GetProperty("languageTag").GetString());
            Assert.Equal(model.DirectoryName, row.GetProperty("directoryName").GetString());
            Assert.Equal(model.ArchiveUrl, row.GetProperty("archiveUrl").GetString());
            Assert.Equal(model.ArchiveSha256, row.GetProperty("archiveSha256").GetString());
            Assert.Equal(model.SizeLabel, row.GetProperty("sizeLabel").GetString());
            Assert.Equal(model.QualificationStatus, row.GetProperty("qualificationStatus").GetString());
            Assert.Equal(model.QualificationPrerequisites, row.GetProperty("qualificationPrerequisites").GetString());
            Assert.False(model.IsQualificationComplete);

            var roles = row.GetProperty("allowedRoles").EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            Assert.Contains("dictation", roles);
            Assert.Contains("final-meeting-import", roles);

            var required = row.GetProperty("requiredFileSha256")
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(model.RequiredFileSha256.Count, required.Count);
            foreach (var file in model.RequiredFileSha256)
            {
                Assert.True(required.TryGetValue(file.Key, out var hash), file.Key);
                Assert.Equal(file.Value, hash);
            }
        }

        Assert.Equal(
            new[] { "dictation", "final-meeting-import" },
            advertised[0].GetProperty("defaultForRoles").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(TranscriptionModelCatalog.DefaultModelId, advertised[0].GetProperty("id").GetString());
    }

    [Fact]
    public void SmokeScriptSelectsEveryAdvertisedCpuModelFromTheCatalogFile()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "smoke-transcription-models.ps1"));
        Assert.Contains("qualification\\cpu-catalog\\advertised-cpu-models.json", script, StringComparison.Ordinal);
        Assert.Contains("$catalog.models", script, StringComparison.Ordinal);
        Assert.Contains("publicPackageCpuOnly", script, StringComparison.Ordinal);
        Assert.DoesNotContain("continue  # skip", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whisper-tiny-en", script, StringComparison.Ordinal);
        Assert.DoesNotContain("cohere-transcribe-int8-en", script, StringComparison.Ordinal);
        Assert.Contains("$env:MUESLI_ASR_PROVIDER = \"cpu\"", script, StringComparison.Ordinal);
        Assert.Contains("$env:MUESLI_PARAKEET_PROVIDER = \"cpu\"", script, StringComparison.Ordinal);
        Assert.Contains("WarmBackend -ne \"cpu\"", script, StringComparison.Ordinal);
        Assert.Contains("Provider identity verified for every successful model: cpu.", script, StringComparison.Ordinal);
        Assert.Contains("DeterministicOutput", script, StringComparison.Ordinal);
        Assert.Contains("ModelInstanceReused", script, StringComparison.Ordinal);
        Assert.Contains("RunCount", script, StringComparison.Ordinal);
        Assert.Contains("MaxRealtimeFactor", script, StringComparison.Ordinal);

        using var document = LoadCatalog();
        foreach (var model in TranscriptionModelCatalog.Models)
        {
            Assert.Contains($"\"id\": \"{model.Id}\"", File.ReadAllText(CatalogPath), StringComparison.Ordinal);
        }

        Assert.Contains("All $($modelIds.Count) supported offline models completed real-audio inference.", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryDocListsEveryAdvertisedModelAndDoesNotClaimL10Complete()
    {
        var inventory = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "L10_CPU_CATALOG_INVENTORY.md"));
        Assert.Contains("inventory-not-qualified", inventory, StringComparison.Ordinal);
        Assert.DoesNotContain("MOD-02 Complete", inventory, StringComparison.Ordinal);
        Assert.DoesNotContain("Complete and verified", inventory, StringComparison.Ordinal);
        foreach (var model in TranscriptionModelCatalog.Models)
        {
            Assert.Contains(model.Id, inventory, StringComparison.Ordinal);
            Assert.Contains(model.ArchiveSha256, inventory, StringComparison.OrdinalIgnoreCase);
        }

        var ledger = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "WINDOWS_LAUNCH_LEDGER.md"));
        var mod02 = ledger.Split('\n').First(line => line.Contains("| MOD-02 |", StringComparison.Ordinal));
        Assert.Contains("Implemented with verification debt", mod02, StringComparison.Ordinal);
        Assert.DoesNotContain("Complete and verified", mod02, StringComparison.Ordinal);
        var qual01 = ledger.Split('\n').First(line => line.Contains("| QUAL-01 |", StringComparison.Ordinal));
        Assert.Contains("Partial", qual01, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationTemplateIsPlaceholderAndHasNoCheckedInAudio()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TemplatePath));
        var root = document.RootElement;
        Assert.Equal("placeholder-not-qualified", root.GetProperty("status").GetString());
        Assert.Equal("cpu", root.GetProperty("requiredProvider").GetString());

        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var modelIds = cases.Select(item => item.GetProperty("modelId").GetString()).ToArray();
        Assert.Equal(TranscriptionModelCatalog.Models.Select(model => model.Id).ToArray(), modelIds);
        Assert.All(cases, item =>
        {
            Assert.True(item.GetProperty("placeholder").GetBoolean());
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("reviewedBy").GetString()));
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("reviewedAt").GetString()));
            Assert.True(string.IsNullOrWhiteSpace(item.GetProperty("referenceProvenance").GetString()));
            Assert.Equal("cpu", item.GetProperty("requiredProvider").GetString());
        });

        var audioDirectory = Path.Combine(RepositoryRoot, "qualification", "cpu-catalog", "audio");
        Assert.False(Directory.EnumerateFiles(audioDirectory, "*.wav", SearchOption.AllDirectories).Any());
        var referenceDirectory = Path.Combine(RepositoryRoot, "qualification", "cpu-catalog", "references");
        Assert.False(Directory.EnumerateFiles(referenceDirectory, "*.txt", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void PublicPackageAndCsprojStayCpuOnlyAtManagedSherpa1134()
    {
        // The shipping CPU-only sherpa dependency lives in the active Core project.
        var project = XDocument.Load(Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows.Core", "Muesli.Windows.Core.csproj"));
        var packageIds = project.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();
        Assert.Contains("org.k2fsa.sherpa.onnx", packageIds, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(packageIds, id => id.Contains("cuda", StringComparison.OrdinalIgnoreCase));
        var sherpa = project.Descendants("PackageReference")
            .Single(element => string.Equals(element.Attribute("Include")?.Value, "org.k2fsa.sherpa.onnx", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PublicNativePackageContract.ExpectedSherpaRuntimeVersion, sherpa.Attribute("Version")?.Value);

        var catalog = JsonSerializer.Deserialize<NativeCatalog>(
            File.ReadAllText(Path.Combine(RepositoryRoot, PublicNativePackageContract.CatalogRelativePath.Replace('/', Path.DirectorySeparatorChar))),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Native catalog deserialized to null.");
        Assert.False(catalog.PublicPackage.CudaProviderIncluded);
        Assert.True(catalog.PublicPackage.CpuProviderIncluded);

        var cudaManifest = PublicNativePackageContract.ReadRepositoryCudaManifest(RepositoryRoot);
        Assert.Equal(PublicNativePackageContract.ExpectedSherpaRuntimeVersion, cudaManifest.RuntimeVersion);
        foreach (var file in cudaManifest.RequiredRuntimeFiles.Concat(cudaManifest.RequiredNvidiaFiles))
        {
            if (file.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase) ||
                file.Equals("sherpa-onnx-c-api.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Contains(file, catalog.ForbiddenPublicCudaFiles, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Contains("native-sherpa-cuda", catalog.ForbiddenPublicCudaDirectoryNames, StringComparer.OrdinalIgnoreCase);

        var installScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "install-parakeet-cuda-runtime.ps1"));
        Assert.Contains("NOT part of the public Wave 0 package", installScript, StringComparison.Ordinal);
        Assert.DoesNotContain("org.k2fsa.sherpa.onnx.runtime.win-x64-cuda", installScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sherpa-onnx-c-api.dll", installScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CudaProvenanceDocDoesNotClaimAPackagedProvider()
    {
        var gap = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "L11_CUDA_PROVENANCE_GAP.md"));
        Assert.Contains("not shipped in the public package", gap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PublicNativePackageContract.ExpectedSherpaRuntimeVersion, gap, StringComparison.Ordinal);
        Assert.Contains("org.k2fsa.sherpa.onnx", gap, StringComparison.Ordinal);
        Assert.Contains(CudaAccelerationPack.ArchiveUrl, gap, StringComparison.Ordinal);
        Assert.Contains(CudaAccelerationPack.ArchiveSha256, gap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CudaAccelerationPack.OnnxRuntimeFileVersion, gap, StringComparison.Ordinal);
        Assert.Contains("SHERPA_ONNX_ENABLE_DIRECTML", gap, StringComparison.Ordinal);
        foreach (var file in CudaAccelerationPack.RequiredFiles)
        {
            Assert.Contains(file.Sha256, gap, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(PublicNativePackageContract.FalseCudaIncludedClaim, gap, StringComparison.Ordinal);
        Assert.DoesNotContain("CUDA is included in the public package", gap, StringComparison.OrdinalIgnoreCase);

        var ledger = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "WINDOWS_LAUNCH_LEDGER.md"));
        var mod01 = ledger.Split('\n').First(line => line.Contains("| MOD-01 |", StringComparison.Ordinal));
        // MOD-01 may advance past "Partial" as measured evidence lands, but it must never claim the
        // CUDA provider is part of the public package while the public notices/inventory stay CPU-only.
        Assert.DoesNotContain("Complete and verified", mod01, StringComparison.Ordinal);
        Assert.Contains("public notices/inventory stay CPU-only", mod01, StringComparison.Ordinal);
    }
}
