using System.IO;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// SQLite-backed <see cref="ILibraryHistoryAdapter"/>. The production composition root opens it
/// only after <see cref="LibraryDatabaseStartup.Prepare"/> reports the database ready. All structured library data lives here.
/// </summary>
public sealed class SqliteLibraryHistoryAdapter : ILibraryHistoryAdapter, ILibrarySearchAdapter, ILibraryFolderAdapter, IAtomicDictationCorrectionStore, IDisposable
{
    private readonly MuesliPersistenceStore _store;
    private readonly bool _ownsStore;
    private bool _disposed;

    public SqliteLibraryHistoryAdapter(
        MuesliPersistenceStore store,
        bool ownsStore = false, string? warning = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _ownsStore = ownsStore;
        LastWarning = warning;
    }

    /// <summary>Opens the existing <c>muesli.db</c> in a profile's data directory.</summary>
    public static SqliteLibraryHistoryAdapter Open(string dataDirectory, string? warning = null)
    {
        var store = new MuesliPersistenceStore(MuesliDatabase.OpenExisting(PersistencePaths.DatabasePathFor(dataDirectory)));
        return new SqliteLibraryHistoryAdapter(store, ownsStore: true, warning: warning);
    }

    public MuesliPersistenceStore Store => _store;

    public IReadOnlyList<SearchHit> Search(SearchQuery query) => _store.Search.Search(query);

    public int CountMatches(SearchQuery query) => _store.Search.CountMatches(query);

    public string? LastWarning { get; }

    public IReadOnlyList<PersistedDictation> LoadDictations() =>
        _store.Dictations.List(UnboundedHistoryQuery.Dictations)
            .Select(LibraryHistoryMapper.ToPersisted)
            .ToList();

    public void AppendDictation(PersistedDictation dictation)
    {
        ArgumentNullException.ThrowIfNull(dictation);
        using var transaction = _store.BeginTransaction();
        _store.Dictations.Upsert(LibraryHistoryMapper.ToRecord(dictation));
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
        }

