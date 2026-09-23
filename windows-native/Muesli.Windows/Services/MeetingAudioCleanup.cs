using System.IO;

namespace Muesli.Windows.Services;

public static class MeetingAudioCleanup
{
    public static List<string> DeleteTemporaryFiles(
        IEnumerable<string>? temporaryPaths,
        string? pathsToPreserve)
    {
        var preserved = (pathsToPreserve ?? "")
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        foreach (var path in temporaryPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            var fullPath = Path.GetFullPath(path);
            if (preserved.Contains(fullPath) || !File.Exists(fullPath))
            {
                continue;
            }
            try
            {
                File.Delete(fullPath);
            }
            catch
            {
                failures.Add(fullPath);
            }
        }

        return failures;
    }
}
