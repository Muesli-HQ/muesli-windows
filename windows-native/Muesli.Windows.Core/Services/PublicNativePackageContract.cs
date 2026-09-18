using System.IO;
using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>
/// Public Wave 0 package truth. CUDA is optional external staging, not a shipped provider.
/// </summary>
internal static class PublicNativePackageContract
{
    public const string CatalogRelativePath =
        "windows-native/Muesli.Windows.Core/NativeRuntime/public-cpu-native-catalog.json";

    // Archived reference for the optional external CUDA staging contract. CUDA is not shipped in
    // the public CPU-only package; the manifest now lives with the retired WPF reference tree.
    public const string CudaManifestRelativePath =
        "windows-native/Muesli.Windows.Wpf.Legacy/NativeRuntime/SherpaOnnxCuda/native-sherpa-cuda-runtime.json";

    public const string ExpectedSherpaRuntimeVersion = "1.13.4";

    public const string PublicPackageDisclosure =
        "Public Wave 0 package includes the CPU Sherpa provider only. NVIDIA CUDA is not included. A version-matched CUDA provider is optional external staging and is not shipped until L11 qualifies it.";

    public const string FalseCudaIncludedClaim =
        "The primary Muesli package includes the version-matched sherpa-onnx CUDA provider";

    public static readonly string[] ForbiddenPublicCudaFiles =
    [
        "onnxruntime_providers_cuda.dll",
        "onnxruntime_providers_shared.dll",
        "cublasLt64_12.dll",
        "cublas64_12.dll",
        "cufft64_11.dll",
        "cudart64_12.dll",
        "cudnn64_9.dll",
        "cudnn_graph64_9.dll",
        "cudnn_engines_runtime_compiled64_9.dll",
        "cudnn_engines_precompiled64_9.dll",
        "cudnn_heuristic64_9.dll",
        "cudnn_ops64_9.dll",
        "cudnn_adv64_9.dll",
        "cudnn_cnn64_9.dll"
    ];

    public static NativeCudaBundleValidation ValidateCudaBundle(
        string directory,
        string expectedRuntimeVersion = ExpectedSherpaRuntimeVersion)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return NativeCudaBundleValidation.Reject("CUDA bundle directory is missing.");
        }

        var manifestPath = Path.Combine(directory, "native-sherpa-cuda-runtime.json");
        if (!File.Exists(manifestPath))
        {
            return NativeCudaBundleValidation.Reject(
                $"CUDA bundle unavailable at {directory}: missing native-sherpa-cuda-runtime.json.");
        }

        NativeCudaManifestFiles manifest;
        try
        {
            manifest = NativeCudaManifestFiles.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception exception)
        {
            return NativeCudaBundleValidation.Reject(
                $"CUDA runtime manifest is invalid at {manifestPath}: {exception.Message}");
        }

        if (!string.Equals(manifest.RuntimeVersion, expectedRuntimeVersion, StringComparison.Ordinal))
        {
            return NativeCudaBundleValidation.Reject(
                $"CUDA bundle at {directory} has runtime version '{manifest.RuntimeVersion ?? "unknown"}'; expected {expectedRuntimeVersion}.");
        }

        var missingRuntimeFiles = manifest.RequiredRuntimeFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        if (missingRuntimeFiles.Length > 0)
        {
            return NativeCudaBundleValidation.Reject(
                $"CUDA bundle incomplete at {directory}: missing {string.Join(", ", missingRuntimeFiles)}.");
        }

        var missingNvidiaFiles = manifest.RequiredNvidiaFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        if (missingNvidiaFiles.Length > 0)
        {
            return NativeCudaBundleValidation.Reject(
                $"CUDA bundle found at {directory}, but NVIDIA dependencies are incomplete. Missing: {string.Join(", ", missingNvidiaFiles)}.");
        }

        return NativeCudaBundleValidation.Pass(
            $"CUDA bundle files at {directory} match sherpa-onnx {expectedRuntimeVersion} plus CUDA 12 / cuDNN 9. Public packaging still requires L11 qualification.");
    }

    public static NativeCudaManifestFiles ReadRepositoryCudaManifest(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, CudaManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return NativeCudaManifestFiles.Parse(File.ReadAllText(path));
    }
}

internal sealed record NativeCudaBundleValidation(bool IsAccepted, string Diagnostic)
{
    public static NativeCudaBundleValidation Pass(string diagnostic) => new(true, diagnostic);

    public static NativeCudaBundleValidation Reject(string diagnostic) => new(false, diagnostic);
}

internal sealed record NativeCudaManifestFiles(
    string? RuntimeVersion,
    string[] RequiredRuntimeFiles,
    string[] RequiredNvidiaFiles)
{
    public static NativeCudaManifestFiles Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var runtimeVersion = root.TryGetProperty("runtimeVersion", out var versionElement)
            ? versionElement.GetString()
            : null;
        return new NativeCudaManifestFiles(
            runtimeVersion,
            ReadStringArray(root, "requiredRuntimeFiles"),
            ReadStringArray(root, "requiredNvidiaFiles"));
    }

    private static string[] ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element.EnumerateArray()
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }
}
