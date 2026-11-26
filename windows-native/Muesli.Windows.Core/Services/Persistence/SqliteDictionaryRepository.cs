namespace Muesli.Windows.Services.Persistence;

/// <summary>Dictionary words and remembered corrections share the existing lossless record format.</summary>
public sealed class SqliteDictionaryRepository(MuesliDatabase database)
{
    public IReadOnlyList<DictionaryEntryRecord> List() => Read(null);

    public IReadOnlyList<DictionaryEntryRecord> FindPhrase(string phrase) =>
        Read(DictionaryCorrectionService.PhraseKey(phrase));

    private IReadOnlyList<DictionaryEntryRecord> Read(string? key) => database.Read(connection =>
    {
        using var command = Db.Command(connection,
            "SELECT id, phrase, replacement, matching_threshold FROM dictionary_entries " +
            (key is null ? "" : "WHERE phrase_key = $key ") + "ORDER BY sort_order, id;");
        if (key is not null) command.Bind("$key", key);
        using var reader = command.ExecuteReader();
        var result = new List<DictionaryEntryRecord>();
        while (reader.Read()) result.Add(new DictionaryEntryRecord
        {
            Id = reader.GetString(0), Phrase = reader.GetString(1), Replacement = reader.GetString(2),
            MatchingThreshold = reader.GetDouble(3)
        });
        return result;
    });

    public void Upsert(DictionaryEntryRecord entry, int? sortOrder = null)
    {
        Validate([entry]);
        database.Write(connection =>
        {
            using var command = Db.Command(connection, """
                INSERT INTO dictionary_entries (id, phrase, phrase_key, replacement, matching_threshold, sort_order)
                VALUES ($id, $phrase, $key, $replacement, $threshold,
                    COALESCE($order, (SELECT sort_order FROM dictionary_entries WHERE id = $id),
                        (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM dictionary_entries)))
                ON CONFLICT(id) DO UPDATE SET phrase = excluded.phrase, phrase_key = excluded.phrase_key,
                    replacement = excluded.replacement, matching_threshold = excluded.matching_threshold,
                    sort_order = excluded.sort_order;
                """);
            command.Bind("$id", entry.Id).Bind("$phrase", entry.Phrase)
                .Bind("$key", DictionaryCorrectionService.PhraseKey(entry.Phrase))
                .Bind("$replacement", entry.Replacement).Bind("$order", (long?)sortOrder);
            command.Parameters.AddWithValue("$threshold", entry.MatchingThreshold);
            command.ExecuteNonQuery();
        });
    }

    public bool Delete(string id) => database.Write(connection =>
        Db.Command(connection, "DELETE FROM dictionary_entries WHERE id = $id;").Bind("$id", id).Execute() > 0);

    /// <summary>Compatibility with the dictionary editor's complete-list save; only changed rows are written.</summary>
    public void Replace(IReadOnlyList<DictionaryEntryRecord> entries)
    {
        Validate(entries);
        using var transaction = database.BeginTransaction();
        var old = List();
        var incoming = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in old.Where(entry => !incoming.Contains(entry.Id))) Delete(entry.Id);
        var oldById = old.Select((entry, index) => (entry, index)).ToDictionary(x => x.entry.Id);
        for (var index = 0; index < entries.Count; index++)
            if (!oldById.TryGetValue(entries[index].Id, out var previous) ||
                previous.entry != entries[index] || previous.index != index)
                Upsert(entries[index], index);
        transaction.Commit();
    }

    internal static void Validate(IReadOnlyList<DictionaryEntryRecord> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) ||
                entry.Phrase is null || entry.Replacement is null || !double.IsFinite(entry.MatchingThreshold))
                throw new PersistenceException("Dictionary data contains an invalid or duplicate identifier or value; nothing was imported.");
    }
}
