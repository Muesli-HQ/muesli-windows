using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Brings a database up to the schema this build expects, one numbered step at a time.
/// <para>
/// Migration is forward-only and refuses to open a database written by a newer build. Downgrading a
/// schema in place would mean deleting columns a newer Muesli is still writing, and a user who tries
/// an older build after a newer one would silently lose that data; failing to open is recoverable,
/// a destructive downgrade is not.
/// </para>
/// </summary>
public static class SchemaMigrator
{
    /// <summary>The schema version recorded in the database file itself.</summary>
    public static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Applies every migration newer than the database's current version, in order. Each step and
    /// its version stamp commit together, so an interrupted upgrade resumes at a step boundary
    /// rather than in the middle of one.
    /// </summary>
    /// <returns>The version the database is at when this returns.</returns>
    public static int Migrate(SqliteConnection connection, IReadOnlyList<SchemaMigration> migrations)
    {
        var ordered = migrations.OrderBy(migration => migration.Version).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].Version == ordered[index - 1].Version)
            {
                throw new PersistenceSchemaException(
                    $"Two migrations share version {ordered[index].Version}.");
            }
        }

        var current = ReadVersion(connection);
        var target = ordered.Count == 0 ? 0 : ordered[^1].Version;
        if (current > target)
        {
            throw new PersistenceSchemaException(
                $"The database is at schema version {current}, which is newer than the version {target} " +
                "this build understands. Update Muesli to open it.");
        }

        foreach (var migration in ordered.Where(migration => migration.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection);
            Record(connection, migration);
            SetVersion(connection, migration.Version);
            transaction.Commit();
            current = migration.Version;
        }

        return current;
    }

    /// <summary>The migrations this database has recorded, oldest first.</summary>
    public static IReadOnlyList<(int Version, string Description, DateTimeOffset AppliedAtUtc)> ReadHistory(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version, description, applied_at_utc FROM schema_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        var history = new List<(int, string, DateTimeOffset)>();
        while (reader.Read())
        {
            history.Add((
                reader.GetInt32(0),
                reader.GetString(1),
                PersistenceTime.FromStorage(reader.GetInt64(2))));
        }

        return history;
    }

    private static void Record(SqliteConnection connection, SchemaMigration migration)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO schema_migrations (version, description, applied_at_utc)
            VALUES ($version, $description, $appliedAt)
            ON CONFLICT (version) DO UPDATE SET
                description = excluded.description,
                applied_at_utc = excluded.applied_at_utc;
            """;
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$description", migration.Description);
        command.Parameters.AddWithValue("$appliedAt", PersistenceTime.ToStorage(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void SetVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();

        // PRAGMA does not take parameters; the value is an int from our own migration list.
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}
