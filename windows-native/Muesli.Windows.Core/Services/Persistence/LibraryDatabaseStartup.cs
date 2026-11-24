using System.IO;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public enum LibraryStartupOutcome
{
    /// <summary>The database exists (or was just created for a new profile) and can be opened.</summary>
    Ready,

    /// <summary>History from a JSON-era development build is present and there is no database.</summary>
    LegacyHistoryNeedsImport,

    /// <summary>There is no database, but backups show there used to be one.</summary>
    DatabaseMissing,

    /// <summary>The database file exists and SQLite cannot open it.</summary>
    DatabaseUnreadable,

    /// <summary>The database has a newer schema than this build understands.</summary>
    DatabaseFromNewerBuild,

    /// <summary>The database was written by an unreleased development build.</summary>
    DatabaseFromPreReleaseBuild
}

public sealed record LibraryStartupResult
{
    public LibraryStartupOutcome Outcome { get; init; }

    /// <summary>What happened and what the user can do about it. Empty when ready.</summary>
    public string Message { get; init; } = "";

    public string DatabasePath { get; init; } = "";

    /// <summary>True when this call created the database for a new profile.</summary>
    public bool CreatedDatabase { get; init; }

    /// <summary>The newest backup, when one exists.</summary>
    public LibraryBackupInfo? LatestBackup { get; init; }

    public bool IsReady => Outcome == LibraryStartupOutcome.Ready;

    /// <summary>True when restoring <see cref="LatestBackup"/> is a sensible way out.</summary>
    public bool CanRestoreBackup => LatestBackup is not null &&
        Outcome is LibraryStartupOutcome.DatabaseMissing or LibraryStartupOutcome.DatabaseUnreadable;
}

/// <summary>Thrown by a host that cannot open the library; carries the reason to show the user.</summary>
public sealed class LibraryUnavailableException(LibraryStartupResult result) : PersistenceException(result.Message)
{
    public LibraryStartupResult Result { get; } = result;
}

/// <summary>
/// Decides, once at launch, whether the library database can be used.
/// <para>
/// SQLite is the only store for history. This check never reads, copies, rewrites or deletes
/// anything: it either reports the database ready, creates an empty one for a profile that has
/// never had history, or explains why it will not open one. It never opens an empty library over
/// history it can see, because that would look like data loss.
/// </para>
/// </summary>
public static class LibraryDatabaseStartup
{
    /// <summary>The JSON files earlier development builds kept history in.</summary>
    public static IReadOnlyList<string> LegacyHistoryFileNames { get; } =
    [
        JsonHistorySnapshotReader.DictationsFileName,
        JsonHistorySnapshotReader.MeetingsFileName,
        JsonHistorySnapshotReader.FoldersFileName,
        JsonHistorySnapshotReader.TemplatesFileName,
        JsonHistorySnapshotReader.DictionaryFileName,
        JsonHistorySnapshotReader.LibraryMutationFileName
    ];

