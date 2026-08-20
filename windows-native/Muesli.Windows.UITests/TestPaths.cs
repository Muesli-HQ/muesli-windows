namespace Muesli.Windows.UITests;

internal static class TestPaths
{
    public static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows", "Muesli.Windows.csproj")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Repository root was not found from the UI test output directory.");
        }
    }

    public static string MuesliExecutable
    {
        get
        {
            var configuration = GuessConfiguration();
            var path = Path.Combine(
                RepositoryRoot,
                "windows-native",
                "Muesli.Windows",
                "bin",
                configuration,
                "net10.0-windows",
                "Muesli.exe");
            if (File.Exists(path))
                return path;

            throw new FileNotFoundException(
                $"Built Muesli.exe was not found at {path}. Build windows-native/Muesli.Windows/Muesli.Windows.csproj first.");
        }
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
