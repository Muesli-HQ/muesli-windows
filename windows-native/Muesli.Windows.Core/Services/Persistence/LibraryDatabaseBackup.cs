using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>One retained copy of the library database.</summary>
public sealed record LibraryBackupInfo(string Path, DateTimeOffset CreatedAtUtc);

/// <summary>
/// Rolling copies of <c>muesli.db</c>, kept in a <c>backups</c> folder beside it.
/// <para>
/// The database is the only copy of the user's history, so one damaged file must not be the end of
/// it. A copy is taken with <c>VACUUM INTO</c>, which produces a complete, consistent database even
/// while the WAL holds uncheckpointed writes. Copies are taken at most once per interval and the
/// oldest are removed, so the folder stays a bounded size.
/// </para>
/// <para>
/// A backup is a full copy of the history. When the user explicitly deletes a dictation or meeting
/// the host calls <see cref="ReplaceAll"/> so no copy keeps the deleted content.
/// </para>
/// </summary>
public static class LibraryDatabaseBackup
{
    public const string DirectoryName = "backups";
    public const int DefaultRetainedCount = 7;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

    private const string FilePrefix = "muesli-";
    private const string FileSuffix = ".db";
    private const string PartialSuffix = ".partial";
    private const string PurgeMarkerFileName = "purge-pending";
    private const string TimestampFormat = "yyyyMMdd-HHmmssfff";

