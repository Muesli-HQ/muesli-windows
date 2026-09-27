using System.Formats.Tar;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>One pinned file inside the CUDA acceleration pack.</summary>
public sealed record CudaPackFile(string Name, long SizeBytes, string Sha256);

/// <summary>
/// The optional NVIDIA acceleration pack. It contains only the sherpa-onnx / ONNX Runtime
/// CUDA binaries published as Apache-2.0 by the sherpa-onnx project at an exact release tag.
/// NVIDIA's own CUDA and cuDNN redistributables are never downloaded, staged, or shipped here;
/// the pack is only usable when the user's machine already provides a matching NVIDIA runtime.
/// </summary>
public static class CudaAccelerationPack
{
    public const string PackVersion = "sherpa-onnx-1.13.4-cuda12-cudnn9-win-x64";
    public const string DisplayName = "NVIDIA CUDA acceleration pack";

    public const string SherpaRuntimeVersion = "1.13.4";
    public const string OnnxRuntimeVersion = "1.24.4";
    /// <summary>
    /// The ONNX Runtime build shipped inside the pinned archive, as its Win32 file version resource
    /// reports it. The provenance gate compares against this exact string.
    /// </summary>
    public const string OnnxRuntimeFileVersion = "1.24.20260316.3.2d92497";
    public const string CudaVersion = "12.x";
    public const string CudnnVersion = "9.x";

    public const string ArchiveUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.4/sherpa-onnx-v1.13.4-cuda-12.x-cudnn-9.x-win-x64-cuda.tar.bz2";
    public const string ArchiveSha256 =
        "11B56076060C109E16D85EBA3ACFB03F6D0C4A738ECD2A99DD45EB689B2051A4";
    public const long ArchiveSizeBytes = 310_807_279;

    /// <summary>Directory inside the archive that holds the runtime libraries.</summary>
    public const string ArchiveLibraryPrefix = "sherpa-onnx-v1.13.4-cuda-12.x-cudnn-9.x-win-x64-cuda/lib/";

    public const string ManifestFileName = "native-sherpa-cuda-runtime.json";

    /// <summary>
    /// Every file the acceleration pack must install. Hashes were computed from the pinned
    /// archive after downloading it and matching its GitHub-published SHA-256.
    /// </summary>
    public static readonly IReadOnlyList<CudaPackFile> RequiredFiles =
    [
        new("onnxruntime.dll", 14_430_752, "3B46571D12A9567791A42A2B2967A79C4E2E957AACDBA09A2DDB4FB391707BAA"),
        new("onnxruntime_providers_shared.dll", 22_040, "1BCBAD19D14BC8395C1422C752E9E4CDD79E316E2582CDCD767BB8F500CDAE99"),
        new("onnxruntime_providers_cuda.dll", 275_606_552, "CE1C698CAE708FD4ED9AB36EDDF256213DF40779D0806D20F32132C5D422DA72"),
        new("sherpa-onnx-c-api.dll", 4_544_000, "BA5097086FDE22FE20C2ACE1AD03E83F2C39D4C0199793C6469F93A48A056D14")
    ];

