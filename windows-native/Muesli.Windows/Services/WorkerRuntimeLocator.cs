using System.Diagnostics;
using System.IO;

namespace Muesli.Windows.Services;

public static class WorkerRuntimeLocator
{
    public static string LastResolutionSource { get; private set; } = "uninitialized";

    public static string FindPythonExecutable()
    {
        var envPath = Environment.GetEnvironmentVariable("MUESLI_PYTHON");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            LastResolutionSource = "MUESLI_PYTHON";
            return envPath;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        if (File.Exists(bundled))
        {
            LastResolutionSource = "bundled";
            return bundled;
        }

        var appLocalVenv = Path.Combine(AppContext.BaseDirectory, ".venv", "Scripts", "python.exe");
        if (File.Exists(appLocalVenv))
        {
            LastResolutionSource = "app-local-venv";
            return appLocalVenv;
        }

        var parentVenv = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".venv", "Scripts", "python.exe"));
        if (File.Exists(parentVenv))
        {
            LastResolutionSource = "parent-venv";
            return parentVenv;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is not null)
        {
            var devWorkerVenv = Path.Combine(repoRoot, ".venv-worker", "Scripts", "python.exe");
            if (File.Exists(devWorkerVenv))
            {
                LastResolutionSource = "dev-worker-venv";
                return devWorkerVenv;
            }
        }

        var python312 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python312", "python.exe");
        if (File.Exists(python312))
        {
            LastResolutionSource = "user-python312";
            return python312;
        }

        var python311 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Python", "Python311", "python.exe");
        if (File.Exists(python311))
        {
            LastResolutionSource = "user-python311";
            return python311;
        }

        LastResolutionSource = "path";
        return "python";
    }

    public static bool IsBundledPython(string pythonPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return false;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "python", "python.exe");
        try
        {
            return File.Exists(pythonPath) &&
                   string.Equals(Path.GetFullPath(pythonPath), Path.GetFullPath(bundled), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void ApplyWorkerEnv(ProcessStartInfo startInfo, string pythonPath)
    {
        if (!IsBundledPython(pythonPath))
        {
            return;
        }

        var pythonDir = Path.GetDirectoryName(pythonPath)!;
        var sitePackages = Path.Combine(pythonDir, "site-packages-muesli");

        startInfo.Environment["PYTHONHOME"] = pythonDir;
        startInfo.Environment["PYTHONPATH"] = sitePackages;
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";

        var existingPath = startInfo.Environment.TryGetValue("PATH", out var current)
            ? current
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        startInfo.Environment["PATH"] = $"{pythonDir};{Path.Combine(pythonDir, "Scripts")};{existingPath}";
    }

    public static string FindWorkerScript()
    {
        var script = FindWorkerScriptOrNull();
        if (script is not null)
        {
            return script;
        }

        throw new FileNotFoundException("Could not find worker/transcribe_worker.py.");
    }

    public static string? FindWorkerScriptOrNull()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "worker", "transcribe_worker.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        var userProfileCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "projects",
            "muesli",
            "worker",
            "transcribe_worker.py");
        return File.Exists(userProfileCandidate) ? userProfileCandidate : null;
    }

    public static string? FindSetupScriptOrNull()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "setup-worker-runtime.ps1");
        if (File.Exists(direct))
        {
            return direct;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "setup-worker-runtime.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        var userProfileCandidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "projects",
            "muesli",
            "scripts",
            "setup-worker-runtime.ps1");
        return File.Exists(userProfileCandidate) ? userProfileCandidate : null;
    }

    public static bool HasWorkerRequirementsLayout()
    {
        var workerScript = FindWorkerScriptOrNull();
        if (string.IsNullOrWhiteSpace(workerScript))
        {
            return false;
        }

        var workerDirectory = Path.GetDirectoryName(workerScript);
        if (string.IsNullOrWhiteSpace(workerDirectory))
        {
            return false;
        }

        return File.Exists(Path.Combine(workerDirectory, "requirements.txt")) &&
               File.Exists(Path.Combine(workerDirectory, "requirements-diarization.txt")) &&
               File.Exists(Path.Combine(workerDirectory, "requirements-postprocess.txt")) &&
               File.Exists(Path.Combine(workerDirectory, "requirements-parakeet.txt"));
    }

    internal static string? FindRepoRoot(string startingDirectory)
    {
        var directory = new DirectoryInfo(startingDirectory);
        while (directory is not null)
        {
            var workerScript = Path.Combine(directory.FullName, "worker", "transcribe_worker.py");
            var sourceProject = Path.Combine(
                directory.FullName,
                "windows-native",
                "Muesli.Windows",
                "Muesli.Windows.csproj");
            if (File.Exists(workerScript) && File.Exists(sourceProject))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
