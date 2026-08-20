using System.IO;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Where the persistence layer keeps its files. These are pure path calculations that touch no disk,
/// so naming a location is never the same thing as creating one.
/// </summary>
public static class PersistencePaths
{
    public const string DatabaseFileName = "muesli.db";

    /// <summary>The JSON history directory this store migrates from: %APPDATA%\muesli\data.</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "muesli",
        "data");

    public static string DefaultDatabasePath => DatabasePathFor(DefaultDataDirectory);

    public static string DatabasePathFor(string dataDirectory) =>
        Path.Combine(Path.GetFullPath(dataDirectory), DatabaseFileName);
}

/// <summary>
/// The persistence layer's entry point: one database and the repositories over it.
/// <para>
/// The production composition root opens this store after a verified L27 cutover and hands it
/// to the active SQLite history adapter. The gate remains off by default, so JSON remains
/// authoritative unless the process explicitly opts in.
/// </para>
/// </summary>
public sealed class MuesliPersistenceStore : IDisposable
{
    private readonly bool _ownsDatabase;
    private bool _disposed;

    public MuesliPersistenceStore(MuesliDatabase database, bool ownsDatabase = true)
    {
        Database = database;
        _ownsDatabase = ownsDatabase;
        Dictations = new SqliteDictationRepository(database);
        Meetings = new SqliteMeetingRepository(database);
        Folders = new SqliteFolderRepository(database);
        Templates = new SqliteTemplateRepository(database);
        Search = new SqliteSearchRepository(database);
    }

    public static MuesliPersistenceStore Open(string databasePath) =>
        new(MuesliDatabase.Open(databasePath));

    public static MuesliPersistenceStore OpenInMemory() =>
        new(MuesliDatabase.OpenInMemory());

    public MuesliDatabase Database { get; }

    public IDictationRepository Dictations { get; }

    public IMeetingRepository Meetings { get; }

    public IFolderRepository Folders { get; }

    public ITemplateRepository Templates { get; }

    public ISearchRepository Search { get; }

    public int SchemaVersion => Database.SchemaVersion;

    /// <summary>
    /// Opens a unit of work spanning any number of repositories. Every repository call on this
    /// thread joins it until it is committed or disposed.
    /// </summary>
    public IPersistenceTransaction BeginTransaction() => Database.BeginTransaction();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsDatabase)
        {
            Database.Dispose();
        }
    }
}
