namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// A dictation as the store holds it. <see cref="Text"/> is the transcript the user can paste;
/// deleting it must remove the row rather than blank the column so a purge leaves nothing behind.
/// </summary>
public sealed record DictationRecord
{
    public string Id { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public int DurationMs { get; init; }
    public string ModelProfile { get; init; } = "";
    public string? FolderId { get; init; }
    public int WordCount { get; init; }

    /// <summary>
    /// Audio ownership: the capture this dictation came from, and whether the app still owns that
    /// file. A dictation whose audio was discarded keeps the path for provenance but must not claim
    /// the file still exists.
    /// </summary>
    public string? AudioPath { get; init; }

    public bool AudioRetained { get; init; }
}

/// <summary>
/// Everything about a meeting except its transcripts, notes, aliases and follow-ups, which are
/// separate rows so a history list never pays for megabytes of transcript text.
/// </summary>
public sealed record MeetingRecord
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>True once the user edits the title, which then survives any later regeneration.</summary>
    public bool TitleIsManual { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public int DurationMs { get; init; }
    public string ModelProfile { get; init; } = "";
    public string? FolderId { get; init; }
    public int WordCount { get; init; }
    public string TemplateName { get; init; } = "";
    public MeetingSessionState SessionState { get; init; } = MeetingSessionState.Completed;
    public bool RecoveredFromInterruption { get; init; }
    public IReadOnlyList<string> HealthWarnings { get; init; } = [];
    public MeetingAudioOwnership Audio { get; init; } = new();

    /// <summary>
    /// The post-meeting automation outcome, stored as the verbatim JSON the source produced. Keeping
    /// the original text means an automation payload that gains fields in a later release still
    /// round-trips through this store untouched.
    /// </summary>
    public string? AutomationResultJson { get; init; }

    /// <summary>
    /// The JSON meeting schema this row was imported from, retained so a later reader can tell a
    /// natively created meeting from one lifted out of <see cref="AppDataStore"/>.
    /// </summary>
    public int SourceSchemaVersion { get; init; } = AppDataStore.CurrentMeetingSchemaVersion;
}

/// <summary>
/// Which capture produced a meeting's audio and which model owns each transcript role. These travel
/// together because losing one of them turns a recording into an unattributable file on disk.
/// </summary>
public sealed record MeetingAudioOwnership
{
    public string SourcePath { get; init; } = "";
    public string? MicrophoneAudioPath { get; init; }
    public string? SystemAudioPath { get; init; }
    public string SystemCaptureMode { get; init; } = "";
    public string? LivePreviewModelId { get; init; }
    public string LiveTranscriptOwnership { get; init; } = "off";
    public string FinalTranscriptOwnerModelId { get; init; } = "";
    public string? GapRecoveryModelId { get; init; }
}

public enum MeetingNoteKind
{
    /// <summary>Rewritten by every re-summarization.</summary>
    Generated,

    /// <summary>Written by the user; never overwritten by a regeneration.</summary>
    Manual
}

public enum MeetingTranscriptKind
{
    /// <summary>Exactly what the model produced, kept so an edit is always reversible.</summary>
    Raw,

