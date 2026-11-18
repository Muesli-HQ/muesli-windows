using System.IO;
using System.Text.Json.Nodes;

namespace Muesli.Windows.Services.Persistence;

/// <summary>Migration originals/backups are recovery-only, retained for 30 days or until explicit deletion.</summary>
internal static class LegacyLibraryRecovery
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private static readonly string[] Names =
    [
        JsonHistorySnapshotReader.DictationsFileName, JsonHistorySnapshotReader.MeetingsFileName,
        JsonHistorySnapshotReader.FoldersFileName, JsonHistorySnapshotReader.TemplatesFileName,
        PersistenceCutover.DictionaryFileName, JsonHistorySnapshotReader.LibraryMutationFileName
    ];

    public static void PruneExpired(string directory, DateTimeOffset? activatedAt)
    {
        if (activatedAt is { } at && DateTimeOffset.UtcNow - at >= Retention) Purge(directory);
    }

    /// <summary>Explicit deletion redacts only the requested records; unrelated recovery data survives.</summary>
    public static void RemoveRecords(string directory, string fileName, IReadOnlySet<string> ids)
    {
        if (ids.Count == 0) return;
        var root = Path.GetFullPath(directory);
        ValidatePath(root, root, directoryAllowed: true);
        var directories = new[] { root }.Concat(Directory.EnumerateDirectories(root,
            PersistenceCutover.JsonSnapshotDirectoryPrefix + "*")).ToList();
        var updates = new List<(string Path, JsonNode Value)>();
        foreach (var folder in directories)
        {
            ValidatePath(root, folder, directoryAllowed: true);
            foreach (var path in Directory.EnumerateFiles(folder).Where(path =>
                Path.GetFileName(path) == fileName || Path.GetFileName(path) == fileName + ".bak" ||
                Path.GetFileName(path).StartsWith(fileName + ".corrupt-", StringComparison.Ordinal)))
            {
                ValidatePath(root, path, directoryAllowed: false);
                var node = JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new IOException("A recovery copy could not be redacted safely.");
                var data = node is JsonObject envelope ? envelope["data"] : node;
                if (data is not JsonArray array) throw new IOException("A recovery copy could not be redacted safely.");
                foreach (var row in array.ToList())
                {
                    var id = (row as JsonObject)?.FirstOrDefault(item => item.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                    if (id is null) throw new IOException("A recovery record has no identifier; deletion was stopped.");
                    if (ids.Contains(id)) array.Remove(row);
                }
                updates.Add((path, data!.DeepClone()));
            }
            // Pending folder journals can contain complete meeting rows too.
            if (fileName is JsonHistorySnapshotReader.MeetingsFileName or JsonHistorySnapshotReader.FoldersFileName)
            foreach (var path in new[] { Path.Combine(folder, JsonHistorySnapshotReader.LibraryMutationFileName),
                         Path.Combine(folder, JsonHistorySnapshotReader.LibraryMutationFileName + ".bak") }.Where(File.Exists))
            {
                ValidatePath(root, path, directoryAllowed: false);
                var node = JsonNode.Parse(File.ReadAllBytes(path)) ?? throw new IOException("A recovery journal could not be read.");
                var data = node["data"] ?? node;
                var key = fileName == JsonHistorySnapshotReader.MeetingsFileName ? "meetings" : "folders";
                if (data[key] is not JsonArray array) throw new IOException("A recovery journal could not be redacted safely.");
                foreach (var row in array.ToList())
                    if (row?["id"]?.GetValue<string>() is { } id && ids.Contains(id)) array.Remove(row);
                updates.Add((path, data.DeepClone()));
            }
        }
        // Validate and parse every JSON copy before changing any. PrivacySensitive prevents new .bak copies.
        foreach (var update in updates.OrderByDescending(item => item.Path.EndsWith(".bak", StringComparison.Ordinal)))
            new AtomicJsonFile().Save(update.Path, update.Value, AtomicJsonSaveMode.PrivacySensitive);
        foreach (var backup in Directory.EnumerateFiles(root, PersistencePaths.DatabaseFileName + ".premigration-*.bak"))
        {
            ValidatePath(root, backup, directoryAllowed: false);
            using var store = new MuesliPersistenceStore(MuesliDatabase.OpenExisting(backup));
            using (var transaction = store.BeginTransaction())
            {
                foreach (var id in ids)
                    switch (fileName)
                    {
                        case JsonHistorySnapshotReader.DictationsFileName: store.Dictations.Delete(id); break;
                        case JsonHistorySnapshotReader.MeetingsFileName: store.Meetings.Delete(id); break;
                        case JsonHistorySnapshotReader.FoldersFileName: store.Folders.Delete(id, FolderDeleteMode.Detach); break;
                        case JsonHistorySnapshotReader.TemplatesFileName: store.Templates.Delete(id); break;
                        case PersistenceCutover.DictionaryFileName: store.Dictionary.Delete(id); break;
                    }
                transaction.Commit();
            }
            store.Database.Checkpoint();
        }
    }

    // Remove all migration copies before committing explicit deletion, not a rewritten second
    // history. A locked/unsafe artifact raises an error and the SQLite transaction rolls back.
    public static void Purge(string directory)
    {
        var root = Path.GetFullPath(directory);
        ValidatePath(root, root, directoryAllowed: true);
        var files = new List<string>();
        var snapshots = Directory.EnumerateDirectories(root, PersistenceCutover.JsonSnapshotDirectoryPrefix + "*").ToList();
        foreach (var snapshot in snapshots)
        {
            ValidatePath(root, snapshot, directoryAllowed: true);
            foreach (var entry in Directory.EnumerateFileSystemEntries(snapshot))
            {
                ValidatePath(root, entry, directoryAllowed: false);
                files.Add(entry);
            }
        }
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            if (Names.Any(source => name == source || name == source + ".bak" ||
                    name.StartsWith(source + ".corrupt-", StringComparison.Ordinal) ||
                    name.StartsWith("." + source + ".", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)) ||
                name.StartsWith(PersistencePaths.DatabaseFileName + ".premigration-", StringComparison.Ordinal) && name.EndsWith(".bak", StringComparison.Ordinal))
            {
                ValidatePath(root, file, directoryAllowed: false);
                files.Add(file);
            }
        }
        foreach (var file in files) File.Delete(file);
        foreach (var snapshot in snapshots) Directory.Delete(snapshot, recursive: false);
    }

    private static void ValidatePath(string root, string path, bool directoryAllowed)
    {
        var full = Path.GetFullPath(path);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A migration recovery artifact escaped its profile.");
        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            !directoryAllowed && (attributes & FileAttributes.Directory) != 0)
            throw new IOException("A migration recovery artifact is not a safe regular path.");
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A migration recovery artifact has an unsafe parent.");
    }
}
