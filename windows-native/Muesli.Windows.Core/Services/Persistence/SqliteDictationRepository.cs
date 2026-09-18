using Microsoft.Data.Sqlite;

namespace Muesli.Windows.Services.Persistence;

public sealed class SqliteDictationRepository : IDictationRepository
{
    private const string Columns =
        "id, created_at_utc, updated_at_utc, title, text, duration_ms, model_profile, " +
        "folder_id, word_count, audio_path, audio_retained";

    private readonly MuesliDatabase _database;

    public SqliteDictationRepository(MuesliDatabase database) => _database = database;

    public DictationRecord? Find(string id) =>
        _database.Read(connection =>
        {
            using var command = Db.Command(connection, $"SELECT {Columns} FROM dictations WHERE id = $id;")
                .Bind("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Map(reader) : null;
        });

    public IReadOnlyList<DictationRecord> List(DictationQuery query) =>
        _database.Read(connection =>
        {
            var filters = new List<string>();
            if (query.FilterByFolder)
            {
                filters.Add(query.FolderId is null ? "folder_id IS NULL" : "folder_id = $folderId");
            }

            if (query.Since is not null)
            {
                filters.Add("created_at_utc >= $since");
            }

            if (query.Until is not null)
            {
                filters.Add("created_at_utc <= $until");
            }

            var where = filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}";
            using var command = Db.Command(
                connection,
                $"SELECT {Columns} FROM dictations {where} ORDER BY {Order(query.Sort)} LIMIT $limit OFFSET $offset;");
            if (query.FilterByFolder && query.FolderId is not null)
            {
                command.Bind("$folderId", query.FolderId);
            }

            if (query.Since is not null)
            {
                command.Bind("$since", query.Since.Value);
            }

            if (query.Until is not null)
            {
                command.Bind("$until", query.Until.Value);
            }

            command.Bind("$limit", query.Limit).Bind("$offset", query.Offset);
            using var reader = command.ExecuteReader();
            var results = new List<DictationRecord>();
            while (reader.Read())
            {
                results.Add(Map(reader));
            }

            return (IReadOnlyList<DictationRecord>)results;
        });

    public int Count() =>
        _database.Read(connection => Db.Command(connection, "SELECT count(*) FROM dictations;").ScalarInt32());

    public void Upsert(DictationRecord dictation) =>
        _database.Write(connection => Write(connection, dictation));

    public void UpsertRange(IEnumerable<DictationRecord> dictations) =>
        _database.Write(connection =>
        {
            foreach (var dictation in dictations)
            {
                Write(connection, dictation);
            }
        });

    public bool Delete(string id) =>
        _database.Write(connection =>
        {
            var removed = Db.Command(connection, "DELETE FROM dictations WHERE id = $id;")
                .Bind("$id", id)
                .Execute();
            SearchIndexWriter.Remove(connection, PersistenceSchema.DictationKind, id);
            return removed > 0;
        });

    public int DeleteAll() =>
        _database.Write(connection =>
        {
            var removed = Db.Command(connection, "DELETE FROM dictations;").Execute();
            SearchIndexWriter.RemoveKind(connection, PersistenceSchema.DictationKind);
            return removed;
        });

    public void MoveToFolder(string id, string? folderId) =>
        _database.Write(connection =>
        {
            Db.Command(
                    connection,
                    "UPDATE dictations SET folder_id = $folderId, updated_at_utc = $updatedAt WHERE id = $id;")
                .Bind("$folderId", folderId)
                .Bind("$updatedAt", DateTimeOffset.UtcNow)
                .Bind("$id", id)
                .Execute();
            SearchIndexWriter.IndexDictation(connection, id);
        });

    private static void Write(SqliteConnection connection, DictationRecord dictation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictation.Id);
        Db.Command(
                connection,
                """
                INSERT INTO dictations (
                    id, created_at_utc, updated_at_utc, title, text, duration_ms,
                    model_profile, folder_id, word_count, audio_path, audio_retained)
                VALUES (
                    $id, $createdAt, $updatedAt, $title, $text, $durationMs,
                    $modelProfile, $folderId, $wordCount, $audioPath, $audioRetained)
                ON CONFLICT (id) DO UPDATE SET
                    created_at_utc = excluded.created_at_utc,
                    updated_at_utc = excluded.updated_at_utc,
                    title = excluded.title,
                    text = excluded.text,
                    duration_ms = excluded.duration_ms,
                    model_profile = excluded.model_profile,
                    folder_id = excluded.folder_id,
                    word_count = excluded.word_count,
                    audio_path = excluded.audio_path,
                    audio_retained = excluded.audio_retained;
                """)
            .Bind("$id", dictation.Id)
            .Bind("$createdAt", dictation.CreatedAtUtc)
            .Bind("$updatedAt", dictation.UpdatedAtUtc)
            .Bind("$title", dictation.Title)
            .Bind("$text", dictation.Text)
            .Bind("$durationMs", dictation.DurationMs)
            .Bind("$modelProfile", dictation.ModelProfile)
            .Bind("$folderId", dictation.FolderId)
            .Bind("$wordCount", dictation.WordCount)
            .Bind("$audioPath", dictation.AudioPath)
            .Bind("$audioRetained", dictation.AudioRetained)
            .Execute();
        SearchIndexWriter.IndexDictation(connection, dictation.Id);
    }

    private static string Order(HistorySort sort) =>
        sort switch
        {
            HistorySort.OldestFirst => "created_at_utc ASC, id ASC",
            HistorySort.TitleAscending => "title COLLATE NOCASE ASC, created_at_utc DESC",
            _ => "created_at_utc DESC, id ASC"
        };

    private static DictationRecord Map(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            CreatedAtUtc = reader.Instant(1),
            UpdatedAtUtc = reader.Instant(2),
            Title = reader.TextOrEmpty(3),
            Text = reader.TextOrEmpty(4),
            DurationMs = reader.Int32(5),
            ModelProfile = reader.TextOrEmpty(6),
            FolderId = reader.Text(7),
            WordCount = reader.Int32(8),
            AudioPath = reader.Text(9),
            AudioRetained = reader.Flag(10)
        };
}
