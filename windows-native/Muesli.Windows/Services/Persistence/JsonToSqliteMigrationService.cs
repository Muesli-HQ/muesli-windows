using System.IO;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public enum JsonMigrationOutcome
{
    /// <summary>The history was imported by this run.</summary>
    Migrated,

    /// <summary>An earlier run already imported exactly this history; nothing was written.</summary>
    AlreadyCurrent,

    /// <summary>There was no JSON history to import.</summary>
    NothingToMigrate,

    /// <summary>Nothing was imported and the database is as it was before the attempt.</summary>
    Failed
}

public sealed record MigrationCounts(int Dictations, int Meetings, int Folders, int Templates)
{
    public static MigrationCounts Empty { get; } = new(0, 0, 0, 0);

    public int Total => Dictations + Meetings + Folders + Templates;
}

public sealed record JsonMigrationOptions
{
    /// <summary>
    /// Imports whatever could be read when a source file is unreadable. Off by default: a partial
    /// import that looks successful is worse than a refusal the user can act on.
    /// </summary>
    public bool ContinueOnCorruptSource { get; init; }

    /// <summary>Copies the pre-migration database aside so the import can be undone.</summary>
    public bool RetainBackup { get; init; } = true;

    /// <summary>Overrides where that copy is written.</summary>
    public string? BackupPath { get; init; }
}

public sealed record JsonToSqliteMigrationResult
{
    public JsonMigrationOutcome Outcome { get; init; }
    public string DatabasePath { get; init; } = "";

    /// <summary>The pre-migration copy, or null when there was no database to preserve.</summary>
    public string? BackupPath { get; init; }

    /// <summary>True when this run created the database file, which rollback deletes.</summary>
    public bool CreatedDatabase { get; init; }

    /// <summary>Content hash of the source history; equal fingerprints mean equal histories.</summary>
    public string SourceFingerprint { get; init; } = "";

    public MigrationCounts Counts { get; init; } = MigrationCounts.Empty;

    /// <summary>True when a previous run was found started but never finished.</summary>
    public bool ResumedAfterInterruption { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Failure { get; init; }

    /// <summary>
    /// Dated copy of the JSON history files taken before import. Null when there was nothing to copy.
    /// L27 does not delete this directory; retention is a later point.
    /// </summary>
    public string? JsonSnapshotDirectory { get; init; }

    public bool Succeeded => Outcome is JsonMigrationOutcome.Migrated
        or JsonMigrationOutcome.AlreadyCurrent
        or JsonMigrationOutcome.NothingToMigrate;
}

/// <summary>
/// Moves the JSON history into SQLite.
/// <para>
/// The contract is that a user who runs this can always get back to where they started. The source
/// files are only ever read; the import is a single transaction, so an interruption leaves the
/// database exactly as it was; the pre-migration database is copied aside first; and the import is
/// verified against content hashes of the source before it is allowed to commit. Re-running after a
/// crash is safe because the run is keyed by the fingerprint of the history it imported.
/// </para>
/// <para>
/// Settings and in-progress meeting journals are deliberately not migrated. Those are small,
/// single-writer documents that are rewritten constantly during a recording, and atomic JSON with a
/// backup is a better fit for them than a transaction against a database the app may not have open
/// yet during crash recovery.
/// </para>
/// </summary>
public sealed class JsonToSqliteMigrationService
{
    private readonly string _jsonDirectory;
    private readonly string _databasePath;
    private readonly Action<string>? _report;

    public JsonToSqliteMigrationService(
        string jsonDirectory,
        string? databasePath = null,
        Action<string>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonDirectory);
        _jsonDirectory = Path.GetFullPath(jsonDirectory);
        _databasePath = Path.GetFullPath(databasePath ?? PersistencePaths.DatabasePathFor(jsonDirectory));
        _report = report;
    }

    public string DatabasePath => _databasePath;