    public static long InstalledSizeBytes => RequiredFiles.Sum(file => file.SizeBytes);

    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "muesli",
        "native-sherpa-cuda");

    public static string InstallDirectory => Path.Combine(InstallRoot, PackVersion);

    public static string VerificationStampPath => Path.Combine(InstallDirectory, ".muesli-cuda-pack-verified.json");

    public static string SizeLabel => $"{InstalledSizeBytes / 1_048_576.0:0} MB";

    public static bool IsInstalled =>
        Directory.Exists(InstallDirectory) &&
        RequiredFiles.All(file => File.Exists(Path.Combine(InstallDirectory, file.Name))) &&
        ValidateManifest(InstallDirectory).IsValid &&
        HasValidVerificationStamp();

    /// <summary>Full SHA-256 re-verification of every installed pack file.</summary>
    public static async Task<CudaPackVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return CudaPackVerification.Missing("The CUDA acceleration pack is not installed.");
        }

        var manager = new CudaAccelerationPackInstaller();
        return await manager.VerifyFilesAsync(cancellationToken);
    }

    private static bool HasValidVerificationStamp()
    {
        try
        {
            if (!File.Exists(VerificationStampPath))
            {
                return false;
            }

            var stamp = JsonSerializer.Deserialize<CudaPackStamp>(File.ReadAllText(VerificationStampPath));
            if (stamp is null || !stamp.PackVersion.Equals(PackVersion, StringComparison.Ordinal))
            {
                return false;
            }

            return RequiredFiles.All(file =>
            {
                var info = new FileInfo(Path.Combine(InstallDirectory, file.Name));
                return info.Exists &&
                       info.Length == file.SizeBytes &&
                       stamp.Files.Any(entry =>
                           entry.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase) &&
                           entry.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) &&
                           entry.Length == info.Length &&
                           entry.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks);
            });
        }
        catch
        {
            return false;
        }
    }

    internal static CudaPackVerification ValidateManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return CudaPackVerification.Fail("The CUDA acceleration pack manifest is missing.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            bool Matches(string propertyName, string expected) =>
                root.TryGetProperty(propertyName, out var element) &&
                string.Equals(element.GetString(), expected, StringComparison.Ordinal);

            if (!Matches("runtimeVersion", SherpaRuntimeVersion) ||
                !Matches("onnxRuntimeVersion", OnnxRuntimeVersion) ||
                !Matches("onnxRuntimeFileVersion", OnnxRuntimeFileVersion) ||
                !Matches("cudaVersion", CudaVersion) ||
                !Matches("cudnnVersion", CudnnVersion) ||
                !Matches("archiveUrl", ArchiveUrl) ||
                !Matches("archiveSha256", ArchiveSha256) ||
                !root.TryGetProperty("archiveSizeBytes", out var archiveSize) ||
                archiveSize.GetInt64() != ArchiveSizeBytes)
            {
                return CudaPackVerification.Fail(
                    "The CUDA acceleration pack manifest does not match the pinned runtime provenance.");
            }

            var runtimeFiles = root.GetProperty("requiredRuntimeFiles")
                .EnumerateArray()
                .Select(element => element.GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!runtimeFiles.SetEquals(RequiredFiles.Select(file => file.Name)))
            {
                return CudaPackVerification.Fail(
                    "The CUDA acceleration pack manifest has an unexpected runtime file inventory.");
            }

            var nvidiaFiles = root.GetProperty("requiredNvidiaFiles")
                .EnumerateArray()
                .Select(element => element.GetString() ?? "")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!nvidiaFiles.SetEquals(NativeSherpaRuntime.NvidiaDependencyFileNames))
            {
                return CudaPackVerification.Fail(
                    "The CUDA acceleration pack manifest has an unexpected NVIDIA dependency inventory.");
            }

            var hashes = root.GetProperty("fileSha256");
            foreach (var expected in RequiredFiles)
            {
                if (!hashes.TryGetProperty(expected.Name, out var hash) ||
                    !string.Equals(hash.GetString(), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return CudaPackVerification.Fail(
                        $"The CUDA acceleration pack manifest has an invalid SHA-256 entry for {expected.Name}.");
                }
            }

            return CudaPackVerification.Pass(
                $"{DisplayName} manifest matches the pinned archive and runtime inventory.");
        }
        catch (Exception exception)
        {
            return CudaPackVerification.Fail(
                $"The CUDA acceleration pack manifest is invalid: {exception.Message}");
        }
    }

    internal sealed record CudaPackStamp(
        [property: JsonPropertyName("packVersion")] string PackVersion,
        [property: JsonPropertyName("archiveSha256")] string ArchiveSha256,
        [property: JsonPropertyName("files")] List<CudaPackStampFile> Files);

    internal sealed record CudaPackStampFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("length")] long Length,
        [property: JsonPropertyName("lastWriteTimeUtcTicks")] long LastWriteTimeUtcTicks);
}

public sealed record CudaPackVerification(bool IsValid, string Diagnostic)
{
    public static CudaPackVerification Pass(string diagnostic) => new(true, diagnostic);
    public static CudaPackVerification Fail(string diagnostic) => new(false, diagnostic);
    public static CudaPackVerification Missing(string diagnostic) => new(false, diagnostic);
}

