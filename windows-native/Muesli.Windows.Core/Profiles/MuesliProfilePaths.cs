namespace Muesli.Windows.Core.Profiles;

public interface IMuesliProfilePaths
{
    string RootDirectory { get; }
    string DataDirectory { get; }
    string CaptureDirectory { get; }
    string LogDirectory { get; }
    string SettingsPath { get; }
    string OnboardingProgressPath { get; }
}

public sealed class MuesliProfilePaths : IMuesliProfilePaths
{
    private MuesliProfilePaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        DataDirectory = Path.Combine(RootDirectory, "data");
        CaptureDirectory = Path.Combine(RootDirectory, "captures");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        SettingsPath = Path.Combine(RootDirectory, "windows-settings.json");
        OnboardingProgressPath = Path.Combine(RootDirectory, "onboarding-progress.json");
    }

    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public string CaptureDirectory { get; }
    public string LogDirectory { get; }
    public string SettingsPath { get; }
    public string OnboardingProgressPath { get; }

    public static MuesliProfilePaths Production() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli"));

    /// <summary>
    /// Resolves the profile selected for this process. WinUI preview and qualification runs set
    /// MUESLI_PROFILE_ROOT to an isolated directory; an unset value preserves the production
    /// %APPDATA%\muesli location used by the WPF fallback. Packaged launches activated by AUMID
    /// cannot inherit that variable, so the same isolated root is also accepted as
    /// <c>--profile-root &lt;path&gt;</c> (or <c>--profile-root=&lt;path&gt;</c>) on the command line.
    /// </summary>
    public static MuesliProfilePaths Current()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("MUESLI_PROFILE_ROOT");
        if (string.IsNullOrWhiteSpace(overrideRoot))
            overrideRoot = ResolveCommandLineProfileRoot();
        return string.IsNullOrWhiteSpace(overrideRoot)
            ? Production()
            : Isolated(overrideRoot);
    }

    private static string? ResolveCommandLineProfileRoot()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            const string prefix = "--profile-root=";
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return argument[prefix.Length..];
            if (string.Equals(argument, "--profile-root", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length)
                return arguments[index + 1];
        }

        return null;
    }

    public static MuesliProfilePaths Isolated(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var fullPath = Path.GetFullPath(rootDirectory);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar),
                pathRoot?.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An isolated profile cannot use a drive root.", nameof(rootDirectory));
        }

        return new MuesliProfilePaths(fullPath);
    }
}

public interface IMuesliProfileLease : IDisposable
{
    string RootDirectory { get; }
}

public interface IMuesliProfileLeaseFactory
{
    IMuesliProfileLease? TryAcquire(IMuesliProfilePaths paths, TimeSpan timeout);
}