    public static LibraryStartupResult Prepare(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        var databasePath = PersistencePaths.DatabasePathFor(directory);
        var latestBackup = LibraryDatabaseBackup.List(directory).FirstOrDefault();

        if (!File.Exists(databasePath))
        {
            // A write-ahead log without its database is what is left when muesli.db was moved or
            // lost after a crash. The log can hold the most recent history, and creating a new
            // database beside it would make SQLite discard it.
            var orphans = new[] { "-wal", "-shm" }
                .Select(suffix => PersistencePaths.DatabaseFileName + suffix)
                .Where(name => File.Exists(Path.Combine(directory, name)))
                .ToList();
            if (orphans.Count > 0)
            {
                return new LibraryStartupResult
                {
                    Outcome = LibraryStartupOutcome.DatabaseMissing,
                    DatabasePath = databasePath,
                    LatestBackup = latestBackup,
                    Message =
                        $"The Muesli library database is missing from {directory}, but part of it " +
                        $"({string.Join(", ", orphans)}) is still there and may hold recent history. " +
                        "Nothing was changed. If muesli.db was moved, put it back beside those files. " +
                        (latestBackup is not null
                            ? $"Otherwise a backup from {Describe(latestBackup)} is available."
                            : "To start with an empty library instead, move those files out of the folder.")
                };
            }

            if (latestBackup is not null)
            {
                return new LibraryStartupResult
                {
                    Outcome = LibraryStartupOutcome.DatabaseMissing,
                    DatabasePath = databasePath,
                    LatestBackup = latestBackup,
                    Message =
                        $"The Muesli library database is missing from {directory}. " +
                        $"A backup from {Describe(latestBackup)} is available."
                };
            }

            var legacy = FindLegacyHistoryFiles(directory);
            if (legacy.Count > 0)
            {
                return new LibraryStartupResult
                {
                    Outcome = LibraryStartupOutcome.LegacyHistoryNeedsImport,
                    DatabasePath = databasePath,
                    Message =
                        $"Muesli found history from an earlier development build in {directory} " +
                        $"({string.Join(", ", legacy)}) and no library database. Nothing was changed. " +
                        "To keep that history, close Muesli and run scripts\\import-legacy-history.ps1 " +
                        $"-ProfileRoot \"{Path.GetDirectoryName(directory)}\" from the repository. " +
                        "To start with an empty library instead, move those files out of the folder."
                };
            }

            using (MuesliDatabase.Open(databasePath))
            {
            }

            return new LibraryStartupResult
            {
                Outcome = LibraryStartupOutcome.Ready,
                DatabasePath = databasePath,
                CreatedDatabase = true
            };
        }

        try
        {
            using (MuesliDatabase.OpenExisting(databasePath))
            {
            }

            return new LibraryStartupResult
            {
                Outcome = LibraryStartupOutcome.Ready,
                DatabasePath = databasePath,
                LatestBackup = latestBackup
            };
        }
        catch (UnsupportedDatabaseException)
        {
            return new LibraryStartupResult
            {
                Outcome = LibraryStartupOutcome.DatabaseFromPreReleaseBuild,
                DatabasePath = databasePath,
                Message =
                    $"The library database in {directory} was written by a pre-release Muesli build and " +
                    "this build cannot open it. It was not changed. Move muesli.db (and muesli.db-wal, " +
                    "muesli.db-shm if present) out of that folder, then start Muesli again."
            };
        }
        catch (PersistenceSchemaException)
        {
            return new LibraryStartupResult
            {
                Outcome = LibraryStartupOutcome.DatabaseFromNewerBuild,
                DatabasePath = databasePath,
                Message =
                    $"The library database in {directory} was written by a newer version of Muesli. " +
                    "It was not changed. Update Muesli to open it."
            };
        }
        catch (Exception exception) when (exception is SqliteException or PersistenceException or IOException or UnauthorizedAccessException)
        {
            return new LibraryStartupResult
            {
                Outcome = LibraryStartupOutcome.DatabaseUnreadable,
                DatabasePath = databasePath,
                LatestBackup = latestBackup,
                Message =
                    $"The Muesli library database in {directory} could not be opened and may be damaged. " +
                    "It was not changed. " +
                    (latestBackup is not null
                        ? $"A backup from {Describe(latestBackup)} is available."
                        : "No backup is available. To start with an empty library, move muesli.db out of that folder.")
            };
        }
    }

    /// <summary>Names of the legacy history files present, including backup and quarantine copies.</summary>
    public static IReadOnlyList<string> FindLegacyHistoryFiles(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return [];
        var found = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dataDirectory))
        {
            var name = Path.GetFileName(path);
            if (LegacyHistoryFileNames.Any(source =>
                    name.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(source + ".", StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(name);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private static string Describe(LibraryBackupInfo backup) =>
        backup.CreatedAtUtc.ToLocalTime().ToString("f");
}
