namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// L27 cutover surface. The production composition root calls this once during
/// FeatureServiceScope construction when the flag is enabled, before FeatureRuntime loads history.
/// Do not invoke it from FeatureRuntime itself.
/// </summary>
/// <remarks>
/// The composition root replaces the bare <c>new AppDataStore()</c> history path with a JSON
/// adapter when the gate is off, and calls <see cref="EnsureMigrated"/> before constructing the
/// <see cref="SqliteLibraryHistoryAdapter"/> when the gate is enabled.
/// <see cref="PersistenceCutover.Rollback"/> is only a pre-first-write JSON rollback. Once the
/// adapter records a post-cutover write, recovery must use an explicit SQLite backup/restore.
/// </remarks>
public interface IPersistenceCutover
{
    JsonToSqliteMigrationResult EnsureMigrated();

    void Rollback(JsonToSqliteMigrationResult result);
}
