using System.Data;
using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Small helpers over ADO.NET so the repositories read as SQL plus binding rather than as ceremony.
/// Commands are always created through <see cref="SqliteConnection.CreateCommand"/>, which attaches
/// the connection's active transaction; a command built any other way is rejected by the provider
/// while a transaction is open.
/// </summary>
internal static class Db
{
    public static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static SqliteCommand Bind(this SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        return command;
    }

    public static SqliteCommand Bind(this SqliteCommand command, string name, long value)
    {
        command.Parameters.AddWithValue(name, value);
        return command;
    }

    public static SqliteCommand Bind(this SqliteCommand command, string name, long? value)
    {
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
        return command;
    }

    public static SqliteCommand Bind(this SqliteCommand command, string name, int value)
    {
        command.Parameters.AddWithValue(name, (long)value);
        return command;
    }

    /// <summary>Booleans are bound as 0/1 because the tables are STRICT INTEGER columns.</summary>
    public static SqliteCommand Bind(this SqliteCommand command, string name, bool value)
    {
        command.Parameters.AddWithValue(name, value ? 1L : 0L);
        return command;
    }

    public static SqliteCommand Bind(this SqliteCommand command, string name, DateTimeOffset value) =>
        command.Bind(name, PersistenceTime.ToStorage(value));

    public static SqliteCommand Bind(this SqliteCommand command, string name, DateTimeOffset? value) =>
        command.Bind(name, PersistenceTime.ToStorage(value));

    public static int Execute(this SqliteCommand command)
    {
        using (command)
        {
            return command.ExecuteNonQuery();
        }
    }

    public static long ScalarInt64(this SqliteCommand command)
    {
        using (command)
        {
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
    }

    public static int ScalarInt32(this SqliteCommand command) => (int)command.ScalarInt64();

    public static string? Text(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static string TextOrEmpty(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

    public static bool Flag(this SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && reader.GetInt64(ordinal) != 0;

    public static DateTimeOffset Instant(this SqliteDataReader reader, int ordinal) =>
        PersistenceTime.FromStorage(reader.Int64(ordinal));

    public static DateTimeOffset? NullableInstant(this SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : PersistenceTime.FromStorage(reader.Int64(ordinal));

    /// <summary>
    /// Reads an integer without assuming the column's storage class. FTS5 keeps unindexed column
    /// values in a shadow table with no declared affinity, so a value written as an integer can come
    /// back as text on some paths.
    /// </summary>
    public static long Int64(this SqliteDataReader reader, int ordinal) =>
        reader.GetFieldType(ordinal) == typeof(long)
            ? reader.GetInt64(ordinal)
            : Convert.ToInt64(reader.GetValue(ordinal));

    public static int Int32(this SqliteDataReader reader, int ordinal) => (int)reader.Int64(ordinal);
}
