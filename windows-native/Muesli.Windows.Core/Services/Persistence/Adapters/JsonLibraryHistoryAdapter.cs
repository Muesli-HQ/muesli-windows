namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Adapter for the pre-cutover JSON history. The production composition root uses this adapter
/// while the L27 gate is disabled, so FeatureRuntime has one history contract in either mode.
/// </summary>
public sealed class JsonLibraryHistoryAdapter : ILibraryHistoryAdapter, ILibrarySearchAdapter, ILibraryFolderAdapter
{
    public JsonLibraryHistoryAdapter(AppDataStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AppDataStore Store { get; }

    public IReadOnlyList<SearchHit> Search(SearchQuery query) =>
        JsonLibrarySearch.Search(Store, query);

    public int CountMatches(SearchQuery query) =>
        JsonLibrarySearch.Search(Store, query, countOnly: true).Count;

    public void MoveMeetingFolder(string id, string? newParentId)
    {
        var folders = Store.LoadMeetingFolders().ToList();
        var index = folders.FindIndex(folder => string.Equals(folder.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new PersistenceException($"Folder '{id}' does not exist.");
        }

        if (string.Equals(id, newParentId, StringComparison.Ordinal))
        {
            throw new PersistenceException($"Folder '{id}' cannot be its own parent.");
        }

        var byId = folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        if (newParentId is not null && !byId.ContainsKey(newParentId))
        {
            throw new PersistenceException($"Folder '{newParentId}' does not exist.");
        }

        var current = newParentId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && byId.TryGetValue(current, out var parent) && seen.Add(current))
        {
            if (string.Equals(current, id, StringComparison.Ordinal))
            {
                throw new PersistenceException($"Folder '{id}' cannot be moved inside its own subtree.");
            }

            current = parent.ParentId;
        }

        folders[index] = folders[index] with { ParentId = newParentId };
        Store.SaveMeetingFolders(folders);
    }

    public bool DeleteMeetingFolder(string id)
    {
        var folders = Store.LoadMeetingFolders().ToList();
        var folder = folders.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (folder is null)
        {
            return false;
        }

        folders = folders
            .Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal))
            .Select(item => string.Equals(item.ParentId, id, StringComparison.Ordinal)
                ? item with { ParentId = folder.ParentId }
                : item)
            .ToList();
        var meetings = Store.LoadMeetings()
            .Select(meeting => string.Equals(meeting.FolderId, id, StringComparison.Ordinal)
                ? meeting with { FolderId = null }
                : meeting)
            .ToList();
        Store.SaveFolderMutation(folders, meetings);
        return true;
    }

    public string? LastWarning => Store.LastWarning;

    public IReadOnlyList<PersistedDictation> LoadDictations() => Store.LoadDictations();

    public void AppendDictation(PersistedDictation dictation)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        var dictations = Store.LoadDictations().ToList();
        dictations.Insert(0, dictation);
        Store.SaveDictations(dictations);
    }

    public void SaveDictations(IEnumerable<PersistedDictation> dictations, bool afterExplicitDeletion = false)
    {
        if (afterExplicitDeletion)
        {
            Store.SaveDictationsAfterDeletion(dictations);
        }
        else
        {
            Store.SaveDictations(dictations);
        }
    }

    public IReadOnlyList<PersistedMeeting> LoadMeetings() => Store.LoadMeetings();

    public void SaveMeetings(IEnumerable<PersistedMeeting> meetings, bool afterExplicitDeletion = false)
    {
        if (afterExplicitDeletion)
        {
            Store.SaveMeetingsAfterDeletion(meetings);
        }
        else
        {
            Store.SaveMeetings(meetings);
        }
    }

    public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() => Store.LoadMeetingFolders();

    public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders) => Store.SaveMeetingFolders(folders);

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => Store.LoadDictionary();

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries) => Store.SaveDictionary(entries);

    public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() => Store.LoadMeetingTemplates();

    public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates) => Store.SaveMeetingTemplates(templates);
}

