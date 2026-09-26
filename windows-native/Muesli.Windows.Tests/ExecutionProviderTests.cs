using System.Text.Json;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Provider tests mutate process-global native-runtime state, so they run in their own
/// non-parallel collection. Nothing here claims real GPU qualification on a CPU-only machine:
/// GPU assertions are gated on the acceleration pack actually being installed and the CUDA
/// runtime actually being loaded.
/// </summary>
[CollectionDefinition("ExecutionProvider", DisableParallelization = true)]
public sealed class ExecutionProviderCollection;

[Collection("ExecutionProvider")]
public sealed class ExecutionProviderServiceTests : IDisposable
{
    private readonly ExecutionProviderPreference _originalPreference =
        ExecutionProviderService.Preference;

    public void Dispose()
    {
        ExecutionProviderService.Configure(_originalPreference);
        ExecutionProviderService.ResetTelemetry();
    }

    [Fact]
    public void AutoPreferredProvidersAlwaysEndOnCpuSoFallbackKeepsTheSameModel()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.Auto);
        var providers = ExecutionProviderService.PreferredProviders(
            ExecutionProviderRole.OfflineTranscription);

        Assert.Equal("cpu", providers[^1].Provider);
        Assert.False(providers[^1].IsGpu);
        Assert.NotEmpty(providers);
    }

    [Fact]
    public void AutomaticOnlyAttemptsGpuWhereMeasurementsShowABenefit()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.Auto);

        // Each answer is a measured decision from qualification/gpu-qualification.
        Assert.True(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.Whisper));
        Assert.True(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.ParakeetTransducer));
        Assert.False(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.Qwen3Asr));
        Assert.False(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.SenseVoice));
        Assert.False(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.CohereTranscribe));
        Assert.False(TranscriptionProviderPolicy.AutoShouldAttemptGpu(NativeAsrModelKind.Parakeet));
        Assert.False(TranscriptionProviderPolicy.AutoShouldAttemptGpuForStreaming);

        Assert.Contains("1.8x slower", TranscriptionProviderPolicy.MeasurementNote(NativeAsrModelKind.Qwen3Asr));
    }

    [Fact]
    public void ExplicitCudaStillOverridesTheMeasuredCpuPolicy()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.Cuda);
        var providers = ExecutionProviderService.PreferredProviders(
            ExecutionProviderRole.OfflineTranscription,
            NativeAsrModelKind.Qwen3Asr);

        // A user who explicitly asks for CUDA gets the CUDA attempt even for an architecture that
        // Automatic keeps on CPU; CPU remains the last resort so the model still runs.
        if (NativeSherpaRuntime.IsCudaCapable)
        {
            Assert.Equal("cuda", providers[0].Provider);
        }

        Assert.Equal("cpu", providers[^1].Provider);
        Assert.NotEmpty(TranscriptionProviderPolicy.MeasurementNote(NativeAsrModelKind.Qwen3Asr));
    }

    [Fact]
    public void CpuPreferenceNeverOffersAGpuProvider()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.Cpu);
        foreach (var role in new[]
                 {
                     ExecutionProviderRole.OfflineTranscription,
                     ExecutionProviderRole.LiveTranscription,
                     ExecutionProviderRole.Diarization
                 })
        {
            var providers = ExecutionProviderService.PreferredProviders(role);
            Assert.Single(providers);
            Assert.Equal("cpu", providers[0].Provider);
        }
    }

    [Fact]
    public void DirectMlIsNeverRequestedAndItsBlockerIsExplained()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.DirectMl);
        var providers = ExecutionProviderService.PreferredProviders(
            ExecutionProviderRole.OfflineTranscription);

        Assert.DoesNotContain(providers, provider =>
            provider.Provider.Contains("directml", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("cpu", providers[^1].Provider);
        Assert.Contains("SHERPA_ONNX_ENABLE_DIRECTML", ExecutionProviderService.DirectMlUnsupportedReason);
        Assert.Contains("Fallback to cpu", ExecutionProviderService.DirectMlUnsupportedReason);

        var status = ExecutionProviderService.Inspect();
        Assert.False(status.DirectMlAvailable);
        Assert.Equal(ExecutionProviderPreference.DirectMl, status.Requested);
    }

    [Fact]
    public void ProviderSettingRoundTripsThroughItsStableStringValue()
    {
        foreach (var preference in Enum.GetValues<ExecutionProviderPreference>())
        {
            var value = ExecutionProviderService.ToSettingValue(preference);
            Assert.Equal(preference, ExecutionProviderService.Parse(value));
        }

        Assert.Equal(ExecutionProviderPreference.Auto, ExecutionProviderService.Parse(null));
        Assert.Equal(ExecutionProviderPreference.Auto, ExecutionProviderService.Parse(""));
        Assert.Equal(ExecutionProviderPreference.Auto, ExecutionProviderService.Parse("obsolete-value"));
        Assert.Equal(ExecutionProviderPreference.Cuda, ExecutionProviderService.Parse("GPU"));
        Assert.Equal(ExecutionProviderPreference.DirectMl, ExecutionProviderService.Parse("dml"));
    }

    [Fact]
    public void ExecutionProviderSettingPersistsAndInvalidValuesNormalizeToAutomatic()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");

        var store = new SettingsStore(path, new InMemorySecretStore());
        store.Save(store.Load() with
        {
            ExecutionProvider = "cuda",
            CleanupModelId = CleanupModelCatalog.S1MiniId
        });

        var reloaded = new SettingsStore(path, new InMemorySecretStore()).Load();
        Assert.Equal("cuda", reloaded.ExecutionProvider);
        Assert.Equal(CleanupModelCatalog.S1MiniId, reloaded.CleanupModelId);

        var normalized = SettingsStore.NormalizeAfterDeserialization(
            reloaded with { ExecutionProvider = "quantum" });
        Assert.Equal("auto", normalized.ExecutionProvider);

        foreach (var preference in Enum.GetValues<ExecutionProviderPreference>())
        {
            var persisted = SettingsStore.NormalizeAfterDeserialization(reloaded with
            {
                ExecutionProvider = ExecutionProviderService.ToSettingValue(preference)
            });
            Assert.Equal(ExecutionProviderService.ToSettingValue(preference), persisted.ExecutionProvider);
        }
    }

    [Fact]
    public void UnknownCleanupModelIdNormalizesToNoneInsteadOfAnUnrelatedModel()
    {
        Assert.Equal("", CleanupModelCatalog.Normalize("does-not-exist"));
        Assert.Equal("", CleanupModelCatalog.Normalize(null));
        Assert.Equal(
            CleanupModelCatalog.S1MiniId,
            CleanupModelCatalog.Normalize(CleanupModelCatalog.S1MiniId.ToUpperInvariant()));
    }

    [Fact]
    public void RestartIsRequiredOnlyWhenTheLoadedRuntimeWouldChange()
    {
        // The test process has not loaded the native runtime in this collection, so no restart is
        // required for any candidate. Once loaded, the assertion is that the answer matches the
        // actual loaded runtime instead of a guessed value.
        if (!NativeSherpaRuntime.HasLoadedRuntime)
        {
            Assert.False(ExecutionProviderService.RestartRequired(ExecutionProviderPreference.Cpu));
            Assert.False(ExecutionProviderService.RestartRequired(ExecutionProviderPreference.Cuda));
            return;
        }

        var loaded = NativeSherpaRuntime.SelectedRuntime;
        var wouldLoadCuda = NativeSherpaRuntime.ResolveCudaBundleDirectory() is not null;
        Assert.Equal(
            loaded == "cuda",
            ExecutionProviderService.RestartRequired(ExecutionProviderPreference.Cpu));
        Assert.Equal(
            wouldLoadCuda != (loaded == "cuda"),
            ExecutionProviderService.RestartRequired(ExecutionProviderPreference.Cuda));
    }

    [Fact]
    public void DirectMlRequestNeverFallsThroughToAWorkingGpuClaim()
    {
        ExecutionProviderService.Configure(ExecutionProviderPreference.DirectMl);
        var status = ExecutionProviderService.Inspect();
        Assert.False(status.DirectMlAvailable);
        Assert.Contains("not available", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DirectML verified", status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DirectML was requested and", status.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InspectionReportsAdaptersDriversAndPackStateWithoutClaimingGpuInference()
    {
        var status = ExecutionProviderService.Inspect();

        Assert.NotNull(status.Adapters);
        Assert.NotEmpty(status.Summary);
        Assert.NotEmpty(status.DirectMlReason);
        Assert.NotNull(status.MissingNvidiaFiles);
        Assert.NotNull(status.AdaptersLabel);
        Assert.NotNull(status.RuntimeDiagnostic);
        Assert.Contains(status.LoadedRuntime, new[] { "cuda", "cpu", "unavailable", "not initialized" });

        // A pack on disk is only ever described as a pack; inference evidence is separate.
        if (status.CudaPackInstalled)
        {
            Assert.Equal(CudaAccelerationPack.InstallDirectory, status.CudaPackDirectory);
        }
    }
}

/// <summary>
/// CUDA acceleration-pack provenance. These tests are hermetic: they validate the pinned
/// definition and validator behaviour without touching the machine's installed pack.
/// </summary>
public sealed class CudaAccelerationPackContractTests
{
    [Fact]
    public void PinnedArchiveAndFileHashesAreCompleteAndWellFormed()
    {
        Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.4/",
            CudaAccelerationPack.ArchiveUrl, StringComparison.Ordinal);
        Assert.Equal(64, CudaAccelerationPack.ArchiveSha256.Length);
        Assert.True(CudaAccelerationPack.ArchiveSha256.All(Uri.IsHexDigit));
        Assert.Equal("1.13.4", CudaAccelerationPack.SherpaRuntimeVersion);
        Assert.Equal("1.24.4", CudaAccelerationPack.OnnxRuntimeVersion);
        Assert.Equal("12.x", CudaAccelerationPack.CudaVersion);
        Assert.Equal("9.x", CudaAccelerationPack.CudnnVersion);
        Assert.True(CudaAccelerationPack.ArchiveSizeBytes > 0);

        Assert.Equal(4, CudaAccelerationPack.RequiredFiles.Count);
        foreach (var file in CudaAccelerationPack.RequiredFiles)
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.True(file.Sha256.All(Uri.IsHexDigit), file.Name);
            Assert.True(file.SizeBytes > 0, file.Name);
        }

        Assert.Contains(CudaAccelerationPack.RequiredFiles, file => file.Name == "onnxruntime_providers_cuda.dll");
        Assert.Contains(CudaAccelerationPack.RequiredFiles, file => file.Name == "onnxruntime_providers_shared.dll");
        Assert.Contains(CudaAccelerationPack.RequiredFiles, file => file.Name == "sherpa-onnx-c-api.dll");
        Assert.Contains(CudaAccelerationPack.RequiredFiles, file => file.Name == "onnxruntime.dll");
    }

    [Fact]
    public void PackInstallLocationIsOutsideThePackagedApplication()
    {
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var installRoot = Path.GetFullPath(CudaAccelerationPack.InstallRoot);
        Assert.False(
            installRoot.StartsWith(applicationDirectory, StringComparison.OrdinalIgnoreCase),
            "The CUDA acceleration pack must not be installed inside the packaged application.");
    }

    [Fact]
    public void PackManifestValidatorAcceptsOnlyAPinnedManifestForTheManagedRoot()
    {
        using var directory = new TestDirectory();
        var payload = new
        {
            runtimeVersion = CudaAccelerationPack.SherpaRuntimeVersion,
            onnxRuntimeVersion = CudaAccelerationPack.OnnxRuntimeVersion,
            onnxRuntimeFileVersion = CudaAccelerationPack.OnnxRuntimeFileVersion,
            requiredRuntimeFiles = CudaAccelerationPack.RequiredFiles.Select(file => file.Name).ToArray(),
            requiredNvidiaFiles = NativeSherpaRuntime.NvidiaDependencyFileNames,
            fileSha256 = CudaAccelerationPack.RequiredFiles.ToDictionary(
                file => file.Name,
                file => file.Sha256,
                StringComparer.OrdinalIgnoreCase)
        };

        var manifest = NativeCudaManifestFiles.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(CudaAccelerationPack.SherpaRuntimeVersion, manifest.RuntimeVersion);
        Assert.Equal(
            CudaAccelerationPack.OnnxRuntimeFileVersion,
            manifest.OnnxRuntimeFileVersion);
        Assert.Equal(4, manifest.FileSha256.Count);
        Assert.Equal(
            CudaAccelerationPack.RequiredFiles.Count,
            manifest.RequiredRuntimeFiles.Length);
        Assert.Equal(
            NativeSherpaRuntime.NvidiaDependencyFileNames.Count,
            manifest.RequiredNvidiaFiles.Length);
    }

    [Fact]
    public void AccelerationPackManifestRequiresPinnedArchiveAndFileInventory()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, CudaAccelerationPack.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                runtimeVersion = CudaAccelerationPack.SherpaRuntimeVersion,
                onnxRuntimeVersion = CudaAccelerationPack.OnnxRuntimeVersion,
                onnxRuntimeFileVersion = CudaAccelerationPack.OnnxRuntimeFileVersion,
                cudaVersion = CudaAccelerationPack.CudaVersion,
                cudnnVersion = CudaAccelerationPack.CudnnVersion,
                archiveUrl = CudaAccelerationPack.ArchiveUrl,
                archiveSha256 = "00" + CudaAccelerationPack.ArchiveSha256[2..],
                archiveSizeBytes = CudaAccelerationPack.ArchiveSizeBytes,
                requiredRuntimeFiles = CudaAccelerationPack.RequiredFiles.Select(file => file.Name),
                requiredNvidiaFiles = NativeSherpaRuntime.NvidiaDependencyFileNames,
                fileSha256 = CudaAccelerationPack.RequiredFiles.ToDictionary(file => file.Name, file => file.Sha256)
            }));

        var result = CudaAccelerationPack.ValidateManifest(directory.Path);

        Assert.False(result.IsValid);
        Assert.Contains("pinned runtime provenance", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingOrWrongVersionCudaBundlesAreRejected()
    {
        using var missing = new TestDirectory();
        var missingResult = PublicNativePackageContract.ValidateCudaBundle(missing.Path);
        Assert.False(missingResult.IsAccepted);
        Assert.Contains("missing", missingResult.Diagnostic, StringComparison.OrdinalIgnoreCase);

        using var wrongVersion = new TestDirectory();
        File.WriteAllText(
            Path.Combine(wrongVersion.Path, "native-sherpa-cuda-runtime.json"),
            JsonSerializer.Serialize(new
            {
                runtimeVersion = "1.13.5",
                requiredRuntimeFiles = Array.Empty<string>(),
                requiredNvidiaFiles = Array.Empty<string>()
            }));
        var wrongVersionResult = PublicNativePackageContract.ValidateCudaBundle(wrongVersion.Path);
        Assert.False(wrongVersionResult.IsAccepted);
        Assert.Contains("1.13.5", wrongVersionResult.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignRidFilesCannotBeSelectedForCudaBecauseThePackIsWinX64Only()
    {
        // The managed pack definition is architecture-specific: every pinned file is a win-x64
        // binary, and the install root is keyed by pack version rather than by arbitrary RID.
        Assert.Equal("sherpa-onnx-1.13.4-cuda12-cudnn9-win-x64", CudaAccelerationPack.PackVersion);
        Assert.DoesNotContain("win-x86", CudaAccelerationPack.PackVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("linux", CudaAccelerationPack.PackVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("osx", CudaAccelerationPack.PackVersion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NvidiaRuntimeRequirementListsEveryCuda12AndCudnn9Dependency()
    {
        var required = NativeSherpaRuntime.NvidiaDependencyFileNames;
        Assert.Contains("cublasLt64_12.dll", required);
        Assert.Contains("cublas64_12.dll", required);
        Assert.Contains("cufft64_11.dll", required);
        Assert.Contains("cudart64_12.dll", required);
        Assert.Contains("cudnn64_9.dll", required);
        Assert.Contains("cudnn_graph64_9.dll", required);
        Assert.Contains("cudnn_engines_precompiled64_9.dll", required);
        Assert.Contains("cudnn_ops64_9.dll", required);
        Assert.Contains("cudnn_adv64_9.dll", required);
        Assert.Contains("cudnn_cnn64_9.dll", required);
    }

    [Fact]
    public async Task PackInstallerVerificationFailsCleanlyWhenNothingIsInstalled()
    {
        // Never mutates the real install: if a pack exists on this machine the test only asserts
        // that verification is deterministic and reports a diagnostic.
        var result = await new CudaAccelerationPackInstaller().VerifyAsync();
        Assert.NotNull(result.Diagnostic);
        if (!CudaAccelerationPack.IsInstalled)
        {
            Assert.False(result.IsValid);
        }
    }

    [Fact]
    public void CudaQualificationIsScopedToTheExactModelOrRole()
    {
        using var directory = new TestDirectory();
        ExecutionProviderService.SetQualificationCacheDirectoryForTests(directory.Path);
        try
        {
            var evidence = new ProviderEvidence(
                "cuda",
                "cuda",
                true,
                12,
                2,
                "CUDAExecutionProvider executed the graph.");

            ExecutionProviderService.StoreQualifiedCudaEvidence("whisper-small-en", evidence);

            Assert.True(ExecutionProviderService.TryGetQualifiedCudaEvidence("whisper-small-en", out var cached));
            Assert.True(cached.ConfirmedGpuInference);
            Assert.False(ExecutionProviderService.TryGetQualifiedCudaEvidence("qwen3-asr-0.6b-int8", out var other));
            Assert.Contains("qwen3-asr-0.6b-int8", other.Detail, StringComparison.Ordinal);

            var persisted = File.ReadAllText(Path.Combine(directory.Path, "provider-qualification.json"));
            Assert.Contains("\"schemaVersion\":2", persisted, StringComparison.Ordinal);
            Assert.Contains("whisper-small-en", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("qwen3-asr-0.6b-int8", persisted, StringComparison.Ordinal);
        }
        finally
        {
            ExecutionProviderService.ResetQualification();
            ExecutionProviderService.SetQualificationCacheDirectoryForTests(null);
        }
    }
}

/// <summary>
/// The warm-up profile verdict is what allows the product to say "CUDA is active". These tests
/// exercise the parser with real ONNX Runtime profile shapes rather than inventing an answer.
/// </summary>
public sealed class ProviderWarmUpEvidenceTests
{
    [Fact]
    public void CudaProfileNodesProduceAConfirmedCudaVerdict()
    {
        using var directory = new TestDirectory();
        var prefix = Path.Combine(directory.Path, "ort-profile-test");
        File.WriteAllText(
            $"{prefix}_2026-09-20_10-00-00.json",
            "[{\"cat\" : \"Node\",\"args\" : {\"provider\" : \"CUDAExecutionProvider\",\"op_name\" : \"MatMul\"}}," +
            "{\"cat\" : \"Node\",\"args\" : {\"provider\" : \"CPUExecutionProvider\",\"op_name\" : \"Cast\"}}]");

        var evidence = ExecutionProviderService.ReadEvidence("cuda", [prefix]);
        Assert.True(evidence.ConfirmedGpuInference);
        Assert.Equal("cuda", evidence.ConfirmedProvider);
        Assert.Equal(1, evidence.GpuNodeCount);
        Assert.Equal(1, evidence.CpuNodeCount);
    }

    [Fact]
    public void CpuOnlyProfileProducesAnHonestCpuFallbackVerdict()
    {
        using var directory = new TestDirectory();
        var prefix = Path.Combine(directory.Path, "ort-profile-test");
        File.WriteAllText(
            $"{prefix}_2026-09-20_10-00-01.json",
            "[{\"cat\" : \"Node\",\"args\" : {\"provider\" : \"CPUExecutionProvider\"}}," +
            "{\"cat\" : \"Node\",\"args\" : {\"provider\" : \"CPUExecutionProvider\"}}]");

        var evidence = ExecutionProviderService.ReadEvidence("cuda", [prefix]);
        Assert.False(evidence.ConfirmedGpuInference);
        Assert.Equal("cpu", evidence.ConfirmedProvider);
        Assert.Equal(2, evidence.CpuNodeCount);
        Assert.Contains("CPUExecutionProvider", evidence.Detail);
    }

    [Fact]
    public void MissingProfileIsTreatedAsCpuRatherThanClaimingGpu()
    {
        using var directory = new TestDirectory();
        var prefix = Path.Combine(directory.Path, "ort-profile-absent");
        var evidence = ExecutionProviderService.ReadEvidence("cuda", [prefix]);

        Assert.False(evidence.ConfirmedGpuInference);
        Assert.Equal("cpu", evidence.ConfirmedProvider);
        Assert.Contains("no warm-up profile", evidence.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectMlProfileNodesWouldBeReportedAsDirectMlNotCuda()
    {
        using var directory = new TestDirectory();
        var prefix = Path.Combine(directory.Path, "ort-profile-test");
        File.WriteAllText(
            $"{prefix}_2026-09-20_10-00-02.json",
            "[{\"cat\" : \"Node\",\"args\" : {\"provider\" : \"DmlExecutionProvider\"}}]");

        var evidence = ExecutionProviderService.ReadEvidence("directml", [prefix]);
        Assert.True(evidence.ConfirmedGpuInference);
        Assert.Equal("directml", evidence.ConfirmedProvider);
    }
}

/// <summary>
/// Cleanup-model lifecycle integrity. The catalog must never invent a model, and the prompt
/// contract must switch only for the model that requires it.
/// </summary>
public sealed class CleanupModelCatalogTests
{
    [Fact]
    public void CatalogMatchesTheMacOsCleanupCatalogWithRealPinnedArtifacts()
    {
        Assert.Equal(3, CleanupModelCatalog.Models.Count);
        Assert.Contains(CleanupModelCatalog.Models, model => model.Id == CleanupModelCatalog.MuesliCleanupId);
        Assert.Contains(CleanupModelCatalog.Models, model => model.Id == CleanupModelCatalog.S1MiniId);
        Assert.Contains(CleanupModelCatalog.Models, model => model.Id == CleanupModelCatalog.QwenBasicId);

        foreach (var model in CleanupModelCatalog.Models)
        {
            Assert.StartsWith("https://huggingface.co/", model.DownloadUrl, StringComparison.Ordinal);
            Assert.EndsWith(".gguf", model.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(64, model.Sha256.Length);
            Assert.True(model.Sha256.All(Uri.IsHexDigit), model.Id);
            Assert.True(model.SizeBytes > 0, model.Id);
            Assert.False(string.IsNullOrWhiteSpace(model.License), model.Id);
            Assert.Contains(model.FileName, model.DownloadUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(model.SourceRevision), model.Id);
        }
    }

    [Fact]
    public void OnlyS1MiniUsesItsFixedControlLinePrompt()
    {
        var s1MiniPath = CleanupModelCatalog.PathFor(
            CleanupModelCatalog.Get(CleanupModelCatalog.S1MiniId)!);
        var muesliPath = CleanupModelCatalog.PathFor(
            CleanupModelCatalog.Get(CleanupModelCatalog.MuesliCleanupId)!);

        Assert.Equal(CleanupPromptContract.S1Mini, NativeTextCleanupService.PromptContractFor(s1MiniPath));
        Assert.Equal(CleanupPromptContract.Configurable, NativeTextCleanupService.PromptContractFor(muesliPath));
        Assert.Equal(
            CleanupPromptContract.Configurable,
            NativeTextCleanupService.PromptContractFor(Path.Combine(Path.GetTempPath(), "unmanaged.gguf")));

        var s1Prompt = NativeTextCleanupService.BuildPrompt(
            "hello world", preserveStructure: true, CleanupPromptContract.S1Mini);
        Assert.Contains("[Styling: semi-formal]", s1Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Instructions:", s1Prompt, StringComparison.Ordinal);

        var configurablePrompt = NativeTextCleanupService.BuildPrompt(
            "hello world", preserveStructure: true, CleanupPromptContract.Configurable);
        Assert.Contains("RAW TRANSCRIPT:", configurablePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionWithoutInstallationNeverResolvesAModelPath()
    {
        using var directory = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", directory.Path);
            Assert.Null(CleanupModelCatalog.ResolveInstalledModelPath(CleanupModelCatalog.DefaultModelId));
            Assert.Null(CleanupModelCatalog.ResolveInstalledModelPath(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", previous);
        }
    }

    [Fact]
    public void LegacyPlacedGgufKeepsWorkingWithoutACatalogSelection()
    {
        using var directory = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", directory.Path);
            var legacy = Path.Combine(directory.Path, "legacy-cleanup.gguf");
            File.WriteAllText(legacy, "not a real model");

            Assert.Equal(legacy, CleanupModelCatalog.ResolveInstalledModelPath(null));
            Assert.Null(CleanupModelCatalog.ResolveInstalledModelPath(CleanupModelCatalog.S1MiniId));
            Assert.Null(CleanupModelCatalog.DefinitionForPath(legacy));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", previous);
        }
    }

    [Fact]
    public void LifecycleSnapshotReportsStatusWithoutDownloadingAnything()
    {
        using var directory = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", directory.Path);
            var service = new CleanupModelLifecycleService(CleanupModelCatalog.S1MiniId);
            var snapshots = service.Snapshots();

            Assert.Equal(3, snapshots.Count);
            Assert.All(snapshots, snapshot => Assert.False(snapshot.IsInstalled));
            var selected = snapshots.Single(snapshot => snapshot.Model.Id == CleanupModelCatalog.S1MiniId);
            Assert.True(selected.IsSelected);
            Assert.Equal("Not downloaded", selected.StatusText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", previous);
        }
    }

    [Fact]
    public async Task InstalledButUnverifiedModelIsReportedAsNeedingVerification()
    {
        using var directory = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE");
        try
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", directory.Path);
            var model = CleanupModelCatalog.Get(CleanupModelCatalog.MuesliCleanupId)!;
            var path = CleanupModelCatalog.PathFor(model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "unverified");

            var service = new CleanupModelLifecycleService(model.Id);
            var snapshot = service.Snapshots().Single(item => item.Model.Id == model.Id);
            Assert.True(snapshot.IsInstalled);
            Assert.False(snapshot.IsVerified);
            Assert.Equal("Installed · needs verification", snapshot.StatusText);

            var failure = await Record.ExceptionAsync(
                () => service.VerifyAsync(model.Id, CancellationToken.None));
            Assert.NotNull(failure);
            Assert.Contains("SHA-256", failure!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Null(await Record.ExceptionAsync(
                () => service.DeleteAsync(model.Id, CancellationToken.None)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE", previous);
        }
    }
}
