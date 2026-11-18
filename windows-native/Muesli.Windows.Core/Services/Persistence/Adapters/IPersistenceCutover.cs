namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Startup initialization and upgrade surface, called before production history is opened.
/// </summary>
/// <remarks>
/// The composition root calls <see cref="EnsureMigrated"/> before constructing the SQLite adapter.
/// <see cref="PersistenceCutover.Rollback"/> is only a pre-first-write JSON rollback. Once the
/// adapter records a post-cutover write, recovery must use an explicit SQLite backup/restore.
/// </remarks>
public interface IPersistenceCutover
{
    JsonToSqliteMigrationResult EnsureMigrated();

    void Rollback(JsonToSqliteMigrationResult result);
}
