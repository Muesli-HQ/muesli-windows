using System.IO;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// SQLite-backed <see cref="ILibraryHistoryAdapter"/>. The production composition root opens it
/// only after <see cref="PersistenceCutover.EnsureMigrated"/> succeeds. Dictionary entries stay
/// on <c>windows-dictionary.json</c>.
/// </summary>
public sealed class SqliteLibraryHistoryAdapter : ILibraryHistoryAdapter, ILibrarySearchAdapter, ILibraryFolderAdapter, IDisposable
{
    private readonly MuesliPersistenceStore _store;
    private readonly AppDataStore _dictionary;
    private readonly string _jsonDirectory;
    private readonly bool _ownsStore;
    private bool _disposed;

    public SqliteLibraryHistoryAdapter(
        MuesliPersistenceStore store,
        string jsonDirectory,
        bool ownsStore = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonDirectory);
        _store = store;
        _jsonDirectory = Path.GetFullPath(jsonDirectory);
        _ownsStore = ownsStore;
        _dictionary = new AppDataStore(_jsonDirectory);
    }

    /// <summary>Opens <c>muesli.db</c> beside the JSON history directory.</summary>
    public static SqliteLibraryHistoryAdapter Open(string jsonDirectory)
    {
        var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(jsonDirectory));
        return new SqliteLibraryHistoryAdapter(store, jsonDirectory, ownsStore: true);
    }

    public MuesliPersistenceStore Store => _store;

    public IReadOnlyList<SearchHit> Search(SearchQuery query) => _store.Search.Search(query);

    public int CountMatches(SearchQuery query) => _store.Search.CountMatches(query);

    public string? LastWarning => _dictionary.LastWarning;

    public IReadOnlyList<PersistedDictation> LoadDictations() =>
        _store.Dictations.List(UnboundedHistoryQuery.Dictations)
            .Select(LibraryHistoryMapper.ToPersisted)
            .ToList();

    public void AppendDictation(PersistedDictation dictation)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        using var transaction = _store.BeginTransaction();
        _store.Dictations.Upsert(LibraryHistoryMapper.ToRecord(dictation));
        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "dictations");
        transaction.Commit();
    }

    public void SaveDictations(IEnumerable<PersistedDictation> dictations, bool afterExplicitDeletion = false)
    {
        var incoming = dictations.ToList();
        using var transaction = _store.BeginTransaction();
        if (afterExplicitDeletion)
        {
            var keep = incoming.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in _store.Dictations.List(UnboundedHistoryQuery.Dictations))
            {
                if (!keep.Contains(existing.Id))
                {
                    _store.Dictations.Delete(existing.Id);
                }
            }

            PurgeLeftoverJson(JsonHistorySnapshotReader.DictationsFileName, incoming);
        }

        foreach (var dictation in incoming)
        {
            _store.Dictations.Upsert(LibraryHistoryMapper.ToRecord(dictation));
        }

        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "dictations");
        transaction.Commit();
    }

    public IReadOnlyList<PersistedMeeting> LoadMeetings()
    {
        var heads = _store.Meetings.List(UnboundedHistoryQuery.Meetings);
        var meetings = new List<PersistedMeeting>(heads.Count);
        foreach (var head in heads)
        {
            var detail = _store.Meetings.FindDetail(head.Id);
            if (detail is not null)
            {
                meetings.Add(LibraryHistoryMapper.ToPersisted(detail));
            }
        }

        return meetings;
    }

    public void SaveMeetings(IEnumerable<PersistedMeeting> meetings, bool afterExplicitDeletion = false)
    {
        var incoming = meetings.ToList();
        using var transaction = _store.BeginTransaction();
        if (afterExplicitDeletion)
        {
            var keep = incoming.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in _store.Meetings.List(UnboundedHistoryQuery.Meetings))
            {
                if (!keep.Contains(existing.Id))
                {
                    _store.Meetings.Delete(existing.Id);
                }
            }

            PurgeLeftoverJson(JsonHistorySnapshotReader.MeetingsFileName, incoming);
        }

        foreach (var meeting in incoming)
        {
            var existing = _store.Meetings.FindDetail(meeting.Id);
            if (existing is null)
            {
                _store.Meetings.Save(LibraryHistoryMapper.ToNewDetail(meeting));
                continue;
            }

            // Per-field writes. Save(MeetingDetail) from UI-shaped rows would delete edited
            // transcripts, follow-ups, and other children the list never loaded.
            _store.Meetings.Upsert(LibraryHistoryMapper.MergeHead(existing, meeting));
            // These fields are authoritative in the UI-shaped record. Empty values deliberately
            // clear the corresponding child row; leaving the old note/alias behind makes a user
            // unable to remove data once SQLite becomes authoritative.
            _store.Meetings.SetNote(meeting.Id, MeetingNoteKind.Generated, meeting.Summary ?? "");
            _store.Meetings.SetNote(meeting.Id, MeetingNoteKind.Manual, meeting.ManualNotes ?? "");
            _store.Meetings.ReplaceSpeakerAliases(meeting.Id, meeting.SpeakerAliases);

            if (string.IsNullOrEmpty(meeting.Transcript) ||
                string.Equals(
                    meeting.Transcript,
                    existing.Transcript(MeetingTranscriptKind.Raw),
                    StringComparison.Ordinal))
            {
                // Clearing the edited transcript reveals the preserved raw model output, exactly as
                // the macOS history editor does. The raw row is never destroyed by a UI save.
                _store.Meetings.SetTranscript(meeting.Id, MeetingTranscriptKind.Edited, "");
            }
            else
            {
                var displayed = LibraryHistoryMapper.DisplayedTranscript(existing);
                if (!string.Equals(meeting.Transcript, displayed, StringComparison.Ordinal) &&
                    !string.Equals(meeting.Transcript, existing.Transcript(MeetingTranscriptKind.Raw), StringComparison.Ordinal))
                {
                    _store.Meetings.SetTranscript(meeting.Id, MeetingTranscriptKind.Edited, meeting.Transcript);
                }
            }
        }

        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "meetings");
        transaction.Commit();
    }

    public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() =>
        _store.Folders.List().Select(LibraryHistoryMapper.ToPersisted).ToList();

    public void MoveMeetingFolder(string id, string? newParentId) => _store.Folders.Move(id, newParentId);

    public bool DeleteMeetingFolder(string id) => _store.Folders.Delete(id, FolderDeleteMode.Detach);

    public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders) =>
        SaveMeetingFolders(folders, afterExplicitDeletion: false);

    /// <summary>
    /// Saves the visible folder list. The normal list update preserves folders outside the current
    /// view; callers that completed an explicit delete pass <c>true</c> so missing rows are removed.
    /// Existing parent ids on the persisted folder records are written through to SQLite.
    /// </summary>
    public void SaveMeetingFolders(
        IEnumerable<PersistedMeetingFolder> folders,
        bool afterExplicitDeletion = false)
    {
        var incoming = folders.ToList();
        using var transaction = _store.BeginTransaction();
        if (afterExplicitDeletion)
        {
            var keep = incoming.Select(folder => folder.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var existing in _store.Folders.List())
            {
                if (!keep.Contains(existing.Id))
                {
                    _store.Folders.Delete(existing.Id, FolderDeleteMode.Detach);
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < incoming.Count; index++)
        {
            var folder = incoming[index];
            var existing = _store.Folders.Find(folder.Id);
            _store.Folders.Upsert(new FolderRecord
            {
                Id = folder.Id,
                Name = folder.Name ?? "",
                ParentId = folder.ParentId,
                SortOrder = index,
                Metadata = existing?.Metadata ?? "",
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now
            });
        }

        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "folders");
        transaction.Commit();
    }

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => _dictionary.LoadDictionary();

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries)
    {
        _dictionary.SaveDictionary(entries);
        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "dictionary");
    }

    public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() =>
        _store.Templates.List().Select(LibraryHistoryMapper.ToPersisted).ToList();

    public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates)
    {
        var incoming = templates.ToList();
        using var transaction = _store.BeginTransaction();
        var keep = incoming.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in _store.Templates.List())
        {
            if (!keep.Contains(existing.Id))
            {
                _store.Templates.Delete(existing.Id);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var template = incoming[index];
            _store.Templates.Upsert(
                LibraryHistoryMapper.ToRecord(template, index, _store.Templates.Find(template.Id)));
        }

        PersistenceCutoverState.MarkFirstPostCutoverWrite(_store.Database, "templates");
        transaction.Commit();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsStore)
        {
            _store.Dispose();
        }
    }

    private void PurgeLeftoverJson<T>(string fileName, T value)
    {
        var path = Path.Combine(_jsonDirectory, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        new AtomicJsonFile().Save(path, value, AtomicJsonSaveMode.PrivacySensitive);
    }
}
