namespace Muesli.Windows.Services;

// The records the app passes to and from the library. They are stored in SQLite by
// SqliteLibraryHistoryAdapter; see database-schema.md at the repository root.

public sealed record PersistedDictation(
    string Id,
    DateTime Timestamp,
    string Text,
    int DurationMs,
    string ModelProfile);

public sealed record PersistedMeeting
{
    /// <summary>The meeting record shape this build writes; stored as <c>meetings.source_schema_version</c>.</summary>
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; init; }
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public int DurationMs { get; init; }
    public string Transcript { get; init; } = "";
    public string Summary { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string ModelProfile { get; init; } = "";
    public string? FolderId { get; init; }
    public int WordCount { get; init; }
    public string TemplateName { get; init; } = "";
    public Dictionary<string, string> SpeakerAliases { get; init; } = new();
    public List<string> HealthWarnings { get; init; } = new();
    public MeetingSessionState SessionState { get; init; } = MeetingSessionState.Completed;
    public string? MicrophoneAudioPath { get; init; }
    public string? SystemAudioPath { get; init; }
    public string SystemCaptureMode { get; init; } = "";
    public bool RecoveredFromInterruption { get; init; }
    public string? LivePreviewModelId { get; init; }
    public string LiveTranscriptOwnership { get; init; } = "off";
    public string FinalTranscriptOwnerModelId { get; init; } = "";
    public string? GapRecoveryModelId { get; init; }

    /// <summary>
    /// Notes the user typed. Generated notes are rewritten by every re-summarization; these are not,
    /// and are never merged into the generated body — losing a user's own writing to a background
    /// regeneration is unrecoverable for them.
    /// </summary>
    public string ManualNotes { get; init; } = "";

    /// <summary>True once the user edits the title, which then survives any later regeneration.</summary>
    public bool TitleIsManual { get; init; }

    /// <summary>
    /// Latest bounded, redacted post-meeting automation outcome. This is diagnostics only and is
    /// deliberately separate from capture-health warnings so an optional hook cannot change the
    /// meeting's durable completion state.
    /// </summary>
    public PostMeetingAutomationResult? AutomationResult { get; init; }
}

public sealed record PersistedMeetingFolder(
    string Id,
    string Name,
    string? ParentId = null);

public sealed record DictionaryEntryRecord
{
    public string Id { get; init; } = "";
    public string Phrase { get; init; } = "";
    public string Replacement { get; init; } = "";
    public double MatchingThreshold { get; init; } = 0.85;
}

public sealed record PersistedMeetingTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Prompt { get; init; } = "";
    public string Icon { get; init; } = "square.and.pencil";
}