/// <summary>
/// Managed lifecycle for the CUDA acceleration pack: download, hash, selective extraction,
/// atomic install, verify, delete. Nothing here is activated by installing it; activating CUDA
/// is an explicit execution-provider choice.
/// </summary>
public sealed class CudaAccelerationPackInstaller
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public Task<ModelOperationResult> PrepareAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? localArchivePath = null) =>
        RunExclusiveAsync(async () =>
        {
            if (CudaAccelerationPack.IsInstalled)
            {
                progress?.Report(new ModelDownloadProgress($"{CudaAccelerationPack.DisplayName} verified", 1, 1));
                return new ModelOperationResult(
                    $"{CudaAccelerationPack.DisplayName} is installed and verified.",
                    CudaAccelerationPack.InstallDirectory);
            }

            Directory.CreateDirectory(CudaAccelerationPack.InstallRoot);
            FileStream? processLock = null;
            var staging = Path.Combine(CudaAccelerationPack.InstallRoot, $".{CudaAccelerationPack.PackVersion}.{Guid.NewGuid():N}.staging");
            var archive = Path.Combine(CudaAccelerationPack.InstallRoot, $".{CudaAccelerationPack.PackVersion}.{Guid.NewGuid():N}.download");
            var ownsArchive = string.IsNullOrWhiteSpace(localArchivePath);
            try
            {
                processLock = await CrossProcessFileLock.AcquireAsync(
                    Path.Combine(CudaAccelerationPack.InstallRoot, ".muesli-cuda-pack.lock"),
                    TimeSpan.FromMinutes(30),
                    cancellationToken);

                if (!ownsArchive)
                {
                    archive = Path.GetFullPath(localArchivePath!);
                    if (!File.Exists(archive))
                    {
                        throw new FileNotFoundException("The CUDA acceleration pack archive was not found.", archive);
                    }

                    await VerifyArchiveAsync(archive, cancellationToken);
                }
                else
                {
                    await DownloadArchiveAsync(archive, progress, cancellationToken);
                }

                progress?.Report(new ModelDownloadProgress("Extracting NVIDIA CUDA acceleration pack", 0, null));

                Directory.CreateDirectory(staging);
                ExtractRequiredFiles(archive, staging, cancellationToken);
                var verification = await VerifyFilesInAsync(staging, cancellationToken);
                if (!verification.IsValid)
                {
                    throw new InvalidDataException(verification.Diagnostic);
                }

                WriteManifest(staging);
                var manifestVerification = CudaAccelerationPack.ValidateManifest(staging);
                if (!manifestVerification.IsValid)
                {
                    throw new InvalidDataException(manifestVerification.Diagnostic);
                }
                await WriteStampAsync(staging, cancellationToken);

                if (Directory.Exists(CudaAccelerationPack.InstallDirectory))
                {
                    Directory.Delete(CudaAccelerationPack.InstallDirectory, recursive: true);
                }

                Directory.Move(staging, CudaAccelerationPack.InstallDirectory);
                progress?.Report(new ModelDownloadProgress($"{CudaAccelerationPack.DisplayName} verified", 1, 1));
                return new ModelOperationResult(
                    $"{CudaAccelerationPack.DisplayName} is installed and verified.",
                    CudaAccelerationPack.InstallDirectory);
            }
            finally
            {
                processLock?.Dispose();
                if (ownsArchive)
                {
                    TryDeleteFile(archive);
                }

                TryDeleteDirectory(staging);
            }
        }, cancellationToken);

    public Task<CudaPackVerification> VerifyAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            () => VerifyFilesInAsync(CudaAccelerationPack.InstallDirectory, cancellationToken),
            cancellationToken);

    public Task<ModelOperationResult> DeleteAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            if (Directory.Exists(CudaAccelerationPack.InstallDirectory))
            {
                Directory.Delete(CudaAccelerationPack.InstallDirectory, recursive: true);
            }

            return Task.FromResult(new ModelOperationResult(
                $"{CudaAccelerationPack.DisplayName} files were deleted.",
                CudaAccelerationPack.InstallDirectory));
        }, cancellationToken);

    /// <summary>
    /// Reports the NVIDIA runtime files the pack needs but that are not resolvable. The pack
    /// itself never carries these; they come from a local CUDA Toolkit install or the optional
    /// dependency staging script.
    /// </summary>
    public static IReadOnlyList<string> MissingNvidiaFiles() =>
        NativeSherpaRuntime.ResolveMissingNvidiaDependencies(CudaAccelerationPack.InstallDirectory);

    internal async Task<CudaPackVerification> VerifyFilesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(CudaAccelerationPack.InstallDirectory))
        {
            return CudaPackVerification.Missing("The CUDA acceleration pack is not installed.");
        }

        var verification = await VerifyFilesInAsync(CudaAccelerationPack.InstallDirectory, cancellationToken);
        return verification.IsValid
            ? CudaAccelerationPack.ValidateManifest(CudaAccelerationPack.InstallDirectory)
            : verification;
    }

    private static async Task<CudaPackVerification> VerifyFilesInAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        foreach (var expected in CudaAccelerationPack.RequiredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, expected.Name);
            if (!File.Exists(path))
            {
                return CudaPackVerification.Fail($"The CUDA acceleration pack is missing {expected.Name}.");
            }

            var info = new FileInfo(path);
            if (info.Length != expected.SizeBytes)
            {
                return CudaPackVerification.Fail(
                    $"The CUDA acceleration pack file {expected.Name} is {info.Length} bytes; expected {expected.SizeBytes}.");
            }

            await using var stream = info.OpenRead();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return CudaPackVerification.Fail(
                    $"The CUDA acceleration pack file {expected.Name} failed SHA-256 verification. Expected {expected.Sha256}; found {actual}.");
            }
        }

        return CudaPackVerification.Pass(
            $"{CudaAccelerationPack.DisplayName} {CudaAccelerationPack.SherpaRuntimeVersion} files match the pinned archive.");
    }

    private static async Task VerifyArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(CudaAccelerationPack.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The CUDA acceleration pack archive failed SHA-256 verification. Expected {CudaAccelerationPack.ArchiveSha256}; found {actual}.");
        }
    }

    private static async Task DownloadArchiveAsync(
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(CudaAccelerationPack.ArchiveUrl, UriKind.Absolute);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The CUDA acceleration pack download must use HTTPS.");
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
                progress?.Report(new ModelDownloadProgress(
                    $"Downloading {CudaAccelerationPack.DisplayName}",
                    received,
                    totalBytes));
            }
        }

        await using var stream = File.OpenRead(destinationPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(CudaAccelerationPack.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The CUDA acceleration pack archive failed SHA-256 verification. Expected {CudaAccelerationPack.ArchiveSha256}; found {actual}.");
        }
    }

    /// <summary>
    /// Extracts only the pinned runtime files. The archive also carries a CLI toolset and a
    /// 275 MB TensorRT provider that Muesli never loads, and extracting them would multiply the
    /// install size for no product capability.
    ///
    /// The sherpa-onnx 1.13.4 GPU archive uses a tar variant that SharpCompress' tar reader
    /// rejects, so the bzip2 layer is decompressed explicitly and .NET's tar reader consumes it.
    /// </summary>
    private static void ExtractRequiredFiles(
        string archivePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        var wanted = CudaAccelerationPack.RequiredFiles
            .Select(file => file.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var file = File.OpenRead(archivePath);
        using var bzip2 = SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            file,
            SharpCompress.Compressors.CompressionMode.Decompress,
            decompressConcatenated: false,
            leaveOpen: false);
        using var reader = new TarReader(bzip2, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.EntryType is TarEntryType.Directory)
            {
                continue;
            }

            var key = entry.Name ?? "";
            var fileName = Path.GetFileName(key);
            if (!wanted.Contains(fileName) ||
                !key.Replace('\\', '/').Contains("/lib/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = SafeArchiveExtractor.ResolveEntryDestination(stagingDirectory, fileName);
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                entry.DataStream?.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            extracted.Add(fileName);
        }

        var missing = wanted.Where(name => !extracted.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"The CUDA acceleration pack archive is missing {string.Join(", ", missing)}.");
        }
    }

    private static void WriteManifest(string directory)
    {
        var manifest = new
        {
            runtimeVersion = CudaAccelerationPack.SherpaRuntimeVersion,
            onnxRuntimeVersion = CudaAccelerationPack.OnnxRuntimeVersion,
            onnxRuntimeFileVersion = CudaAccelerationPack.OnnxRuntimeFileVersion,
            cudaVersion = CudaAccelerationPack.CudaVersion,
            cudnnVersion = CudaAccelerationPack.CudnnVersion,
            archiveUrl = CudaAccelerationPack.ArchiveUrl,
            archiveSha256 = CudaAccelerationPack.ArchiveSha256,
            archiveSizeBytes = CudaAccelerationPack.ArchiveSizeBytes,
            source = "github.com/k2-fsa/sherpa-onnx releases v1.13.4 (Apache-2.0)",
            requiredRuntimeFiles = CudaAccelerationPack.RequiredFiles.Select(file => file.Name).ToArray(),
            requiredNvidiaFiles = NativeSherpaRuntime.NvidiaDependencyFileNames,
            fileSha256 = CudaAccelerationPack.RequiredFiles.ToDictionary(
                file => file.Name,
                file => file.Sha256,
                StringComparer.OrdinalIgnoreCase)
        };
        File.WriteAllText(
            Path.Combine(directory, CudaAccelerationPack.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WriteStampAsync(string directory, CancellationToken cancellationToken)
    {
        var files = new List<CudaAccelerationPack.CudaPackStampFile>();
        foreach (var expected in CudaAccelerationPack.RequiredFiles)
        {
            var info = new FileInfo(Path.Combine(directory, expected.Name));
            await using var stream = info.OpenRead();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            files.Add(new CudaAccelerationPack.CudaPackStampFile(expected.Name, hash, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        File.WriteAllText(
            Path.Combine(directory, ".muesli-cuda-pack-verified.json"),
            JsonSerializer.Serialize(
                new CudaAccelerationPack.CudaPackStamp(
                    CudaAccelerationPack.PackVersion,
                    CudaAccelerationPack.ArchiveSha256,
                    files),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
