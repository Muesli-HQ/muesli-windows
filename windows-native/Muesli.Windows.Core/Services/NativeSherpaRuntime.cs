using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SherpaOnnx;

namespace Muesli.Windows.Services;

internal static class NativeSherpaRuntime
{
    private const string ExpectedRuntimeVersion = "1.13.4";
    private const string ManifestFileName = "native-sherpa-cuda-runtime.json";

    private static readonly string[] CudaRuntimeFiles =
    [
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "onnxruntime_providers_cuda.dll",
        "sherpa-onnx-c-api.dll"
    ];

    private static readonly string[] NvidiaDependencyFiles =
    [
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

    private static readonly object Gate = new();
    private static bool _checked;
    private static bool _isAvailable;
    private static IntPtr _sherpaHandle;
    private static bool _resolverRegistered;
    private static string _selectedRuntime = "not initialized";
    private static string _diagnostic = "Sherpa native runtime selection has not run.";
    private static string? _cudaRuntimeDirectory;
    private static readonly List<IntPtr> DllDirectoryCookies = [];

    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _isAvailable;
        }
    }

    public static bool IsCudaCapable
    {
        get
        {
            EnsureLoaded();
            return _isAvailable &&
                   _selectedRuntime.Equals("cuda", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string SelectedRuntime
    {
        get
        {
            EnsureLoaded();
            return _selectedRuntime;
        }
    }

    public static string Diagnostic
    {
        get
        {
            EnsureLoaded();
            return _diagnostic;
        }
    }

    public static string? CudaRuntimeDirectory
    {
        get
        {
            EnsureLoaded();
            return _cudaRuntimeDirectory;
        }
    }

    public static string GpuDependencyCacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "muesli",
            "native-sherpa-cuda-dependencies",
            "cuda12-cudnn9");

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_checked)
            {
                return;
            }

            _isAvailable = TryLoad();
            _checked = true;
        }
    }

    private static bool TryLoad()
    {
        var cudaDiagnostics = new List<string>();
        foreach (var directory in CudaBundleDirectories().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoadCudaRuntime(directory, out var diagnostic))
            {
                _selectedRuntime = "cuda";
                _cudaRuntimeDirectory = directory;
                _diagnostic = diagnostic;
                return true;
            }

            cudaDiagnostics.Add(diagnostic);
        }

        foreach (var directory in CpuRuntimeDirectories())
        {
            TryLoadDependency(Path.Combine(directory, "onnxruntime.dll"));
            if (TryLoadPath(Path.Combine(directory, "sherpa-onnx-c-api.dll"), out var cpuHandle))
            {
                _sherpaHandle = cpuHandle;
                RegisterDllImportResolver();
                _selectedRuntime = "cpu";
                _diagnostic = BuildCpuDiagnostic(
                    cudaDiagnostics,
                    $"CPU Sherpa runtime loaded from {directory}.");
                return true;
            }
        }

        _selectedRuntime = "unavailable";
        _diagnostic = string.Join(
            Environment.NewLine,
            cudaDiagnostics.Append("CPU Sherpa runtime could not be loaded."));
        return false;
    }

    private static bool TryLoadCudaRuntime(string directory, out string diagnostic)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            diagnostic = $"CUDA bundle unavailable at {directory}: missing {ManifestFileName}.";
            return false;
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            var runtimeVersion = root.TryGetProperty("runtimeVersion", out var runtimeVersionElement)
                ? runtimeVersionElement.GetString()
                : null;
            if (!string.Equals(runtimeVersion, ExpectedRuntimeVersion, StringComparison.Ordinal))
            {
                diagnostic =
                    $"CUDA bundle at {directory} has runtime version '{runtimeVersion ?? "unknown"}'; expected {ExpectedRuntimeVersion}.";
                return false;
            }

            var managedVersion = typeof(OfflineRecognizer).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            if (!managedVersion.StartsWith(ExpectedRuntimeVersion, StringComparison.Ordinal))
            {
                diagnostic =
                    $"Managed Sherpa version {managedVersion} does not match CUDA runtime {ExpectedRuntimeVersion}.";
                return false;
            }
        }
        catch (Exception exception)
        {
            diagnostic = $"CUDA runtime manifest is invalid at {manifestPath}: {exception.Message}";
            return false;
        }

        var missingRuntimeFiles = CudaRuntimeFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        if (missingRuntimeFiles.Length > 0)
        {
            diagnostic =
                $"CUDA bundle incomplete at {directory}: missing {string.Join(", ", missingRuntimeFiles)}.";
            return false;
        }

        var dependencyDirectories = NativeDependencyDirectories(directory).ToArray();
        var resolvedDependencies = NvidiaDependencyFiles
            .Select(file => new
            {
                File = file,
                Path = ResolveFile(file, dependencyDirectories)
            })
            .ToArray();
        var missingDependencies = resolvedDependencies
            .Where(item => item.Path is null)
            .Select(item => item.File)
            .ToArray();
        if (missingDependencies.Length > 0)
        {
            diagnostic = string.Join(
                Environment.NewLine,
                $"CUDA bundle found at {directory}, but NVIDIA dependencies are incomplete.",
                $"Missing: {string.Join(", ", missingDependencies)}.",
                $"Searched: {string.Join("; ", dependencyDirectories)}.");
            return false;
        }

        var searchDirectories = resolvedDependencies
            .Select(item => Path.GetDirectoryName(item.Path!))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Append(directory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!ConfigureTrustedDllSearchDirectories(searchDirectories))
        {
            diagnostic = "Windows could not configure the trusted CUDA dependency search directories.";
            return false;
        }

        // Do not preload cuDNN's component DLLs one-by-one. cuDNN 9 loads several
        // of them dynamically and owns their initialization order. Making every
        // dependency directory visible before loading the CUDA execution provider
        // matches ONNX Runtime's supported Windows loading path.
        if (!TryLoadPath(Path.Combine(directory, "onnxruntime.dll"), out _) ||
            !TryLoadPath(Path.Combine(directory, "onnxruntime_providers_shared.dll"), out _) ||
            !TryLoadPath(Path.Combine(directory, "sherpa-onnx-c-api.dll"), out var sherpaHandle))
        {
            diagnostic =
                $"CUDA Sherpa/ONNX Runtime libraries could not be loaded from {directory}.";
            return false;
        }

        // ONNX Runtime loads the CUDA execution-provider DLL when a CUDA session is
        // created. Loading that provider as an arbitrary standalone module is not a
        // valid capability test and can fail even though an actual CUDA recognizer
        // initializes successfully.
        _sherpaHandle = sherpaHandle;
        RegisterDllImportResolver();
        diagnostic = string.Join(
            Environment.NewLine,
            $"CUDA-capable Sherpa runtime {ExpectedRuntimeVersion} loaded from {directory}.",
            $"NVIDIA dependency directories: {string.Join("; ", searchDirectories)}.",
            "CUDA 12.x and cuDNN 9.x dependency checks passed.");
        return true;
    }

    private static string BuildCpuDiagnostic(IEnumerable<string> cudaDiagnostics, string cpuStatus)
    {
        return string.Join(
            Environment.NewLine,
            cudaDiagnostics.Append(cpuStatus));
    }

    private static IEnumerable<string> CudaBundleDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("MUESLI_SHERPA_CUDA_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Path.GetFullPath(configured);
        }

        yield return Path.Combine(AppContext.BaseDirectory, "native-sherpa-cuda");
    }

    private static IEnumerable<string> NativeDependencyDirectories(string cudaBundleDirectory)
    {
        yield return cudaBundleDirectory;
        yield return GpuDependencyCacheDirectory;

        foreach (var variableName in new[]
                 {
                     "MUESLI_CUDA_PATH",
                     "MUESLI_CUDNN_PATH",
                     "CUDA_PATH",
                     "CUDNN_PATH"
                 })
        {
            var configured = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            yield return Path.GetFullPath(configured);
            yield return Path.Combine(Path.GetFullPath(configured), "bin");
        }

    }

    private static IEnumerable<string> CpuRuntimeDirectories()
    {
        var configured = Environment.GetEnvironmentVariable("MUESLI_SHERPA_CPU_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return Path.GetFullPath(configured);
        }

        var baseDirectory = AppContext.BaseDirectory;
        yield return baseDirectory;

        var runtimeId = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            yield return Path.Combine(baseDirectory, "runtimes", runtimeId, "native");
        }
    }

    private static string? ResolveFile(string fileName, IEnumerable<string> directories)
    {
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // Invalid PATH entries are ignored and included in the final diagnostic.
            }
        }

        return null;
    }

    private static bool ConfigureTrustedDllSearchDirectories(IEnumerable<string> directories)
    {
        const uint loadLibrarySearchDefaultDirs = 0x00001000;
        if (!SetDefaultDllDirectories(loadLibrarySearchDefaultDirs))
        {
            return false;
        }

        foreach (var directory in directories
                     .Where(Directory.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cookie = AddDllDirectory(directory);
            if (cookie == IntPtr.Zero)
            {
                return false;
            }

            DllDirectoryCookies.Add(cookie);
        }

        return DllDirectoryCookies.Count > 0;
    }

    private static void RegisterDllImportResolver()
    {
        if (_resolverRegistered || _sherpaHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(OfflineRecognizer).Assembly,
                ResolveSherpaImport);
            _resolverRegistered = true;
        }
        catch (InvalidOperationException)
        {
            // A resolver may already be registered by the package. The loaded module
            // still wins normal Windows basename resolution.
            _resolverRegistered = true;
        }
    }

    private static IntPtr ResolveSherpaImport(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        return libraryName.Contains("sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase)
            ? _sherpaHandle
            : IntPtr.Zero;
    }

    private static void TryLoadDependency(string path)
    {
        if (File.Exists(path))
        {
            TryLoadPath(path, out _);
        }
    }

    private static bool TryLoadPath(string path, out IntPtr handle)
    {
        if (!File.Exists(path))
        {
            handle = IntPtr.Zero;
            return false;
        }

        try
        {
            return NativeLibrary.TryLoad(path, out handle);
        }
        catch
        {
            handle = IntPtr.Zero;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