        foreach (var dictation in incoming)
        {
            var existing = _store.Dictations.Find(dictation.Id);
            if (existing is null) _store.Dictations.Upsert(LibraryHistoryMapper.ToRecord(dictation));
            else if (LibraryHistoryMapper.ToPersisted(existing) != dictation)
                _store.Dictations.Upsert(existing with
                {
                    Text = dictation.Text, DurationMs = dictation.DurationMs, ModelProfile = dictation.ModelProfile,
                    WordCount = Muesli.Windows.Core.Insights.LibraryMetrics.CountWords(dictation.Text),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
        }

        transaction.Commit();
    }

    public bool TryUpdateDictationText(string id, string expectedText, string replacementText)
    {
        using var transaction = _store.BeginTransaction();
        var existing = _store.Dictations.Find(id);
        if (existing is null || existing.Text != expectedText) return false;
        // Preserve fields that the lightweight history projection does not carry (audio, folder, title).
        _store.Dictations.Upsert(existing with
        {
            Text = replacementText,
            WordCount = Muesli.Windows.Core.Insights.LibraryMetrics.CountWords(replacementText),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        transaction.Commit();
        return true;
    }

    public DictationCorrectionResult SaveCorrection(string id, string expectedText, string replacementText,
        string? phraseToRemember, string? rememberedReplacement,
        IReadOnlyList<DictionaryEntryRecord>? confirmedConflicts = null)
    {
        if (phraseToRemember is not null && (string.IsNullOrWhiteSpace(rememberedReplacement) ||
            string.IsNullOrWhiteSpace(DictionaryCorrectionService.PhraseKey(phraseToRemember))))
            return new(DictationCorrectionOutcome.Invalid, "Enter a phrase and replacement to remember.");
        try
        {
            using var transaction = _store.BeginTransaction();
            var existing = _store.Dictations.Find(id);
            if (existing is null || existing.Text != expectedText)
                return new(DictationCorrectionOutcome.Stale, "This dictation changed or was deleted. Select the text again.");
            if (phraseToRemember is not null)
            {
                var matching = _store.Dictionary.FindPhrase(phraseToRemember);
                var conflicts = matching.Where(entry => entry.Replacement.Trim() != rememberedReplacement!.Trim()).ToList();
                if (conflicts.Count > 0 && (confirmedConflicts is null || !conflicts.SequenceEqual(confirmedConflicts)))
                    return new(DictationCorrectionOutcome.Conflict,
                        "This phrase has a different remembered replacement. Confirm replacing it, or make a one-off edit.", conflicts);
                if (matching.Count != 1 || matching[0].Replacement.Trim() != rememberedReplacement!.Trim())
                {
                    var entry = matching.FirstOrDefault() ?? new DictionaryEntryRecord
                    {
                        Id = $"dict_{Guid.NewGuid():N}", Phrase = phraseToRemember,
                        MatchingThreshold = DictionaryCorrectionService.MaximumThreshold
                    };
                    foreach (var duplicate in matching.Skip(1)) _store.Dictionary.Delete(duplicate.Id);
                    _store.Dictionary.Upsert(entry with { Replacement = rememberedReplacement!.Trim() });
                }
            }
            if (!TryUpdateDictationText(id, expectedText, replacementText))
                throw new PersistenceException("The dictation changed during its correction transaction.");
            transaction.Commit();
            return new(DictationCorrectionOutcome.Saved, phraseToRemember is null
                ? "Dictation corrected. No dictionary entry was changed."
                : "Dictation corrected and correction remembered. Manage it in Dictionary.");
        }
        catch (Exception exception) when (exception is PersistenceException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return new(DictationCorrectionOutcome.Failed, "Could not save the correction. No changes were committed. Try again.");
        }
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

    public bool DeleteDictation(string id)
    {
        using var transaction = _store.BeginTransaction();
        if (!_store.Dictations.Delete(id)) return false;
        transaction.Commit();
        return true;
    }

    public bool DeleteMeeting(string id)
    {
        using var transaction = _store.BeginTransaction();
        foreach (var link in _store.Meetings.ListFollowUpsLinkedTo(id).Where(link => link.Id == $"meeting_thread_{id}"))
            _store.Meetings.DeleteFollowUp(link.Id);
        if (!_store.Meetings.Delete(id)) return false;
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Forgets a meeting's recording: source, microphone and system-audio paths. A normal save
    /// cannot do this, because <see cref="LibraryHistoryMapper.MergeHead"/> reads empty audio
    /// fields as "unchanged" so list-shaped saves never drop a recording by accident.
    /// </summary>
    public bool ClearMeetingAudio(string id)
    {
        using var transaction = _store.BeginTransaction();
        if (_store.Meetings.FindDetail(id) is not { } existing) return false;
        _store.Meetings.Upsert(existing.Meeting with
        {
            Audio = existing.Meeting.Audio with { SourcePath = "", MicrophoneAudioPath = null, SystemAudioPath = null }
        });
        transaction.Commit();
        return true;
    }

    /// <summary>One meeting by id, without loading the whole library.</summary>
    public PersistedMeeting? FindMeeting(string id) =>
        _store.Meetings.FindDetail(id) is { } detail ? LibraryHistoryMapper.ToPersisted(detail) : null;

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
        }

        foreach (var submitted in incoming)
        {
            // A page can still hold a folder that was deleted elsewhere (the sidebar, another
            // page). Deleting a folder unfiles its meetings, so a save naming it means "unfiled",
            // not a foreign-key failure that silently loses the notes being saved with it.
            var meeting = !string.IsNullOrWhiteSpace(submitted.FolderId) && _store.Folders.Find(submitted.FolderId) is null
                ? submitted with { FolderId = null }
                : submitted;
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

            if (!string.IsNullOrEmpty(meeting.Transcript) &&
                string.IsNullOrEmpty(existing.Transcript(MeetingTranscriptKind.Raw)) &&
                string.IsNullOrEmpty(existing.Transcript(MeetingTranscriptKind.Edited)))
            {
                // The row was created before any speech was recognized (notes typed while
                // recording, or a follow-up draft). Its first transcript is the recognizer's
                // output, as on macOS where finalization fills the transcript of the row
                // inserted at the start of the meeting. Later saves are edits of it.
                _store.Meetings.SetTranscript(meeting.Id, MeetingTranscriptKind.Raw, meeting.Transcript);
            }
            else if (string.IsNullOrEmpty(meeting.Transcript) ||
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

        transaction.Commit();
    }

    public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() =>
        _store.Folders.List().Select(LibraryHistoryMapper.ToPersisted).ToList();

    public void MoveMeetingFolder(string id, string? newParentId)
    {
        using var transaction = _store.BeginTransaction();
        _store.Folders.Move(id, newParentId);
        transaction.Commit();
    }

    public bool DeleteMeetingFolder(string id)
    {
        using var transaction = _store.BeginTransaction();
        var removed = _store.Folders.Delete(id, FolderDeleteMode.Detach);
        transaction.Commit();
        return removed;
    }

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

        transaction.Commit();
    }

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => _store.Dictionary.List();

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries)
    {
        using var transaction = _store.BeginTransaction();
        _store.Dictionary.Replace(entries.ToList());
        transaction.Commit();
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

}