    public JsonToSqliteMigrationResult Migrate(JsonMigrationOptions? options = null)
    {
        options ??= new JsonMigrationOptions();
        var snapshot = JsonHistorySnapshotReader.Read(_jsonDirectory);
        var warnings = snapshot.Problems.Select(problem => problem.Message).ToList();
        foreach (var warning in warnings)
        {
            _report?.Invoke(warning);
        }

        if (snapshot.HasBlockingProblems && !options.ContinueOnCorruptSource)
        {
            return new JsonToSqliteMigrationResult
            {
                Outcome = JsonMigrationOutcome.Failed,
                DatabasePath = _databasePath,
                Warnings = warnings,
                Failure = "The JSON history could not be read in full, so nothing was imported. " +
                          string.Join(" ", snapshot.Problems.Where(p => p.Blocking).Select(p => p.Message))
            };
        }

        var plan = MigrationPlan.Build(snapshot, warnings);
        return Migrate(plan, options);
    }

    /// <summary>
    /// Imports an already captured plan. The cutover path calls this overload so the migration does
    /// not read live JSON a second time after it has calculated the authority fingerprint.
    /// </summary>
    internal JsonToSqliteMigrationResult Migrate(
        MigrationPlan plan,
        JsonMigrationOptions? options = null)
    {
        options ??= new JsonMigrationOptions();

        if (plan.Counts.Total == 0)
        {
            return new JsonToSqliteMigrationResult
            {
                Outcome = JsonMigrationOutcome.NothingToMigrate,
                DatabasePath = _databasePath,
                SourceFingerprint = plan.Fingerprint,
                Warnings = plan.Warnings
            };
        }

        var databaseExisted = File.Exists(_databasePath);
        string? backupPath = null;
        var resumed = false;

        using (var database = MuesliDatabase.Open(_databasePath))
        {
            var store = new MuesliPersistenceStore(database, ownsDatabase: false);
            var completed = FindCompletedRun(database, plan.Fingerprint);
            if (completed.Found)
            {
                _report?.Invoke("The SQLite history already matches the JSON history; nothing to do.");
                return new JsonToSqliteMigrationResult
                {
                    Outcome = JsonMigrationOutcome.AlreadyCurrent,
                    DatabasePath = _databasePath,
                    BackupPath = completed.BackupPath,
                    SourceFingerprint = plan.Fingerprint,
                    Counts = plan.Counts,
                    Warnings = plan.Warnings
                };
            }

            resumed = MarkInterruptedRuns(database);
            if (resumed)
            {
                _report?.Invoke("A previous migration was interrupted; it left no partial data and is being retried.");
            }

            if (databaseExisted && options.RetainBackup && HasRows(database))
            {
                backupPath = options.BackupPath ?? BackupPathFor(_databasePath);
                try
                {
                    database.BackupTo(backupPath);
                }
                catch (PersistenceException)
                {
                    return new JsonToSqliteMigrationResult
                    {
                        Outcome = JsonMigrationOutcome.Failed,
                        DatabasePath = _databasePath,
                        BackupPath = backupPath,
                        SourceFingerprint = plan.Fingerprint,
                        Warnings = plan.Warnings,
                        Failure = "The pre-migration SQLite backup could not be retained; nothing was imported."
                    };
                }

                _report?.Invoke($"Kept a pre-migration copy at {Path.GetFileName(backupPath)}.");
            }

            var runId = Guid.NewGuid().ToString("n");
            StartRun(database, runId, plan.Fingerprint, backupPath);

            string? failure = null;
            IPersistenceTransaction? transaction = null;
            try
            {
                transaction = database.BeginTransaction();
                var activeTransaction = transaction!;
                Import(store, plan);
                failure = Verify(store, plan);
                if (failure is null)
                {
                    CompleteRun(database, runId, plan.Counts);
                    activeTransaction.Commit();
                }
                else
                {
                    activeTransaction.Rollback();
                }
            }
            catch (Exception exception) when (exception is SqliteException or PersistenceException)
            {
                failure = exception.Message;
                try
                {
                    transaction?.Rollback();
                }
                catch (Exception)
                {
                    _report?.Invoke("The failed migration transaction could not be reopened; no cutover was activated.");
                }
            }
            finally
            {
                transaction?.Dispose();
            }

            if (failure is not null)
            {
                FailRun(database, runId, failure);
                _report?.Invoke($"The migration was rolled back: {failure}");
                return new JsonToSqliteMigrationResult
                {
                    Outcome = JsonMigrationOutcome.Failed,
                    DatabasePath = _databasePath,
                    BackupPath = backupPath,
                    CreatedDatabase = !databaseExisted,
                    SourceFingerprint = plan.Fingerprint,
                    ResumedAfterInterruption = resumed,
                    Warnings = plan.Warnings,
                    Failure = failure
                };
            }
        }

        _report?.Invoke(
            $"Imported {plan.Counts.Dictations} dictations, {plan.Counts.Meetings} meetings, " +
            $"{plan.Counts.Folders} folders and {plan.Counts.Templates} templates.");

        return new JsonToSqliteMigrationResult
        {
            Outcome = JsonMigrationOutcome.Migrated,
            DatabasePath = _databasePath,
            BackupPath = backupPath,
            CreatedDatabase = !databaseExisted,
            SourceFingerprint = plan.Fingerprint,
            Counts = plan.Counts,
            ResumedAfterInterruption = resumed,
            Warnings = plan.Warnings
        };
    }

