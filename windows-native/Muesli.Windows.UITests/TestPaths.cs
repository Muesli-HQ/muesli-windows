namespace Muesli.Windows.UITests;

internal static class TestPaths
{
    public static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Repository root was not found from the UI test output directory.");
        }
    }

    /// <summary>
    /// The WinUI 3 shell project launched packaged through the WinApp CLI
    /// (<c>winapp run --detach --json</c>), the supported packaged host path.
    /// </summary>
    public static string WinUiProjectPath =>
        Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows.WinUI",
            "Muesli.Windows.WinUI.csproj");

    /// <summary>
    /// The unpackaged WinUI shell built by <c>scripts/run-winui-preview.ps1</c>.
    /// Loose MSIX registration needs Developer Mode, which requires elevation, so WinUI
    /// automation drives the unpackaged binary. Package-identity features are qualified
    /// separately against the packaged build.
    /// </summary>
    public static string? TryFindWinUiExecutable()
    {
        var configuration = GuessConfiguration();
        var root = Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows.WinUI",
            "bin",
            "unpackaged",
            configuration);
        if (!Directory.Exists(root))
            return null;

        return Directory
            .EnumerateFiles(root, "Muesli.Windows.WinUI.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string GuessConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.Name is "Debug" or "Release")
                return directory.Name;
            directory = directory.Parent;
        }

        return "Debug";
    }
}
