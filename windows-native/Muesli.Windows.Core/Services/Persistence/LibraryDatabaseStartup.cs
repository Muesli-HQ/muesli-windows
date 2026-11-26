using System.IO;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public enum LibraryStartupOutcome
{
    /// <summary>The database exists (or was just created for a new profile) and can be opened.</summary>
    Ready,

    /// <summary>There is no database, but parts of one (its write-ahead log) are still there.</summary>
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

    public bool IsReady => Outcome == LibraryStartupOutcome.Ready;
}

/// <summary>Thrown by a host that cannot open the library; carries the reason to show the user.</summary>
public sealed class LibraryUnavailableException(LibraryStartupResult result) : PersistenceException(result.Message)
{
    public LibraryStartupResult Result { get; } = result;
}

/// <summary>
/// Decides, once at launch, whether the library database can be used.
/// <para>
/// SQLite is the only store for history. This check either reports the database ready, creates
/// an empty one for a profile that has none, or explains why it will not open the one it found.
/// It never replaces a database, or the remains of one, with an empty library, and it never
/// modifies a file it refuses. JSON files left by pre-release development builds are not history
/// to this build and are ignored.
/// </para>
/// </summary>
public static class LibraryDatabaseStartup
{
    public static LibraryStartupResult Prepare(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        var databasePath = PersistencePaths.DatabasePathFor(directory);

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
                    Message =
                        $"The Muesli library database is missing from {directory}, but part of it " +
                        $"({string.Join(", ", orphans)}) is still there and may hold recent history. " +
                        "Nothing was changed. If muesli.db was moved, put it back beside those files. " +
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
                DatabasePath = databasePath
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
                Message =
                    $"The Muesli library database in {directory} could not be opened and may be damaged. " +
                    "It was not changed. To start with an empty library, move muesli.db out of that folder."
            };
        }
    }
}
