namespace Muesli.Windows.Services;

/// <summary>
/// macOS LocalStorageUsage (89b12f20, 73f2342f): a read-only scan of Muesli's profile and model folders.
/// Reparse points (symlinks, junctions) are never followed; a path is counted once even when roots overlap.
/// No file contents are read and no folder is created.
/// </summary>
public sealed class LocalStorageUsage
{
    public enum Category { Models, MeetingAudio, MeetingVideo, Database, WaveformCache, Other }

    public sealed record Entry(string Name, long Bytes);

    public Dictionary<Category, long> Bytes { get; } = [];
    public IReadOnlyList<Entry> Models { get; private set; } = [];
    public int UnavailableLocations { get; private set; }
    public long? FreeBytes { get; private set; }
    public long TotalBytes => Bytes.Values.Sum();

    public long this[Category category] => Bytes.GetValueOrDefault(category);

    /// <summary>The folders the model catalogs download into (env overrides included), then the shared cache root.</summary>
    public static IReadOnlyList<string> DefaultModelRoots() =>
    [
        TranscriptionModelCatalog.ModelCacheDirectory,
        NativeParakeetClient.ModelCacheDirectory,
        StreamingModelCatalog.ModelCacheDirectory,
        CleanupModelCatalog.ModelCacheDirectory,
        NativeDiarizationClient.ModelCacheDirectory,
        Path.Combine(MuesliPathService.UserProfileDirectory, ".cache", "muesli"),
    ];

    /// <summary>Model roots first, so a model folder inside a broader cache root keeps its own name.</summary>
    public static LocalStorageUsage Scan(string profileRoot, IEnumerable<string> modelRoots, CancellationToken cancellationToken = default)
    {
        var result = new LocalStorageUsage();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, isModel) in modelRoots.Select(root => (root, true)).Append((profileRoot, false)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full) || new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            var pending = new Stack<string>([full]);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try { children = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.UnavailableLocations++;
                    continue;
                }
                foreach (var child in children)
                {
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (child is DirectoryInfo) { pending.Push(child.FullName); continue; }
                    if (child is not FileInfo file || !seen.Add(file.FullName)) continue;
                    long size;
                    try { size = file.Length; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        result.UnavailableLocations++;
                        continue;
                    }
                    var relative = Path.GetRelativePath(full, file.FullName).Replace('\\', '/');
                    var category = isModel ? Category.Models : CategoryFor(relative);
                    result.Bytes[category] = result[category] + size;
                    if (category == Category.Models)
                    {
                        var name = isModel ? relative.Split('/')[0] : relative.Split('/').ElementAtOrDefault(1) ?? file.Name;
                        modelSizes[name] = modelSizes.GetValueOrDefault(name) + size;
                    }
                }
            }
        }
        result.Models = modelSizes.Select(pair => new Entry(pair.Key, pair.Value)).OrderByDescending(entry => entry.Bytes).ToList();
        try { result.FreeBytes = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(profileRoot))!).AvailableFreeSpace; }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>The Windows profile layout (macOS <c>category(for:)</c>), relative to <c>%APPDATA%\muesli</c>.</summary>
    public static Category CategoryFor(string path)
    {
        if (path.StartsWith("captures/videos/", StringComparison.OrdinalIgnoreCase))
            return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? Category.MeetingVideo : Category.Other;
        if (path.StartsWith("captures/recordings/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("captures/in-progress/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("captures/meeting-", StringComparison.OrdinalIgnoreCase)) return Category.MeetingAudio;
        if (path.StartsWith("streaming-models/", StringComparison.OrdinalIgnoreCase)) return Category.Models;
        var name = path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && !path[5..].Contains('/') ? path[5..] : null;
        if (name is not null && (name.Equals("muesli.db", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("muesli.db-", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return Category.Database;
        if (path.StartsWith("cache/waveforms/", StringComparison.OrdinalIgnoreCase)) return Category.WaveformCache;
        return Category.Other;
    }
}
