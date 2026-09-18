namespace Muesli.Windows.Services.Persistence;

/// <summary>FeatureRuntime-facing search contract shared by JSON and SQLite history modes.</summary>
public interface ILibrarySearchAdapter
{
    IReadOnlyList<SearchHit> Search(SearchQuery query);

    /// <summary>Total matches, ignoring the requested page.</summary>
    int CountMatches(SearchQuery query);
}

/// <summary>
/// In-memory search field list used by the dashboard until SQLite FTS is the live query path.
/// Notes, aliases, follow-ups, and folder text are also indexed by <see cref="ISearchRepository"/>.
/// </summary>
public static class ProductionInMemorySearchMatch
{
    public static bool DictationMatches(string text, string modelProfile, string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
        {
            return true;
        }

        return Contains(text, needle) || Contains(modelProfile, needle);
    }

    public static bool MeetingMatches(
        string title,
        string summary,
        string transcript,
        string metadata,
        string query,
        string? notes = null)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
        {
            return true;
        }

        return Contains(title, needle) ||
               Contains(summary, needle) ||
               Contains(transcript, needle) ||
               Contains(metadata, needle) ||
               Contains(notes ?? "", needle);
    }

    public static string MeetingSnippet(
        string title,
        string summary,
        string transcript,
        string notes,
        string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0)
        {
            return FirstNonEmpty(notes, transcript, summary);
        }

        if (Contains(notes, needle))
        {
            return notes;
        }

        if (Contains(title, needle))
        {
            return FirstNonEmpty(notes, transcript, summary);
        }

        if (Contains(transcript, needle))
        {
            return transcript;
        }

        if (Contains(summary, needle))
        {
            return summary;
        }

        return FirstNonEmpty(notes, transcript, summary);
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
