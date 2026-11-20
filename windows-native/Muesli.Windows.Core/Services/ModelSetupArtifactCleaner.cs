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
            if (Owns(name, expectedPrefix, ".partial") || Owns(name, expectedPrefix, ".download"))
            {
                TryDelete(file);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!Owns(name, expectedPrefix, ".staging"))
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

    /// <summary>
    /// ".{prefix}.{token}{suffix}" with a dot-free token. Without that, cleaning
    /// "sherpa-onnx-whisper-tiny" would delete the in-flight ".sherpa-onnx-whisper-tiny.en.{guid}.download".
    /// </summary>
    internal static bool Owns(string name, string expectedPrefix, string suffix) =>
        name.Length > expectedPrefix.Length + suffix.Length &&
        name.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
        !name.AsSpan(expectedPrefix.Length, name.Length - expectedPrefix.Length - suffix.Length).Contains('.');

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
