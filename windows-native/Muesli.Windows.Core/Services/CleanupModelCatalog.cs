using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>How a cleanup model wants its instruction text presented.</summary>
public enum CleanupPromptContract
{
    /// <summary>Muesli's configurable cleanup instructions.</summary>
    Configurable,

    /// <summary>S1-mini's fixed control line; the model was trained on this exact shape.</summary>
    S1Mini,

    /// <summary>macOS MuesliCleanupV2Prompt: the v12 fine-tunes' fixed ChatML contract (Qwen 3.5 builds).</summary>
    TrainedV12Qwen,

    /// <summary>The same contract for the LFM 2.5 build, which starts from its BOS token and has no think block.</summary>
    TrainedV12Lfm
}

/// <summary>
/// One pinned local cleanup model. Every value comes from the published artifact: the URL resolves
/// to the exact file, and the SHA-256 is the file's LFS object id as published by the host.
/// </summary>
public sealed record CleanupModelDefinition(
    string Id,
    string DisplayName,
    string Repository,
    string FileName,
    string DownloadUrl,
    long SizeBytes,
    string Sha256,
    string License,
    string LanguageSupport,
    string Summary,
    string SourceRevision,
    CleanupPromptContract Contract = CleanupPromptContract.Configurable);

/// <summary>
/// Local cleanup models. These mirror the macOS cleanup catalog exactly; the Windows runtime is
/// llama.cpp through LLamaSharp rather than LLM.swift, but the GGUF artifacts are identical.
/// </summary>
public static class CleanupModelCatalog
{
    /// <summary>macOS finetunedV4 "Muesli Cleanup Lite": the v12 Qwen 3.5 0.8B fine-tune.</summary>
    public const string MuesliCleanupId = "qwen35-postproc-v4";
    public const string ProLfmId = "lfm25-cleanup-v12";
    public const string ProQwenId = "qwen35-2b-cleanup-v12";
    public const string S1MiniId = "superwhisper-s1-mini";
    public const string QwenBasicId = "qwen35-0.8b";
    public const string DefaultModelId = MuesliCleanupId;

    /// <summary>
    /// macOS retires older Lite choices by resolving them to the current Cleanup Lite. The retired v3 file stays
    /// on disk until the user deletes it; it is never used again.
    /// </summary>
    public const string RetiredV3Id = "muesli-cleanup-qwen35-postproc-v3";

