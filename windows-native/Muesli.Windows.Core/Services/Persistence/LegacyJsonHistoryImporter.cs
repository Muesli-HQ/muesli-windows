using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed record LegacyImportResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = "";
    public string DatabasePath { get; init; } = "";
    public MigrationCounts Counts { get; init; } = MigrationCounts.Empty;
    public int DictionaryEntries { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// One-time import of the JSON history kept by development builds into a new library database.
/// <para>
/// This is a developer tool, run on purpose through the command host. The app never calls it:
/// a launch neither looks for nor converts JSON history. The source files are only read, and are
/// left exactly as they were whether the import succeeds or fails.
/// </para>
/// <para>
/// The import is all or nothing. It is built in a temporary file beside the destination, verified
/// against content hashes of the source, and only then moved into place. It refuses to run when a
/// database already exists, so it can never overwrite or blend into a live library.
/// </para>
/// </summary>
public static class LegacyJsonHistoryImporter
{
    public static LegacyImportResult Import(string dataDirectory, Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var directory = Path.GetFullPath(dataDirectory);
        var databasePath = PersistencePaths.DatabasePathFor(directory);
        if (!Directory.Exists(directory))
        {
            return Failed(databasePath, $"The data directory does not exist: {directory}");
        }

        if (File.Exists(databasePath))
        {
            return Failed(
                databasePath,
                "A library database already exists in this profile. The importer only creates a new " +
                "library and never changes an existing one; nothing was imported.");
        }

        JsonHistorySnapshot snapshot;
        try
        {
            snapshot = JsonHistorySnapshotReader.Read(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Failed(databasePath, $"The JSON history could not be read, so nothing was imported. {exception.Message}");
        }

        var warnings = snapshot.Problems.Where(problem => !problem.Blocking).Select(problem => problem.Message).ToList();
        if (snapshot.HasBlockingProblems)
        {
            return Failed(
                databasePath,
                "The JSON history could not be read in full, so nothing was imported. " +
                string.Join(" ", snapshot.Problems.Where(problem => problem.Blocking).Select(problem => problem.Message)),
                warnings);
        }

        var plan = MigrationPlan.Build(snapshot, warnings);
        var staging = $"{databasePath}.import-{Guid.NewGuid():n}";
        try
        {
            var migrated = new JsonToSqliteMigrationService(directory, staging, report)
                .Migrate(plan, new JsonMigrationOptions { RetainBackup = false });
            if (!migrated.Succeeded)
            {
                return Failed(databasePath, migrated.Failure ?? "The JSON history was not imported.", plan.Warnings);
            }

            // Opening creates the file when the history was empty, so an import of a profile with
            // only a dictionary (or nothing at all) still ends with a usable library.
            using (var database = MuesliDatabase.Open(staging))
            {
                var dictionary = new SqliteDictionaryRepository(database);
                using (var transaction = database.BeginTransaction())
                {
                    dictionary.Replace(snapshot.Dictionary);
                    if (!snapshot.Dictionary.SequenceEqual(dictionary.List()))
                    {
                        return Failed(databasePath, "The imported dictionary did not match its source; nothing was imported.", plan.Warnings);
                    }

                    transaction.Commit();
                }

                // Fold the WAL into the file so the single file moved below is the whole database.
                database.Checkpoint();
            }

            if (File.Exists(databasePath))
            {
                return Failed(databasePath, "A library database appeared while the import was running; nothing was imported.", plan.Warnings);
            }

            File.Move(staging, databasePath);
        }
        catch (Exception exception) when (exception is SqliteException or PersistenceException or IOException or UnauthorizedAccessException)
        {
            return Failed(databasePath, $"The import could not be completed, so nothing was imported. {exception.Message}", plan.Warnings);
        }
        finally
        {
            DeleteStaging(staging);
        }

        var summary =
            $"Imported {plan.Counts.Dictations} dictations, {plan.Counts.Meetings} meetings, " +
            $"{plan.Counts.Folders} folders, {plan.Counts.Templates} templates and " +
            $"{snapshot.Dictionary.Count} dictionary entries. The JSON files were not changed.";
        report?.Invoke(summary);
        return new LegacyImportResult
        {
            Succeeded = true,
            Message = summary,
            DatabasePath = databasePath,
            Counts = plan.Counts,
            DictionaryEntries = snapshot.Dictionary.Count,
            Warnings = plan.Warnings
        };
    }

    private static LegacyImportResult Failed(string databasePath, string message, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Succeeded = false,
            Message = message,
            DatabasePath = databasePath,
            Warnings = warnings ?? []
        };

    private static void DeleteStaging(string staging)
    {
        foreach (var path in new[] { staging, staging + "-wal", staging + "-shm" })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover staging file is inert: it is never opened as the library.
            }
        }
    }
}