    /// <summary>The user's corrected transcript, if they made one.</summary>
    Edited
}

public sealed record MeetingNote
{
    public string MeetingId { get; init; } = "";
    public MeetingNoteKind Kind { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record MeetingTranscript
{
    public string MeetingId { get; init; } = "";
    public MeetingTranscriptKind Kind { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record SpeakerAliasRecord
{
    public string MeetingId { get; init; } = "";

    /// <summary>The diarized label as the model emitted it, for example "Speaker 1".</summary>
    public string SpeakerKey { get; init; } = "";

    /// <summary>The name the user gave that speaker.</summary>
    public string Alias { get; init; } = "";
}

public enum FollowUpStatus
{
    Open,
    Done,
    Dismissed
}

/// <summary>
/// A follow-up carried out of a meeting. It always belongs to the meeting that produced it and may
/// additionally link to the meeting or dictation that resolved it, which is what makes a chain of
/// recurring meetings traceable rather than a pile of unrelated action items.
/// </summary>
public sealed record FollowUpRecord
{
    public string Id { get; init; } = "";
    public string MeetingId { get; init; } = "";
    public string Text { get; init; } = "";
    public string Owner { get; init; } = "";
    public DateTimeOffset? DueAtUtc { get; init; }
    public FollowUpStatus Status { get; init; } = FollowUpStatus.Open;
    public string? LinkedMeetingId { get; init; }
    public string? LinkedDictationId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

/// <summary>A meeting with every child row that belongs to it, loaded or saved as one unit.</summary>
public sealed record MeetingDetail
{
    public MeetingRecord Meeting { get; init; } = new();
    public IReadOnlyList<MeetingNote> Notes { get; init; } = [];
    public IReadOnlyList<MeetingTranscript> Transcripts { get; init; } = [];
    public IReadOnlyList<SpeakerAliasRecord> SpeakerAliases { get; init; } = [];
    public IReadOnlyList<FollowUpRecord> FollowUps { get; init; } = [];

    public string? Note(MeetingNoteKind kind) =>
        Notes.FirstOrDefault(note => note.Kind == kind)?.Content;

    public string? Transcript(MeetingTranscriptKind kind) =>
        Transcripts.FirstOrDefault(transcript => transcript.Kind == kind)?.Content;
}

/// <summary>
/// A folder in the history tree. <see cref="ParentId"/> is null for a root folder;
/// <see cref="Metadata"/> is free-form user text (a description or colour tag) and is searchable.
/// </summary>
public sealed record FolderRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? ParentId { get; init; }
    public int SortOrder { get; init; }
    public string Metadata { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record TemplateRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Icon { get; init; } = "square.and.pencil";
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public enum HistorySort
{
    NewestFirst,
    OldestFirst,
    TitleAscending
}

/// <summary>Filters for a dictation history page. <see cref="Limit"/> bounds every read.</summary>
public sealed record DictationQuery
{
    public string? FolderId { get; init; }

    /// <summary>When true, <see cref="FolderId"/> is applied even if null, selecting unfiled items.</summary>
    public bool FilterByFolder { get; init; }

    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public HistorySort Sort { get; init; } = HistorySort.NewestFirst;
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

public sealed record MeetingQuery
{
    public string? FolderId { get; init; }
    public bool FilterByFolder { get; init; }
    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public MeetingSessionState? SessionState { get; init; }
    public HistorySort Sort { get; init; } = HistorySort.NewestFirst;
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
}

[Flags]
public enum SearchRecordKinds
{
    None = 0,
    Dictation = 1,
    Meeting = 2,
    Folder = 4,
    All = Dictation | Meeting | Folder
}

/// <summary>
/// The indexed fields a query may be restricted to. These map one-to-one onto columns of the FTS
/// table, so narrowing a search is a column filter rather than a post-filter over hits.
/// </summary>
[Flags]
public enum SearchFields
{
    None = 0,
    Title = 1,
    Transcript = 2,
    Notes = 4,
    DictationText = 8,
    SpeakerAliases = 16,
    Folder = 32,
    All = Title | Transcript | Notes | DictationText | SpeakerAliases | Folder
}

public enum SearchSort
{
    /// <summary>BM25 relevance, with title matches weighted above body matches.</summary>
    Relevance,
    NewestFirst
}

public sealed record SearchQuery
{
    public string Text { get; init; } = "";
    public SearchRecordKinds Kinds { get; init; } = SearchRecordKinds.All;
    public SearchFields Fields { get; init; } = SearchFields.All;
    public string? FolderId { get; init; }
    public SearchSort Sort { get; init; } = SearchSort.Relevance;

    /// <summary>
    /// Treats the last term as a prefix, which is what an as-you-type search box needs. Turn it off
    /// for an explicit "find this exact word" search.
    /// </summary>
    public bool PrefixMatchLastTerm { get; init; } = true;

    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public enum SearchRecordKind
{
    Dictation,
    Meeting,
    Folder
}

public sealed record SearchHit
{
    public SearchRecordKind Kind { get; init; }
    public string RecordId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? FolderId { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>The matching text with the matched terms bracketed, for list display.</summary>
    public string Snippet { get; init; } = "";

    /// <summary>BM25 score; lower is a better match, matching SQLite's own ordering.</summary>
    public double Rank { get; init; }
}
