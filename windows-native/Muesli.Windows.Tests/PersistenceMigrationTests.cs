using Microsoft.Data.Sqlite;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class PersistenceSchemaTests
{
    [Fact]
    public void AFreshDatabaseIsBroughtToTheCurrentSchemaAndRecordsHowItGotThere()
    {
        using var directory = new TestDirectory();

        using var database = MuesliDatabase.Open(directory.File("history.db"));

        Assert.Equal(PersistenceSchema.CurrentVersion, database.SchemaVersion);
        var history = database.Read(SchemaMigrator.ReadHistory);
        Assert.Equal(
            PersistenceSchema.Migrations.Select(migration => migration.Version),
            history.Select(entry => entry.Version));
    }

    [Fact]
    public void ReopeningAnUpToDateDatabaseAppliesNothing()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.db");
        DateTimeOffset applied;

        using (var database = MuesliDatabase.Open(path))
        {
            applied = database.Read(SchemaMigrator.ReadHistory)[0].AppliedAtUtc;
        }

        using (var database = MuesliDatabase.Open(path))
        {
            Assert.Equal(applied, database.Read(SchemaMigrator.ReadHistory)[0].AppliedAtUtc);
        }
    }

    [Fact]
    public void ADatabaseWrittenByANewerBuildIsRefusedRatherThanDowngraded()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.db");
        using (var store = MuesliPersistenceStore.Open(path))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "written by a newer build" });
            store.Database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {PersistenceSchema.CurrentVersion + 5};";
                command.ExecuteNonQuery();
            });
        }

        var exception = Assert.Throws<PersistenceSchemaException>(() => MuesliDatabase.Open(path));

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationsRunInOrderAndOnlyOnce()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var applied = new List<int>();
        var migrations = new List<SchemaMigration>
        {
            new(2, "second", _ => applied.Add(2)),
            new(1, "first", migrationConnection => PersistenceSchema.Execute(
                migrationConnection,
                "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY NOT NULL, description TEXT NOT NULL, applied_at_utc INTEGER NOT NULL) STRICT;")),
            new(3, "third", _ => applied.Add(3))
        };

        Assert.Equal(3, SchemaMigrator.Migrate(connection, migrations));
        Assert.Equal([2, 3], applied);

        Assert.Equal(3, SchemaMigrator.Migrate(connection, migrations));
        Assert.Equal([2, 3], applied);
        Assert.Equal([1, 2, 3], SchemaMigrator.ReadHistory(connection).Select(entry => entry.Version));
    }

    [Fact]
    public void AFailingMigrationLeavesTheSchemaVersionWhereItWas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var migrations = new List<SchemaMigration>
        {
            new(1, "first", migrationConnection => PersistenceSchema.Execute(
                migrationConnection,
                "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY NOT NULL, description TEXT NOT NULL, applied_at_utc INTEGER NOT NULL) STRICT;" +
                "CREATE TABLE kept (id TEXT PRIMARY KEY NOT NULL) STRICT;")),
            new(2, "explodes", _ => throw new InvalidOperationException("simulated"))
        };

        Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Migrate(connection, migrations));

        Assert.Equal(1, SchemaMigrator.ReadVersion(connection));
        Assert.Equal([1], SchemaMigrator.ReadHistory(connection).Select(entry => entry.Version));
    }

    [Fact]
    public void DuplicateMigrationVersionsAreRejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Throws<PersistenceSchemaException>(() => SchemaMigrator.Migrate(
            connection,
            [new SchemaMigration(1, "a", _ => { }), new SchemaMigration(1, "b", _ => { })]));
    }

    [Fact]
    public void DefaultPathsAreCalculatedWithoutTouchingTheUserProfile()
    {
        var dataDirectory = PersistencePaths.DefaultDataDirectory;
        var existed = Directory.Exists(dataDirectory);

        var databasePath = PersistencePaths.DefaultDatabasePath;

        Assert.Equal(existed, Directory.Exists(dataDirectory));
        Assert.Equal(Path.Combine(dataDirectory, PersistencePaths.DatabaseFileName), databasePath);
        Assert.False(File.Exists(databasePath) && !existed);
    }
}
