namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Full-text search across titles, transcripts, notes, dictation text, speaker aliases and folder
/// metadata. The index is maintained inside the same transaction as the write that changed a record,
/// so a committed record is always findable and a rolled-back one is never indexed.
/// </summary>
public interface ISearchRepository
{
    IReadOnlyList<SearchHit> Search(SearchQuery query);

    /// <summary>Total matches for the query, ignoring limit and offset.</summary>
    int CountMatches(SearchQuery query);

    /// <summary>
    /// Rebuilds every search document from the relational tables. Needed after a bulk import and as
    /// the repair path if an index is ever found inconsistent.
    /// </summary>
    void Rebuild();
}
