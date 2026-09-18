using SharpCompress.Archives;
using System.IO;

namespace Muesli.Windows.Services;

public static class SafeArchiveExtractor
{
    public static string ResolveEntryDestination(string destinationRoot, string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey) || Path.IsPathRooted(entryKey) || entryKey.Contains(':'))
        {
            throw new InvalidDataException($"Archive entry has an unsafe path: {entryKey}");
        }

        var root = Path.GetFullPath(destinationRoot);
        var normalizedKey = entryKey.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, normalizedKey));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry would escape the model cache: {entryKey}");
        }
        return destination;
    }

    public static void ExtractSafely(
        IArchive archive,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = ResolveEntryDestination(destinationRoot, entry.Key ?? "");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.OpenEntryStream();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
    }
}
