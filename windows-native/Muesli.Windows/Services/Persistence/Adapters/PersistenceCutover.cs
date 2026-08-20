using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Startup-only JSON → SQLite cutover. Call from <c>FeatureServiceScope.CreateProduction</c> when
/// <see cref="PersistenceCutoverGate.IsEnabled"/> is true — never from FeatureRuntime.
/// </summary>
/// <remarks>
/// The composition root calls <see cref="EnsureMigrated"/> before it opens the SQLite adapter.
/// A failure is surfaced as a redacted startup error and does not fall back to JSON after the
/// enabled cutover was attempted.
/// </remarks>
public sealed class PersistenceCutover : IPersistenceCutover
{
    public const string JsonSnapshotDirectoryPrefix = "json-history-pre-sqlite-";
    public const string DictionaryFileName = "windows-dictionary.json";

    private static readonly string[] HistoryFileNames =
    [
        JsonHistorySnapshotReader.DictationsFileName,
        JsonHistorySnapshotReader.MeetingsFileName,
        JsonHistorySnapshotReader.FoldersFileName,
        JsonHistorySnapshotReader.TemplatesFileName,
        DictionaryFileName
    ];

    private readonly string _jsonDirectory;
    private readonly string _databasePath;
    private readonly Action<string>? _report;
    private readonly PersistenceCutoverOptions _options;
    private readonly JsonToSqliteMigrationService _migration;

