namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Dictation history. Every read is bounded by <see cref="DictationQuery.Limit"/> so a caller cannot
/// accidentally materialise a ten-thousand-row history to render one page of it.
/// </summary>
public interface IDictationRepository
{
    DictationRecord? Find(string id);

    IReadOnlyList<DictationRecord> List(DictationQuery query);

    int Count();

    void Upsert(DictationRecord dictation);

    /// <summary>Writes many dictations as one transaction; used by import and migration paths.</summary>
    void UpsertRange(IEnumerable<DictationRecord> dictations);

    /// <summary>Returns false when the id was not present, so callers can distinguish a no-op delete.</summary>
    bool Delete(string id);

    /// <summary>Deletes every dictation and its search index entries, returning the row count removed.</summary>
    int DeleteAll();

    void MoveToFolder(string id, string? folderId);
}
