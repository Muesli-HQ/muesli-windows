using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public enum PersistenceAuthority
{
    Json,
    Sqlite
}

/// <summary>
/// The durable cutover record. There is exactly one row per database, created by schema migration
/// 2. Keeping this in SQLite means a restart cannot infer authority from a mutable JSON file or a
/// stale migration-run row.
/// </summary>
public sealed record PersistenceCutoverStateRecord
{
    public PersistenceAuthority Authority { get; init; } = PersistenceAuthority.Json;
    public string SourceFingerprint { get; init; } = "";
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public string? RetainedBackupPath { get; init; }
    public DateTimeOffset? FirstPostCutoverWriteAtUtc { get; init; }
    public string? FirstPostCutoverWrite { get; init; }
}

/// <summary>Reads and updates the singleton cutover row without introducing a second store.</summary>
public static class PersistenceCutoverState
{
    public const string TableName = "persistence_cutover_state";

    public static PersistenceCutoverStateRecord Read(MuesliDatabase database) =>
        database.Read(connection =>
        {
            using var command = Db.Command(
                connection,
                """
                SELECT authority, source_fingerprint, activated_at_utc, retained_backup_path,
                       first_post_cutover_write_at_utc, first_post_cutover_write
                FROM persistence_cutover_state
                WHERE id = 1;
                """);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                // A database created by a pre-L27 development build can have schema 1 but no state
                // row if it was copied while migration 2 was being developed. Fail closed rather
                // than treating an unknown authority as JSON or SQLite.
                throw new PersistenceSchemaException(
                    "The SQLite history has no persistence cutover state; it cannot be used safely.");
            }

            return new PersistenceCutoverStateRecord
            {
                Authority = ParseAuthority(reader.GetString(0)),
                SourceFingerprint = reader.GetString(1),
                ActivatedAtUtc = reader.NullableInstant(2),
                RetainedBackupPath = reader.Text(3),
                FirstPostCutoverWriteAtUtc = reader.NullableInstant(4),
                FirstPostCutoverWrite = reader.Text(5)
            };
        });

    /// <summary>
    /// Marks SQLite authoritative only after the caller has verified the imported rows. The
    /// authority row and its metadata commit together as one transaction.
    /// </summary>
    public static void SetSqliteAuthority(
        MuesliDatabase database,
        string sourceFingerprint,
        string? retainedBackupPath,
        DateTimeOffset? activatedAtUtc = null) =>
        database.Write(connection =>
        {
            var state = Read(connection);
            if (state.Authority == PersistenceAuthority.Sqlite)
            {
                return;
            }

            Db.Command(
                    connection,
                    """
                    UPDATE persistence_cutover_state
                    SET authority = 'sqlite',
                        source_fingerprint = $fingerprint,
                        activated_at_utc = $activatedAt,
                        retained_backup_path = $backupPath
                    WHERE id = 1;
                    """)
                .Bind("$fingerprint", sourceFingerprint)
                .Bind("$activatedAt", activatedAtUtc ?? DateTimeOffset.UtcNow)
                .Bind("$backupPath", retainedBackupPath)
                .Execute();
        });

    /// <summary>
    /// Records only the first successful mutation after SQLite becomes authoritative. This is
    /// intentionally idempotent: all adapter writes can call it without racing or overwriting the
    /// evidence needed to decide whether JSON rollback remains safe.
    /// </summary>
    public static void MarkFirstPostCutoverWrite(MuesliDatabase database, string operation) =>
        database.Write(connection =>
        {
            var state = Read(connection);
            if (state.Authority != PersistenceAuthority.Sqlite ||
                state.FirstPostCutoverWriteAtUtc is not null)
            {
                return;
            }

            Db.Command(
                    connection,
                    """
                    UPDATE persistence_cutover_state
                    SET first_post_cutover_write_at_utc = $writtenAt,
                        first_post_cutover_write = $operation
                    WHERE id = 1 AND authority = 'sqlite'
                      AND first_post_cutover_write_at_utc IS NULL;
                    """)
                .Bind("$writtenAt", DateTimeOffset.UtcNow)
                .Bind("$operation", operation)
                .Execute();
        });

    private static PersistenceCutoverStateRecord Read(SqliteConnection connection)
    {
        using var command = Db.Command(
            connection,
            """
            SELECT authority, source_fingerprint, activated_at_utc, retained_backup_path,
                   first_post_cutover_write_at_utc, first_post_cutover_write
            FROM persistence_cutover_state
            WHERE id = 1;
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new PersistenceSchemaException(
                "The SQLite history has no persistence cutover state; it cannot be used safely.");
        }

        return new PersistenceCutoverStateRecord
        {
            Authority = ParseAuthority(reader.GetString(0)),
            SourceFingerprint = reader.GetString(1),
            ActivatedAtUtc = reader.NullableInstant(2),
            RetainedBackupPath = reader.Text(3),
            FirstPostCutoverWriteAtUtc = reader.NullableInstant(4),
            FirstPostCutoverWrite = reader.Text(5)
        };
    }

    private static PersistenceAuthority ParseAuthority(string value) =>
        value switch
        {
            "json" => PersistenceAuthority.Json,
            "sqlite" => PersistenceAuthority.Sqlite,
            _ => throw new PersistenceSchemaException(
                "The SQLite history has an unknown persistence authority; it cannot be used safely.")
        };
}