    /// <summary>
    /// Undoes a migration by restoring the copy taken before it, or by removing the database this
    /// run created. The database must not be open; <see cref="Migrate"/> closes it before returning.
    /// </summary>
    public void Rollback(JsonToSqliteMigrationResult result)
    {
        if (result.BackupPath is not null && File.Exists(result.BackupPath))
        {
            DeleteSidecars(_databasePath);
            File.Copy(result.BackupPath, _databasePath, overwrite: true);
            _report?.Invoke("Restored the pre-migration database.");
            return;
        }

        if (result.CreatedDatabase)
        {
            DeleteSidecars(_databasePath);
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }

            _report?.Invoke("Removed the database this migration created.");
            return;
        }

        throw new PersistenceException(
            "There is no retained backup for this migration, so it cannot be rolled back.");
    }

    internal static string BackupPathFor(string databasePath) =>
        $"{databasePath}.premigration-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.bak";

    private static void Import(MuesliPersistenceStore store, MigrationPlan plan)
    {
        // Folders first: a meeting or dictation carrying a folder id cannot be written before the
        // folder it points at exists.
        store.Folders.UpsertRange(plan.Folders);
        store.Templates.UpsertRange(plan.Templates);
        store.Dictations.UpsertRange(plan.Dictations);
        store.Meetings.SaveRange(plan.Meetings);
    }

    /// <summary>
    /// Reads the imported rows back and compares them, field by field, with the source. Returns null
    /// when the import is faithful and a description of the difference when it is not.
    /// </summary>
    private static string? Verify(MuesliPersistenceStore store, MigrationPlan plan)
    {
        var folders = store.Folders.List()
            .Where(folder => plan.FolderIds.Contains(folder.Id))
            .ToList();
        if (folders.Count != plan.Folders.Count)
        {
            return Missing("folders", plan.Folders.Count, folders.Count);
        }

        var templates = store.Templates.List()
            .Where(template => plan.TemplateIds.Contains(template.Id))
            .ToList();
        if (templates.Count != plan.Templates.Count)
        {
            return Missing("templates", plan.Templates.Count, templates.Count);
        }

        var dictations = store.Dictations
            .List(new DictationQuery { Limit = int.MaxValue })
            .Where(dictation => plan.DictationIds.Contains(dictation.Id))
            .ToList();
        if (dictations.Count != plan.Dictations.Count)
        {
            return Missing("dictations", plan.Dictations.Count, dictations.Count);
        }

        var meetings = new List<MeetingDetail>(plan.Meetings.Count);
        foreach (var planned in plan.Meetings)
        {
            var detail = store.Meetings.FindDetail(planned.Meeting.Id);
            if (detail is null)
            {
                return $"Meeting '{planned.Meeting.Id}' is missing from the imported database.";
            }

            meetings.Add(detail);
        }

        return CompareDigest("folders", PersistenceDigest.OfFolders(plan.Folders), PersistenceDigest.OfFolders(folders))
            ?? CompareDigest("templates", PersistenceDigest.OfTemplates(plan.Templates), PersistenceDigest.OfTemplates(templates))
            ?? CompareDigest("dictations", PersistenceDigest.OfDictations(plan.Dictations), PersistenceDigest.OfDictations(dictations))
            ?? CompareDigest("meetings", PersistenceDigest.OfMeetings(plan.Meetings), PersistenceDigest.OfMeetings(meetings));
    }

    private static string Missing(string entity, int expected, int actual) =>
        $"Expected {expected} {entity} after the import but found {actual}.";

    private static string? CompareDigest(string entity, string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? null
            : $"The imported {entity} do not match the source content hash ({expected} != {actual}).";

    private static bool HasRows(MuesliDatabase database) =>
        database.Read(connection => Db.Command(
                connection,
                """
                SELECT (SELECT count(*) FROM dictations)
                     + (SELECT count(*) FROM meetings)
                     + (SELECT count(*) FROM folders)
                     + (SELECT count(*) FROM templates);
                """)
            .ScalarInt32()) > 0;

    private static (bool Found, string? BackupPath) FindCompletedRun(
        MuesliDatabase database,
        string fingerprint) =>
        database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                """
                SELECT backup_path
                FROM migration_runs
                WHERE state = 'completed' AND source_fingerprint = $fingerprint
                ORDER BY completed_at_utc DESC
                LIMIT 1;
                """)
                .Bind("$fingerprint", fingerprint);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (true, reader.Text(0))
                : (false, null);
        });

    /// <summary>
    /// Closes out any run that was started but never finished. The import commits as one
    /// transaction, so such a run wrote no data; the row is a marker that the attempt happened.
    /// </summary>
    private static bool MarkInterruptedRuns(MuesliDatabase database) =>
        database.Write(connection => Db.Command(
                connection,
                """
                UPDATE migration_runs
                SET state = 'failed', failure = 'Interrupted before the import committed.'
                WHERE state = 'started';
                """)
            .Execute()) > 0;

    private static void StartRun(MuesliDatabase database, string runId, string fingerprint, string? backupPath) =>
        database.Write(connection => Db.Command(
                connection,
                """
                INSERT INTO migration_runs (id, source_fingerprint, state, started_at_utc, backup_path)
                VALUES ($id, $fingerprint, 'started', $startedAt, $backupPath);
                """)
            .Bind("$id", runId)
            .Bind("$fingerprint", fingerprint)
            .Bind("$startedAt", DateTimeOffset.UtcNow)
            .Bind("$backupPath", backupPath)
            .Execute());

    private static void CompleteRun(MuesliDatabase database, string runId, MigrationCounts counts) =>
        database.Write(connection => Db.Command(
                connection,
                """
                UPDATE migration_runs
                SET state = 'completed',
                    completed_at_utc = $completedAt,
                    dictation_count = $dictations,
                    meeting_count = $meetings,
                    folder_count = $folders,
                    template_count = $templates
                WHERE id = $id;
                """)
            .Bind("$completedAt", DateTimeOffset.UtcNow)
            .Bind("$dictations", counts.Dictations)
            .Bind("$meetings", counts.Meetings)
            .Bind("$folders", counts.Folders)
            .Bind("$templates", counts.Templates)
            .Bind("$id", runId)
            .Execute());

    private static void FailRun(MuesliDatabase database, string runId, string failure) =>
        database.Write(connection => Db.Command(
                connection,
                """
                UPDATE migration_runs
                SET state = 'failed', completed_at_utc = $completedAt, failure = $failure
                WHERE id = $id;
                """)
            .Bind("$completedAt", DateTimeOffset.UtcNow)
            .Bind("$failure", failure)
            .Bind("$id", runId)
            .Execute());

    private static void DeleteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
