using System.IO;

namespace Muesli.Windows.Services;

public static class DictationTemporaryAudioPolicy
{
    public const string SegmentPrefix = "dictation-tmp-";
    public const string MergedPrefix = "dictation-merged-";

    public static CaptureCleanupResult CleanupInterruptedFiles(string captureDirectory)
    {
        var directory = Path.GetFullPath(captureDirectory);
        if (!Directory.Exists(directory))
        {
            return new CaptureCleanupResult(0, []);
        }

        var deleted = 0;
        var failed = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly)
                     .Where(path => IsOwnedTemporaryPath(path, directory)))
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch
            {
                failed.Add(path);
            }
        }
        return new CaptureCleanupResult(deleted, failed);
    }

    public static bool IsOwnedTemporaryPath(string path, string captureDirectory)
    {
        var directory = Path.GetFullPath(captureDirectory);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), directory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(fullPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(fullPath);
        return name.StartsWith(SegmentPrefix, StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(MergedPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