/// <summary>
/// Search implementation for the pre-cutover JSON adapter. It intentionally uses the same fields,
/// filtering, ordering and paging contract as SQLite FTS so switching persistence modes cannot
/// change what the dashboard finds. This path is bounded by the JSON history size and is replaced
/// by the indexed SQLite implementation after cutover.
/// </summary>
internal static class JsonLibrarySearch
{
    public static IReadOnlyList<SearchHit> Search(AppDataStore store, SearchQuery query, bool countOnly = false)
    {
        if (query.Kinds == SearchRecordKinds.None || query.Fields == SearchFields.None)
        {
            return [];
        }

        var terms = query.Text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        var folders = store.LoadMeetingFolders();
        var folderById = folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        var hits = new List<SearchHit>();

        if (query.Kinds.HasFlag(SearchRecordKinds.Dictation))
        {
            foreach (var dictation in store.LoadDictations())
            {
                if (!MatchesAll(terms, query, (SearchFields.DictationText, $"{dictation.Text}\n{dictation.ModelProfile}")))
                {
                    continue;
                }

                hits.Add(new SearchHit
                {
                    Kind = SearchRecordKind.Dictation,
                    RecordId = dictation.Id,
                    CreatedAtUtc = new DateTimeOffset(dictation.Timestamp.ToUniversalTime()),
                    Title = "",
                    Snippet = Snippet(dictation.Text, terms, query),
                    Rank = 1
                });
            }
        }

        if (query.Kinds.HasFlag(SearchRecordKinds.Meeting))
        {
            foreach (var meeting in store.LoadMeetings())
            {
                if (query.FolderId is not null && !string.Equals(query.FolderId, meeting.FolderId, StringComparison.Ordinal))
                {
                    continue;
                }

                var folderText = FolderText(meeting.FolderId, folderById);
                if (!MatchesAll(
                        terms,
                        query,
                        (SearchFields.Title, meeting.Title),
                        (SearchFields.Transcript, meeting.Transcript),
                        (SearchFields.Notes, string.Join("\n", meeting.Summary, meeting.ManualNotes)),
                        (SearchFields.SpeakerAliases, string.Join("\n", meeting.SpeakerAliases.Select(pair => $"{pair.Key} {pair.Value}"))),
                        (SearchFields.Folder, folderText)))
                {
                    continue;
                }

                var body = string.Join("\n", meeting.Summary, meeting.ManualNotes, meeting.Transcript);
                hits.Add(new SearchHit
                {
                    Kind = SearchRecordKind.Meeting,
                    RecordId = meeting.Id,
                    FolderId = meeting.FolderId,
                    CreatedAtUtc = new DateTimeOffset(meeting.CreatedAt.ToUniversalTime()),
                    Title = meeting.Title,
                    Snippet = Snippet(body, terms, query),
                    Rank = Matches(meeting.Title, terms, query) ? 0 : 1
                });
            }
        }

        if (query.Kinds.HasFlag(SearchRecordKinds.Folder))
        {
            foreach (var folder in folders)
            {
                var folderText = FolderText(folder.Id, folderById);
                if (!MatchesAll(
                        terms,
                        query,
                        (SearchFields.Title, folder.Name),
                        (SearchFields.Folder, folderText)))
                {
                    continue;
                }

                hits.Add(new SearchHit
                {
                    Kind = SearchRecordKind.Folder,
                    RecordId = folder.Id,
                    FolderId = folder.ParentId,
                    CreatedAtUtc = DateTimeOffset.MinValue,
                    Title = folder.Name,
                    Snippet = Snippet(folderText, terms, query),
                    Rank = Matches(folder.Name, terms, query) ? 0 : 1
                });
            }
        }

        IEnumerable<SearchHit> ordered = query.Sort == SearchSort.NewestFirst
            ? hits.OrderByDescending(hit => hit.CreatedAtUtc).ThenBy(hit => hit.RecordId, StringComparer.Ordinal)
            : hits.OrderBy(hit => hit.Rank).ThenByDescending(hit => hit.CreatedAtUtc).ThenBy(hit => hit.RecordId, StringComparer.Ordinal);
        var result = ordered.ToList();
        if (countOnly)
        {
            return result;
        }

        return result.Skip(Math.Max(0, query.Offset)).Take(Math.Max(0, query.Limit)).ToList();
    }

    private static bool MatchesAll(
        IReadOnlyList<string> terms,
        SearchQuery query,
        params (SearchFields Field, string Value)[] fields)
    {
        foreach (var term in terms)
        {
            var found = fields.Any(field =>
                query.Fields.HasFlag(field.Field) && MatchesTerm(field.Value, term, query.PrefixMatchLastTerm && term == terms[^1]));
            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(string value, IReadOnlyList<string> terms, SearchQuery query) =>
        terms.All(term => MatchesTerm(value, term, query.PrefixMatchLastTerm && term == terms[^1]));

    private static bool MatchesTerm(string value, string term, bool prefix)
    {
        var normalizedValue = Normalize(value);
        var normalizedTerm = Normalize(term);
        if (normalizedTerm.Length == 0)
        {
            return false;
        }

        if (!prefix)
        {
            // FTS matches a complete token, not an arbitrary substring. Check token boundaries
            // while retaining a readable fallback for punctuation-heavy human text.
            return normalizedValue
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Any(token => string.Equals(token.Trim('"', ',', '.', ':', ';', '!', '?', '-', '_'), normalizedTerm, StringComparison.OrdinalIgnoreCase));
        }

        return normalizedValue
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(token => token.Trim('"', ',', '.', ':', ';', '!', '?', '-', '_')
                .StartsWith(normalizedTerm, StringComparison.OrdinalIgnoreCase));
    }

    private static string Snippet(string value, IReadOnlyList<string> terms, SearchQuery query)
    {
        var first = value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => terms.Any(term => MatchesTerm(token, term, query.PrefixMatchLastTerm)));
        if (first is null)
        {
            return value;
        }

        var term = terms.First(term => MatchesTerm(first, term, query.PrefixMatchLastTerm));
        var index = first.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? $"[{first}]"
            : $"{first[..index]}[{first[index..(index + term.Length)]}]{first[(index + term.Length)..]}";
    }

    private static string FolderText(string? id, IReadOnlyDictionary<string, PersistedMeetingFolder> folders)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = id;
        while (current is not null && folders.TryGetValue(current, out var folder) && seen.Add(current))
        {
            values.Add(folder.Name);
            current = folder.ParentId;
        }

        values.Reverse();
        return string.Join(" ", values);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(character =>
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(chars.ToArray()).Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
    }
}
