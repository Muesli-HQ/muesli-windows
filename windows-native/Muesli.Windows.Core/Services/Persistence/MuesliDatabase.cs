using System.IO;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public class PersistenceException : Exception
{
    public PersistenceException(string message)
        : base(message)
    {
    }

    public PersistenceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Thrown when a database cannot be brought to the schema this build understands.</summary>
public sealed class PersistenceSchemaException : PersistenceException
{
    public PersistenceSchemaException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A unit of work. Commit is explicit: disposing without committing rolls back, so an exception
/// escaping a using block can never leave a half-written meeting behind.
/// </summary>
public interface IPersistenceTransaction : IDisposable
{
    void Commit();

    void Rollback();
}

/// <summary>
/// Owns the single SQLite connection the persistence layer uses and serialises access to it.
/// <para>
/// One connection rather than a pool: SQLite's writer is single at any moment anyway, and a single
/// connection makes an ambient transaction a real scope that repository calls join instead of an
/// invisible piece of thread state. Reads and writes take a re-entrant lock, so a transaction opened
/// on a thread must be committed and disposed on that same thread.
/// </para>
/// </summary>
public sealed class MuesliDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private readonly bool _isFileDatabase;
    private AmbientTransaction? _ambient;
    private bool _disposed;

    private MuesliDatabase(SqliteConnection connection, string databasePath, bool isFileDatabase)
    {
        _connection = connection;
        _isFileDatabase = isFileDatabase;
        DatabasePath = databasePath;
    }

    /// <summary>The database file, or ":memory:" for a transient database.</summary>
    public string DatabasePath { get; }

    public bool IsFileDatabase => _isFileDatabase;

    public int SchemaVersion => Read(SchemaMigrator.ReadVersion);

    /// <summary>
    /// Opens (creating if needed) the database at <paramref name="databasePath"/> and brings it to
    /// the current schema.
    /// </summary>
    public static MuesliDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new PersistenceException($"The database path '{databasePath}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,

            // The connection lives as long as this object, and a pooled connection would keep a
            // handle on the file after disposal, which breaks backup restore and test cleanup.
            Pooling = false
        };

        return Create(builder.ToString(), fullPath, isFileDatabase: true);
    }

    /// <summary>A private in-memory database that lives exactly as long as this object.</summary>
    public static MuesliDatabase OpenInMemory()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false
        };

        return Create(builder.ToString(), ":memory:", isFileDatabase: false);
    }

    private static MuesliDatabase Create(string connectionString, string databasePath, bool isFileDatabase)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Configure(connection, isFileDatabase);
            SchemaMigrator.Migrate(connection, PersistenceSchema.Migrations);
            return new MuesliDatabase(connection, databasePath, isFileDatabase);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Configure(SqliteConnection connection, bool isFileDatabase)
    {
        // WAL keeps a long read (a search over the whole history) from blocking the write that
        // finishes a meeting. It is meaningless for an in-memory database.
        var pragmas = isFileDatabase
            ? "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY;"
            : "PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY;";
        using var command = connection.CreateCommand();
        command.CommandText = pragmas;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Opens a transaction that later repository calls on this thread join. Nested scopes do not
    /// open a second transaction; rolling one back forces the outermost commit to fail.
    /// </summary>
    public IPersistenceTransaction BeginTransaction()
    {
        Monitor.Enter(_gate);
        try
        {
            ThrowIfDisposed();
            if (_ambient is not null)
            {
                return new JoinedTransaction(_ambient, _gate);
            }

            var transaction = _connection.BeginTransaction();
            _ambient = new AmbientTransaction(this, transaction, _gate);
            return _ambient;
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    internal T Read<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return work(_connection);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside the ambient transaction if there is one, and otherwise
    /// inside a transaction of its own. Multi-statement writes are therefore atomic by default.
    /// </summary>
    internal T Write<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_ambient is not null)
            {
                return work(_connection);
            }

            using var transaction = _connection.BeginTransaction();
            var result = work(_connection);
            transaction.Commit();
            return result;
        }
    }

    internal void Write(Action<SqliteConnection> work) =>
        Write<object?>(connection =>
        {
            work(connection);
            return null;
        });

    /// <summary>
    /// Writes a consistent standalone copy of the database, including anything still in the WAL.
    /// Used to retain a rollback backup before a migration.
    /// </summary>
    public void BackupTo(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var fullPath = Path.GetFullPath(backupPath);
        if (File.Exists(fullPath))
        {
            throw new PersistenceException($"The backup path '{fullPath}' already exists.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_ambient is not null)
            {
                throw new PersistenceException("A backup cannot be taken inside a transaction.");
            }

            using var command = _connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path;";
            command.Parameters.AddWithValue("$path", fullPath);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Folds the WAL back into the database file so the file alone is complete.</summary>
    public void Checkpoint()
    {
        if (!_isFileDatabase)
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
    }

    private void ClearAmbient() => _ambient = null;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ambient?.DisposeWithoutUnlock();
            _ambient = null;

            // Pooling is off, so disposing the connection releases the file handle immediately and a
            // rollback restore or a test directory teardown can replace the database file at once.
            _connection.Dispose();
        }
    }

    private sealed class AmbientTransaction : IPersistenceTransaction
    {
        private readonly MuesliDatabase _database;
        private readonly SqliteTransaction _transaction;
        private readonly object _gate;
        private bool _completed;
        private bool _lockReleased;

        public AmbientTransaction(MuesliDatabase database, SqliteTransaction transaction, object gate)
        {
            _database = database;
            _transaction = transaction;
            _gate = gate;
        }

        public bool RollbackOnly { get; private set; }

        public void MarkRollbackOnly() => RollbackOnly = true;

        public void Commit()
        {
            if (_completed)
            {
                throw new PersistenceException("The transaction has already been completed.");
            }

            if (RollbackOnly)
            {
                Rollback();
                throw new PersistenceException(
                    "The transaction was marked rollback-only by a nested scope and cannot be committed.");
            }

            _transaction.Commit();
            Complete();
        }

        public void Rollback()
        {
            if (_completed)
            {
                return;
            }

            _transaction.Rollback();
            Complete();
        }

        /// <summary>
        /// Releases the connection lock exactly once. Completing the transaction deliberately does
        /// not unlock, so the lock is held for the whole scope and released by the using block.
        /// </summary>
        public void Dispose()
        {
            if (_lockReleased)
            {
                return;
            }

            if (!_completed)
            {
                // An escaping exception must not leave a partially written unit of work behind.
                try
                {
                    _transaction.Rollback();
                }
                catch (SqliteException)
                {
                    // The connection may already be gone; there is nothing left to undo.
                }

                Complete();
            }

            _lockReleased = true;
            Monitor.Exit(_gate);
        }

        public void DisposeWithoutUnlock()
        {
            _completed = true;
            _transaction.Dispose();
        }

        private void Complete()
        {
            _completed = true;
            _transaction.Dispose();
            _database.ClearAmbient();
        }
    }

    private sealed class JoinedTransaction : IPersistenceTransaction
    {
        private readonly AmbientTransaction _outer;
        private readonly object _gate;
        private bool _disposed;

        public JoinedTransaction(AmbientTransaction outer, object gate)
        {
            _outer = outer;
            _gate = gate;
        }

        public void Commit()
        {
            // The outermost scope decides; a nested commit only says "nothing went wrong here".
        }

        public void Rollback() => _outer.MarkRollbackOnly();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.Exit(_gate);
        }
    }
}
