namespace Muesli.Windows.Services.Persistence;

internal static class DictionaryMigration
{
    public static bool IsComplete(MuesliDatabase database) => database.Read(connection =>
        Db.Command(connection, "SELECT count(*) FROM dictionary_import_state WHERE id = 1;").ScalarInt32() == 1);

    public static DateTimeOffset? CompletedAt(MuesliDatabase database) => database.Read(connection =>
    {
        using var command = Db.Command(connection, "SELECT completed_at_utc FROM dictionary_import_state WHERE id = 1;");
        var value = command.ExecuteScalar();
        return value is long ticks ? (DateTimeOffset?)PersistenceTime.FromStorage(ticks) : null;
    });

    // The import, full-value readback verification and completion marker share one transaction.
    // This also joins the authority transaction when the whole JSON library is being upgraded.
    public static void Import(MuesliDatabase database, IReadOnlyList<DictionaryEntryRecord> entries)
    {
        using var transaction = database.BeginTransaction();
        if (IsComplete(database)) { transaction.Commit(); return; }
        SqliteDictionaryRepository.Validate(entries);
        var repository = new SqliteDictionaryRepository(database);
        if (repository.List().Count != 0)
            throw new PersistenceException("Unmarked SQLite dictionary data exists; automatic import was stopped to preserve it.");
        repository.Replace(entries);
        if (!entries.SequenceEqual(repository.List()))
            throw new PersistenceException("The imported dictionary did not match its source; nothing was activated.");
        database.Write(connection => Db.Command(connection, """
            INSERT INTO dictionary_import_state (id, completed_at_utc, imported_count) VALUES (1, $at, $count);
            """).Bind("$at", DateTimeOffset.UtcNow).Bind("$count", entries.Count).Execute());
        transaction.Commit();
    }
}