    private static readonly string[] RetiredLiteIds = [RetiredV3Id, "qwen35-postproc-v3", "qwen3-postproc-v2"];

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "muesli", "native-cleanup");

    public static IReadOnlyList<CleanupModelDefinition> Models { get; } =
    [
        // macOS ManagedCleanupModel (MAC-25): the v12 fine-tunes, pinned to the commits macOS ships.
        new CleanupModelDefinition(
            MuesliCleanupId,
            "Muesli Cleanup Lite",
            "Muesli-HQ/qwen35-0.8b-muesli-cleanup-v12-gguf",
            "qwen35-0.8b-muesli-v12-Q4_K_M.gguf",
            "https://huggingface.co/Muesli-HQ/qwen35-0.8b-muesli-cleanup-v12-gguf/resolve/0e6b7446a64b170881bfe1af4b3ba892d18ecf3a/qwen35-0.8b-muesli-v12-Q4_K_M.gguf?download=1",
            529_296_832,
            "3E3EABC91542E66F672230B7361917F5FA3A1E06ACE61E8157C7A34D121ABF8A",
            "First-party Muesli cleanup model",
            "English-first",
            "Fine-tuned from Qwen3.5-0.8B with extended training. Small and fast for everyday cleanup, with improved app and screen context.",
            "Muesli-HQ/qwen35-0.8b-muesli-cleanup-v12-gguf@0e6b7446a64b170881bfe1af4b3ba892d18ecf3a",
            CleanupPromptContract.TrainedV12Qwen),
        new CleanupModelDefinition(
            ProLfmId,
            "Muesli Pro Cleanup — LFM 2.5",
            "Muesli-HQ/lfm25-1.2b-muesli-cleanup-v12-gguf",
            "lfm25-muesli-v12-Q4_K_M.gguf",
            "https://huggingface.co/Muesli-HQ/lfm25-1.2b-muesli-cleanup-v12-gguf/resolve/9f8b4e25/lfm25-muesli-v12-Q4_K_M.gguf?download=1",
            730_898_432,
            "DA13D32708D27D29446C895314E50AA7FB700B76460057FED2AD773E9FA9C258",
            "LFM Open License v1.0 (published with the model on Hugging Face)",
            "English-first",
            "Fine-tuned from LFM2.5-1.2B. More capable cleanup with app and screen context. LFM Open License v1.0.",
            "Muesli-HQ/lfm25-1.2b-muesli-cleanup-v12-gguf@9f8b4e25",
            CleanupPromptContract.TrainedV12Lfm),
        new CleanupModelDefinition(
            ProQwenId,
            "Muesli Pro Cleanup — Qwen 3.5",
            "Muesli-HQ/qwen35-2b-muesli-cleanup-v12-gguf",
            "qwen35-2b-muesli-v12-Q4_K_M.gguf",
            "https://huggingface.co/Muesli-HQ/qwen35-2b-muesli-cleanup-v12-gguf/resolve/e35af097/qwen35-2b-muesli-v12-Q4_K_M.gguf?download=1",
            1_274_396_096,
            "2D59D2118170E0170FA294D71EBA0A701C4F26063F284F68A8BED880322C4143",
            "First-party Muesli cleanup model",
            "English-first",
            "Fine-tuned from Qwen3.5-2B. More capable cleanup with app and screen context.",
            "Muesli-HQ/qwen35-2b-muesli-cleanup-v12-gguf@e35af097",
            CleanupPromptContract.TrainedV12Qwen),
        new CleanupModelDefinition(
            S1MiniId,
            "S1-mini by Superwhisper",
            "superwhisper/s1-mini-GGUF",
            "s1-mini-q4_k_m.gguf",
            "https://huggingface.co/superwhisper/s1-mini-GGUF/resolve/main/s1-mini-q4_k_m.gguf",
            484_219_808,
            "3B41EBE2502CBD03E811D5D16B022F5AB551EDA58D62597D152F89535003C634",
            "Apache-2.0 (derivative of Qwen3-0.6B)",
            "English only",
            "Superwhisper's punctuation, truecasing, and inverse-text-normalization model. Uses its own fixed control line, so Muesli's configurable instructions do not apply.",
            "superwhisper/s1-mini-GGUF@main",
            CleanupPromptContract.S1Mini),
        new CleanupModelDefinition(
            QwenBasicId,
            "Qwen Basic Cleanup",
            "unsloth/Qwen3.5-0.8B-GGUF",
            "Qwen3.5-0.8B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q4_K_M.gguf",
            532_517_120,
            "BD258782E35F7F458F8ACED1ADC053E6E92E89BC735BA3BE89D38A06121DC517",
            "Apache-2.0",
            "Multilingual",
            "A general Qwen3.5 0.8B instruction model used for cleanup. Larger context, slower, and less tuned for dictation cleanup than Muesli Cleanup.",
            "unsloth/Qwen3.5-0.8B-GGUF@main")
    ];

    public static string PathFor(CleanupModelDefinition model) =>
        Path.Combine(ModelCacheDirectory, model.Id, model.FileName);

    public static bool TryGet(string? id, out CleanupModelDefinition model)
    {
        model = Models.FirstOrDefault(candidate =>
            candidate.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return model is not null;
    }

    public static CleanupModelDefinition? Get(string? id) => TryGet(id, out var model) ? model : null;

    /// <summary>Coerces a persisted id to a known model, or "" when unset. Never invents a model.</summary>
    public static string Normalize(string? id) =>
        RetiredLiteIds.Contains(id?.Trim(), StringComparer.OrdinalIgnoreCase) ? MuesliCleanupId : Get(id)?.Id ?? "";

    /// <summary>
    /// The model the cleanup stage should use. An explicit selection is honoured exactly: when that
    /// model is not installed the result is null so the caller reports "Needs model" instead of
    /// silently cleaning up with an unrelated file. Only when no catalog model is selected does a
    /// GGUF a user placed in the cache directly keep working.
    /// </summary>
    public static string? ResolveInstalledModelPath(string? selectedId)
    {
        if (TryGet(selectedId, out var selected))
        {
            var selectedPath = PathFor(selected);
            return HasGgufHeader(selectedPath) ? selectedPath : null;
        }

        return LegacyModelPath();
    }

    /// <summary>
    /// macOS rejects an interrupted GGUF by its header. Catalog downloads are already atomic and hashed; this
    /// also covers a file someone placed in the cache that was cut short or isn't a GGUF.
    /// </summary>
    public static bool HasGgufHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[8];
            // "GGUF" then a little-endian version; a file shorter than the fixed header is incomplete.
            return stream.Length > 24 && stream.ReadAtLeast(magic, 8, throwOnEndOfStream: false) == 8 &&
                   magic[0] == (byte)'G' && magic[1] == (byte)'G' && magic[2] == (byte)'U' && magic[3] == (byte)'F' &&
                   BitConverter.ToUInt32(magic[4..]) is >= 1 and <= 3;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? LegacyModelPath()
    {
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return null;
        }

        var known = Models.Select(PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retired = Path.Combine(ModelCacheDirectory, RetiredV3Id) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(ModelCacheDirectory, "*.gguf", SearchOption.AllDirectories)
            .Where(path => !known.Contains(path) && !path.StartsWith(retired, StringComparison.OrdinalIgnoreCase))
            .Where(HasGgufHeader)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static CleanupModelDefinition? DefinitionForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Models.FirstOrDefault(model =>
            PathFor(model).Equals(path, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record CleanupModelSnapshot(
    CleanupModelDefinition Model,
    bool IsInstalled,
    bool IsVerified,
    bool IsSelected,
    string StatusText,
    string DiskSize);

/// <summary>Managed lifecycle for one cleanup model. Selection never triggers a download.</summary>
public sealed class CleanupModelInstaller
{
    private readonly CleanupModelDefinition _model;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string? _path;

    /// <param name="path">Where the file goes when it isn't a cleanup model (Cotypist's typeahead models).</param>
    public CleanupModelInstaller(CleanupModelDefinition model, string? path = null)
    {
        _model = model;
        _path = path;
    }

    public string ModelPath => _path ?? CleanupModelCatalog.PathFor(_model);
    private string VerificationStampPath => Path.Combine(Path.GetDirectoryName(ModelPath)!, ".muesli-verified.json");
    public string DisplayName => _model.DisplayName;
    public string Id => _model.Id;

    public bool IsInstalled => File.Exists(ModelPath);

    public bool IsVerified
    {
        get
        {
            if (!IsInstalled)
            {
                return false;
            }

            try
            {
                if (!File.Exists(VerificationStampPath))
                {
                    return false;
                }

                var stamp = JsonSerializer.Deserialize<CleanupVerificationStamp>(File.ReadAllText(VerificationStampPath));
                var info = new FileInfo(ModelPath);
                return stamp is not null &&
                       stamp.Sha256.Equals(_model.Sha256, StringComparison.OrdinalIgnoreCase) &&
                       stamp.Length == info.Length &&
                       stamp.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return false;
            }
        }
    }

    public long DiskSizeBytes => IsInstalled ? new FileInfo(ModelPath).Length : 0;

    public string DiskSizeLabel => DiskSizeBytes switch
    {
        >= 1_073_741_824 => $"{DiskSizeBytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{DiskSizeBytes / 1_048_576.0:0} MB",
        > 0 => $"{DiskSizeBytes} B",
        _ => ""
    };

    public async Task<ModelOperationResult> PrepareAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        FileStream? processLock = null;
        try
        {
            var directory = Path.GetDirectoryName(ModelPath)!;
            Directory.CreateDirectory(directory);
            processLock = await CrossProcessFileLock.AcquireAsync(
                Path.Combine(_path is null ? CleanupModelCatalog.ModelCacheDirectory : directory, $".muesli-cleanup-{_model.Id}.lock"),
                TimeSpan.FromMinutes(30),
                cancellationToken);

            if (IsVerified)
            {
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                return new ModelOperationResult($"{_model.DisplayName} is downloaded and verified.", ModelPath);
            }

            if (IsInstalled && await VerifyFileAsync(cancellationToken))
            {
                await WriteStampAsync(cancellationToken);
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                return new ModelOperationResult($"{_model.DisplayName} passed SHA-256 verification.", ModelPath);
            }

            var partPath = $"{ModelPath}.part";
            var downloadPath = $"{ModelPath}.{Guid.NewGuid():N}.download";
            try
            {
                await DownloadAsync(downloadPath, partPath, progress, cancellationToken);
                if (!await VerifyPathAsync(downloadPath, cancellationToken))
                {
                    throw new InvalidDataException($"{_model.DisplayName} failed SHA-256 verification after download.");
                }

                if (File.Exists(ModelPath))
                {
                    File.Delete(ModelPath);
                }

                File.Move(downloadPath, ModelPath);
                await WriteStampAsync(cancellationToken);
                progress?.Report(new ModelDownloadProgress($"{_model.DisplayName} verified", 1, 1));
                return new ModelOperationResult($"{_model.DisplayName} is downloaded and verified.", ModelPath);
            }
            finally
            {
                TryDelete(downloadPath);
                TryDelete(partPath);
            }
        }
        finally
        {
            processLock?.Dispose();
            _gate.Release();
        }
    }

    public async Task<ModelOperationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsInstalled)
            {
                throw new InvalidOperationException($"{_model.DisplayName} is not downloaded.");
            }

            if (!await VerifyFileAsync(cancellationToken))
            {
                throw new InvalidDataException($"{_model.DisplayName} failed SHA-256 verification.");
            }

            await WriteStampAsync(cancellationToken);
            return new ModelOperationResult($"{_model.DisplayName} passed SHA-256 verification.", ModelPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModelOperationResult> DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(ModelPath)!;
            if (File.Exists(ModelPath))
            {
                File.Delete(ModelPath);
            }

            if (File.Exists(VerificationStampPath))
            {
                File.Delete(VerificationStampPath);
            }

            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return new ModelOperationResult($"{_model.DisplayName} files were deleted.", ModelPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The completed file keeps the final name only after VerifyPathAsync, so an interrupted
    // download is never mistaken for an installed model; partial bytes resume from the shared
    // engine's resume folder.
    private Task DownloadAsync(
        string destinationPath,
        string partPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        ModelFileDownloads.DownloadFileAsync(
            _model.DownloadUrl, destinationPath, null, $"Downloading {_model.DisplayName}", progress, cancellationToken);

    private Task<bool> VerifyFileAsync(CancellationToken cancellationToken) =>
        VerifyPathAsync(ModelPath, cancellationToken);

    private async Task<bool> VerifyPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.Length != _model.SizeBytes)
        {
            return false;
        }

        await using var stream = info.OpenRead();
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(_model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task WriteStampAsync(CancellationToken cancellationToken)
    {
        var info = new FileInfo(ModelPath);
        await using var stream = info.OpenRead();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        Directory.CreateDirectory(Path.GetDirectoryName(VerificationStampPath)!);
        File.WriteAllText(
            VerificationStampPath,
            JsonSerializer.Serialize(
                new CleanupVerificationStamp(_model.Id, hash, info.Length, info.LastWriteTimeUtc.Ticks),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record CleanupVerificationStamp(
        [property: JsonPropertyName("modelId")] string ModelId,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("length")] long Length,
        [property: JsonPropertyName("lastWriteTimeUtcTicks")] long LastWriteTimeUtcTicks);
}

/// <summary>Snapshots and lifecycle operations the Models page cleanup category renders.</summary>
public sealed class CleanupModelLifecycleService
{
    public CleanupModelLifecycleService(string? selectedModelId = null) =>
        SelectedModelId = CleanupModelCatalog.Normalize(selectedModelId);

    public string SelectedModelId { get; }

    public IReadOnlyList<CleanupModelSnapshot> Snapshots()
    {
        var result = new List<CleanupModelSnapshot>(CleanupModelCatalog.Models.Count);
        foreach (var model in CleanupModelCatalog.Models)
        {
            var installer = new CleanupModelInstaller(model);
            var selected = model.Id.Equals(SelectedModelId, StringComparison.OrdinalIgnoreCase);
            var status = !NativeTextCleanupService.IsRuntimeAvailable
                ? "Runtime unavailable"
                : installer.IsVerified
                    ? selected ? "Selected" : "Downloaded"
                    : installer.IsInstalled
                        ? "Installed · needs verification"
                        : "Not downloaded";
            result.Add(new CleanupModelSnapshot(
                model,
                installer.IsInstalled,
                installer.IsVerified,
                selected,
                status,
                installer.DiskSizeLabel));
        }

        return result;
    }

    public CleanupModelInstaller InstallerFor(string modelId)
    {
        if (!CleanupModelCatalog.TryGet(modelId, out var model))
        {
            throw new InvalidOperationException($"Unknown cleanup model id '{modelId}'.");
        }

        return new CleanupModelInstaller(model);
    }

    public Task<ModelOperationResult> PrepareAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken) =>
        InstallerFor(modelId).PrepareAsync(progress, cancellationToken);

    public Task<ModelOperationResult> VerifyAsync(string modelId, CancellationToken cancellationToken) =>
        InstallerFor(modelId).VerifyAsync(cancellationToken);

    public Task<ModelOperationResult> DeleteAsync(string modelId, CancellationToken cancellationToken) =>
        InstallerFor(modelId).DeleteAsync(cancellationToken);

    public Task<ModelOperationResult> RetryAsync(string modelId, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken) =>
        InstallerFor(modelId).PrepareAsync(progress, cancellationToken);
}