    public static string DirectoryFor(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), DirectoryName);

    /// <summary>Completed backups, newest first. Unfinished and unrelated files are ignored.</summary>
    public static IReadOnlyList<LibraryBackupInfo> List(string dataDirectory)
    {
        var directory = DirectoryFor(dataDirectory);
        if (!Directory.Exists(directory)) return [];
        var backups = new List<LibraryBackupInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileSuffix))
        {
            var name = Path.GetFileName(path);
            var stamp = name[FilePrefix.Length..^FileSuffix.Length];
            if (DateTimeOffset.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
            {
                backups.Add(new LibraryBackupInfo(path, createdAt));
            }
        }

        return backups.OrderByDescending(backup => backup.CreatedAtUtc).ToList();
    }

    /// <summary>
    /// Takes a backup unless the newest one is younger than <paramref name="interval"/>. When a
    /// deletion's purge was recorded but never finished (the app was closed first), it is finished
    /// here instead, whatever the age of the newest backup.
    /// </summary>
    public static LibraryBackupInfo? CreateIfDue(
        MuesliDatabase database,
        string dataDirectory,
        TimeSpan? interval = null,
        int retainedCount = DefaultRetainedCount,
        DateTimeOffset? now = null)
    {
        if (PurgePendingSince(dataDirectory) is not null) return ReplaceAll(database, dataDirectory);
        var moment = now ?? DateTimeOffset.UtcNow;
        var newest = List(dataDirectory).FirstOrDefault();
        if (newest is not null && moment - newest.CreatedAtUtc < (interval ?? DefaultInterval)) return null;
        return Create(database, dataDirectory, retainedCount, moment);
    }

    /// <summary>
    /// Takes a backup now and removes the oldest ones beyond <paramref name="retainedCount"/>.
    /// Returns null, keeping nothing, when a deletion was recorded while the copy was being made:
    /// that copy may still hold the deleted content.
    /// </summary>
    public static LibraryBackupInfo? Create(
        MuesliDatabase database,
        string dataDirectory,
        int retainedCount = DefaultRetainedCount,
        DateTimeOffset? now = null)
    {
        var startedAtUtc = DateTime.UtcNow;
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedCount, 1);
        var directory = DirectoryFor(dataDirectory);
        Directory.CreateDirectory(directory);
        DeletePartials(directory);

        var createdAt = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var path = Path.Combine(
            directory,
            FilePrefix + createdAt.ToString(TimestampFormat, CultureInfo.InvariantCulture) + FileSuffix);
        if (File.Exists(path)) return new LibraryBackupInfo(path, createdAt);

        // A copy interrupted by a crash must never be listed as a backup, so it is written under a
        // name List ignores and only renamed once SQLite has finished it.
        var partial = path + PartialSuffix;
        try
        {
            database.BackupTo(partial);
            if (PurgePendingSince(dataDirectory) is { } deletedAtUtc && deletedAtUtc >= startedAtUtc) return null;
            File.Move(partial, path);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }

        foreach (var expired in List(dataDirectory).Skip(retainedCount))
        {
            File.Delete(expired.Path);
        }

        return new LibraryBackupInfo(path, createdAt);
    }

    /// <summary>
    /// Removes every backup, then takes a fresh one. Called after an explicit deletion so the
    /// deleted text does not survive in an older copy.
    /// </summary>
    public static LibraryBackupInfo? ReplaceAll(MuesliDatabase database, string dataDirectory)
    {
        var pendingSince = PurgePendingSince(dataDirectory);
        DeleteAll(dataDirectory);
        var created = Create(database, dataDirectory);
        // Clear the record only if no further deletion arrived meanwhile; otherwise the next run
        // must purge again.
        if (created is not null && pendingSince is not null && PurgePendingSince(dataDirectory) == pendingSince)
        {
            File.Delete(PurgeMarkerPath(dataDirectory));
        }

        return created;
    }

    /// <summary>
    /// Records, durably and at once, that an explicit deletion happened and every existing backup
    /// must go, then removes the backups it can. The record survives the app being closed or
    /// killed, so the purge is finished on the next launch if it was not finished now.
    /// </summary>
    public static void RecordDeletionAndPurge(string dataDirectory)
    {
        var directory = DirectoryFor(dataDirectory);
        Directory.CreateDirectory(directory);
        var marker = PurgeMarkerPath(dataDirectory);
        using (var stream = new FileStream(marker, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
        {
            stream.WriteByte((byte)'1');
            stream.Flush(flushToDisk: true);
        }

        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
        DeleteAll(dataDirectory);
    }

    /// <summary>When a deletion's backup purge was recorded and has not finished, the time it was recorded.</summary>
    public static DateTime? PurgePendingSince(string dataDirectory)
    {
        var marker = PurgeMarkerPath(dataDirectory);
        return File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : null;
    }

    private static string PurgeMarkerPath(string dataDirectory) =>
        Path.Combine(DirectoryFor(dataDirectory), PurgeMarkerFileName);

    public static int DeleteAll(string dataDirectory)
    {
        var directory = DirectoryFor(dataDirectory);
        if (!Directory.Exists(directory)) return 0;
        var deleted = 0;
        foreach (var backup in List(dataDirectory))
        {
            File.Delete(backup.Path);
            deleted++;
        }

        try
        {
            DeletePartials(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy still being written is locked. It is discarded by Create when it finishes,
            // because the purge record is newer than its start.
        }

        return deleted;
    }

    /// <summary>
    /// Puts the newest backup that passes SQLite's integrity check in place of the live database.
    /// The file being replaced is renamed, never deleted, so nothing is lost if the user wants a
    /// specialist to look at it. Returns null, changing nothing, when no usable backup exists.
    /// The database must not be open.
    /// </summary>
    public static LibraryBackupInfo? RestoreLatest(string dataDirectory, DateTimeOffset? now = null)
    {
        var usable = List(dataDirectory).FirstOrDefault(backup => PassesIntegrityCheck(backup.Path));
        if (usable is null) return null;

        var databasePath = PersistencePaths.DatabasePathFor(dataDirectory);
        var stamp = (now ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = databasePath + suffix;
            if (File.Exists(source)) File.Move(source, $"{databasePath}.replaced-{stamp}{suffix}");
        }

        // Copy under another name and rename, so an interrupted restore never leaves a partial
        // file where the library is expected.
        var staging = $"{databasePath}.restoring-{stamp}";
        try
        {
            File.Copy(usable.Path, staging, overwrite: false);
            File.Move(staging, databasePath);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }

        return usable;
    }

    private static bool PassesIntegrityCheck(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void DeletePartials(string directory)
    {
        foreach (var partial in Directory.EnumerateFiles(directory, FilePrefix + "*" + FileSuffix + PartialSuffix))
        {
            File.Delete(partial);
        }
    }
}