    public PersistenceCutover(
        string jsonDirectory,
        string? databasePath = null,
        Action<string>? report = null,
        PersistenceCutoverOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonDirectory);
        _jsonDirectory = Path.GetFullPath(jsonDirectory);
        _databasePath = Path.GetFullPath(databasePath ?? PersistencePaths.DatabasePathFor(jsonDirectory));
        _report = report;
        _options = options ?? new PersistenceCutoverOptions();
        _migration = new JsonToSqliteMigrationService(_jsonDirectory, _databasePath, SafeReport);
    }

    public string JsonDirectory => _jsonDirectory;

    public string DatabasePath => _databasePath;

    public JsonToSqliteMigrationResult EnsureMigrated()
    {
        var databaseExistedBeforeRun = File.Exists(_databasePath);
        if (File.Exists(_databasePath))
        {
            try
            {
                using var existing = MuesliDatabase.Open(_databasePath);
                var existingState = PersistenceCutoverState.Read(existing);
                if (existingState.Authority == PersistenceAuthority.Sqlite)
                {
                    // Once SQLite is authoritative, the retained JSON is a rollback snapshot, not
                    // an input. Do not read it, compare it, re-import it, or fall back to it after a
                    // restart when the snapshot has naturally diverged from live history.
                    return AlreadyAuthoritative(existing, existingState);
                }
            }
            catch (PersistenceSchemaException)
            {
                SafeReport("The SQLite history was written by a newer Muesli build and was not opened.");
                return Failed("The SQLite history was written by a newer Muesli build and was not opened.");
            }
            catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
            {
                SafeReport("The SQLite history could not be opened, so nothing was imported.");
                return Failed("The SQLite history could not be opened, so nothing was imported.");
            }
        }

        JsonHistorySnapshot snapshot;
        try
        {
            snapshot = JsonHistorySnapshotReader.Read(_jsonDirectory);
        }
        catch (InvalidDataException exception)
        {
            return Failed(SafeFailure(exception.Message));
        }

        var warnings = snapshot.Problems
            .Select(problem => SafeFailure(problem.Message))
            .ToList();
        if (snapshot.HasBlockingProblems)
        {
            var blocking = snapshot.Problems.Where(problem => problem.Blocking).Select(problem => problem.Message);
            return Failed(
                SafeFailure(
                    "The JSON history could not be read in full, so nothing was imported. " +
                    string.Join(" ", blocking)),
                warnings: warnings);
        }

        var plan = MigrationPlan.Build(snapshot, warnings);

        var snapshotDirectory = SnapshotJsonHistory();
        if (_options.FailAfterJsonSnapshot)
        {
            return new JsonToSqliteMigrationResult
            {
                Outcome = JsonMigrationOutcome.Failed,
                DatabasePath = _databasePath,
                SourceFingerprint = plan.Fingerprint,
                Counts = plan.Counts,
                Warnings = plan.Warnings,
                JsonSnapshotDirectory = snapshotDirectory,
                Failure = "Cutover was stopped after the JSON snapshot and before import."
            };
        }

        JsonToSqliteMigrationResult migrated;
        try
        {
            migrated = _migration.Migrate();
        }
        catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
        {
            if (!databaseExistedBeforeRun)
            {
                DeleteDatabaseAndSidecars();
            }

            return Failed(
                "The JSON history was not imported because SQLite could not complete the cutover.",
                warnings: plan.Warnings.ToList()) with
            {
                SourceFingerprint = plan.Fingerprint,
                Counts = plan.Counts,
                JsonSnapshotDirectory = snapshotDirectory
            };
        }

        if (!migrated.Succeeded)
        {
            if (!databaseExistedBeforeRun && migrated.CreatedDatabase)
            {
                DeleteDatabaseAndSidecars();
            }

            return migrated with
            {
                JsonSnapshotDirectory = snapshotDirectory ?? migrated.JsonSnapshotDirectory,
                Warnings = migrated.Warnings.Select(SafeFailure).ToList(),
                Failure = SafeFailure(migrated.Failure ?? "The JSON history was not imported.")
            };
        }

        // JsonToSqliteMigrationService verifies all normalized rows before returning. The state
        // transition is the next transaction, so a crash before this point leaves authority at
        // JSON and the next startup can safely retry against the retained source.
        try
        {
            using var database = MuesliDatabase.Open(_databasePath);
            var state = PersistenceCutoverState.Read(database);
            if (state.Authority == PersistenceAuthority.Sqlite)
            {
                return AlreadyAuthoritative(database, state);
            }

            PersistenceCutoverState.SetSqliteAuthority(
                database,
                plan.Fingerprint,
                migrated.BackupPath);
        }
        catch (Exception exception) when (exception is PersistenceException or SqliteException or IOException)
        {
            if (!databaseExistedBeforeRun)
            {
                DeleteDatabaseAndSidecars();
            }

            return Failed(
                "The JSON history was imported but SQLite authority could not be committed; the cutover was not activated.",
                warnings: plan.Warnings.ToList()) with
            {
                SourceFingerprint = plan.Fingerprint,
                Counts = plan.Counts,
                JsonSnapshotDirectory = snapshotDirectory
            };
        }

        return migrated with
        {
            Outcome = JsonMigrationOutcome.Migrated,
            CreatedDatabase = migrated.CreatedDatabase || !databaseExistedBeforeRun,
            SourceFingerprint = plan.Fingerprint,
            Counts = plan.Counts,
            Warnings = migrated.Warnings.Select(SafeFailure).ToList(),
            JsonSnapshotDirectory = snapshotDirectory ?? migrated.JsonSnapshotDirectory
        };
    }

    public void Rollback(JsonToSqliteMigrationResult result)
    {
        if (result.Outcome != JsonMigrationOutcome.Migrated)
        {
            SafeReport("Rollback left JSON and the database as they were after the failed or no-op cutover.");
            return;
        }

        if (File.Exists(_databasePath))
        {
            using var database = MuesliDatabase.Open(_databasePath);
            var state = PersistenceCutoverState.Read(database);
            if (state.Authority == PersistenceAuthority.Sqlite &&
                state.FirstPostCutoverWriteAtUtc is not null)
            {
                throw new PersistenceException(
                    "JSON rollback is unavailable after the first SQLite write; restore an explicit SQLite backup instead.");
            }
        }

        _migration.Rollback(result);
        SafeReport("Rolled back SQLite; JSON history files and the pre-sqlite snapshot were left in place.");
    }

    private JsonToSqliteMigrationResult Failed(string failure, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Outcome = JsonMigrationOutcome.Failed,
            DatabasePath = _databasePath,
            Failure = failure,
            Warnings = warnings ?? []
        };

    private string? SnapshotJsonHistory()
    {
        var sources = HistoryFileNames
            .SelectMany(name => new[]
            {
                Path.Combine(_jsonDirectory, name),
                Path.Combine(_jsonDirectory, name + ".bak")
            })
            .Where(File.Exists)
            .ToList();
        if (sources.Count == 0)
        {
            return ExistingSnapshotDirectory();
        }

        var directory = Path.Combine(
            _jsonDirectory,
            $"{JsonSnapshotDirectoryPrefix}{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z");
        Directory.CreateDirectory(directory);
        foreach (var source in sources)
        {
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: false);
        }

        SafeReport($"Kept a JSON history snapshot as {Path.GetFileName(directory)}.");
        return directory;
    }

    private string? ExistingSnapshotDirectory()
    {
        if (!Directory.Exists(_jsonDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(_jsonDirectory, JsonSnapshotDirectoryPrefix + "*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .LastOrDefault();
    }

    private JsonToSqliteMigrationResult AlreadyAuthoritative(
        MuesliDatabase database,
        PersistenceCutoverStateRecord state)
    {
        var counts = database.Read(connection => new MigrationCounts(
            Db.Command(connection, "SELECT count(*) FROM dictations;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM meetings;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM folders;").ScalarInt32(),
            Db.Command(connection, "SELECT count(*) FROM templates;").ScalarInt32()));
        return new JsonToSqliteMigrationResult
        {
            Outcome = JsonMigrationOutcome.AlreadyCurrent,
            DatabasePath = _databasePath,
            BackupPath = state.RetainedBackupPath,
            SourceFingerprint = state.SourceFingerprint,
            Counts = counts,
            JsonSnapshotDirectory = ExistingSnapshotDirectory()
        };
    }

    private void DeleteDatabaseAndSidecars()
    {
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private void SafeReport(string message) => _report?.Invoke(Sanitize(message));

    private static string SafeFailure(string message) => Sanitize(message);

    /// <summary>
    /// Cutover messages may name files and opaque ids. They must not echo transcript, title, or path
    /// payloads from the history itself.
    /// </summary>
    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        // Parser and provider messages occasionally quote the offending value. Keep the useful
        // diagnostic category but never echo a title, transcript, path, or opaque record id.
        return Regex.Replace(message, "(?<=['\\\"]).*?(?=['\\\"])", "[redacted]", RegexOptions.CultureInvariant);
    }
}
