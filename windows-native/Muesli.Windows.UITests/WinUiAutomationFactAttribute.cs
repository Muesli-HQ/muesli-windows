namespace Muesli.Windows.UITests;

/// <summary>
/// xUnit fact for the WinUI 3 shell. Adds the packaged-host prerequisite (the WinApp CLI, which
/// builds, registers, and launches the development package) on top of
/// <see cref="UiAutomationEnvironment"/>'s interactive-desktop gate, and names the missing
/// prerequisite in the skip reason instead of reporting a pass that never ran.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WinUiAutomationFactAttribute : FactAttribute
{
    public WinUiAutomationFactAttribute()
    {
        if (UiAutomationEnvironment.SkipReason is { } reason)
        {
            Skip = reason;
            return;
        }

        if (FindOnPath("winapp.exe") is null)
        {
            Skip = "Skipped because the WinApp CLI (winapp.exe) is not on PATH. " +
                   "The WinUI shell is qualified through its supported packaged launch path: " +
                   "winapp run windows-native/Muesli.Windows.WinUI/Muesli.Windows.WinUI.csproj.";
            return;
        }

        if (RequireUnpackagedBuild && TestPaths.TryFindWinUiExecutable() is null)
        {
            Skip = "Skipped because the unpackaged WinUI shell is not built. " +
                   "Run scripts/run-winui-preview.ps1 (or build Muesli.Windows.WinUI.csproj with " +
                   "-p:WindowsPackageType=None -p:BaseOutputPath=bin/unpackaged/) first.";
        }
    }

    /// <summary>
    /// True only for tests that explicitly qualify behavior without package identity
    /// (loose-executable launch, MSIX-unavailable reporting, executable-level redirection).
    /// </summary>
    public bool RequireUnpackagedBuild { get; set; }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
