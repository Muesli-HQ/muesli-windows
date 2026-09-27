using System.IO;

namespace Muesli.Windows.Services;

internal static class ModelSetupArtifactCleaner
{
    public static void Cleanup(string cacheDirectory, string artifactPrefix)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        var root = Path.GetFullPath(cacheDirectory);
        var expectedPrefix = $".{artifactPrefix}.";
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)))
            {
                TryDelete(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Model setup never loads staging directories. A locked artifact can be
                // retried on the next setup attempt without affecting the verified cache.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a locked artifact can be retried on the next setup attempt.
        }
    }
}
