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
    S1Mini
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
    string SourceRevision);

/// <summary>
/// Local cleanup models. These mirror the macOS cleanup catalog exactly; the Windows runtime is
/// llama.cpp through LLamaSharp rather than LLM.swift, but the GGUF artifacts are identical.
/// </summary>
public static class CleanupModelCatalog
{
    public const string MuesliCleanupId = "muesli-cleanup-qwen35-postproc-v3";
    public const string S1MiniId = "superwhisper-s1-mini";
    public const string QwenBasicId = "qwen35-0.8b";
    public const string DefaultModelId = MuesliCleanupId;

    public static string ModelCacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_NATIVE_CLEANUP_CACHE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "muesli", "native-cleanup");

    public static IReadOnlyList<CleanupModelDefinition> Models { get; } =
    [
        new CleanupModelDefinition(
            MuesliCleanupId,
            "Muesli Cleanup",
            "phequals/qwen35-postproc-v3-gguf",
            "qwen35-postproc-v3-Q4_K_M.gguf",
            "https://huggingface.co/phequals/qwen35-postproc-v3-gguf/resolve/main/qwen35-postproc-v3-Q4_K_M.gguf",
            529_296_768,
            "DD44D9F850B9BE16565456523535ADDDE907148922F7CD3578651B09AA230703",
            "First-party Muesli cleanup model (no separate license file published by the host)",
            "English-first; configurable cleanup instructions",
            "The default Muesli cleanup model. Removes filler words, resolves \"scratch that\", fixes punctuation, and formats lists.",
            "phequals/qwen35-postproc-v3-gguf@main"),
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
            "superwhisper/s1-mini-GGUF@main"),
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
    public static string Normalize(string? id) => Get(id)?.Id ?? "";

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
            return File.Exists(selectedPath) ? selectedPath : null;
        }

        return LegacyModelPath();
    }

    public static string? LegacyModelPath()
    {
        if (!Directory.Exists(ModelCacheDirectory))
        {
            return null;
        }

        var known = Models.Select(PathFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(ModelCacheDirectory, "*.gguf", SearchOption.AllDirectories)
            .Where(path => !known.Contains(path))
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

    public CleanupModelInstaller(CleanupModelDefinition model) => _model = model;

    public string ModelPath => CleanupModelCatalog.PathFor(_model);
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
                Path.Combine(CleanupModelCatalog.ModelCacheDirectory, $".muesli-cleanup-{_model.Id}.lock"),
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

    private async Task DownloadAsync(
        string destinationPath,
        string partPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_model.DownloadUrl, UriKind.Absolute);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cleanup model downloads must use HTTPS.");
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new ModelDownloadProgress($"Downloading {_model.DisplayName}", received, totalBytes));
            }
        }

        // The .part name exists only while a download is in flight; the completed file keeps the
        // final name so an interrupted download is never mistaken for an installed model.
        if (File.Exists(partPath))
        {
            File.Delete(partPath);
        }
    }

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
