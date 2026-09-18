namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Enum values are stored as their own explicit strings rather than as ordinals. An ordinal would
/// silently re-map every stored row the day a value is inserted into the middle of an enum, and the
/// CHECK constraints in the schema are written against these exact strings.
/// </summary>
internal static class PersistenceEnums
{
    public static string ToStorage(MeetingNoteKind kind) =>
        kind switch
        {
            MeetingNoteKind.Generated => "generated",
            MeetingNoteKind.Manual => "manual",
            _ => throw new PersistenceException($"Unknown note kind '{kind}'.")
        };

    public static MeetingNoteKind NoteKind(string value) =>
        value switch
        {
            "generated" => MeetingNoteKind.Generated,
            "manual" => MeetingNoteKind.Manual,
            _ => throw new PersistenceException($"Unknown note kind '{value}'.")
        };

    public static string ToStorage(MeetingTranscriptKind kind) =>
        kind switch
        {
            MeetingTranscriptKind.Raw => "raw",
            MeetingTranscriptKind.Edited => "edited",
            _ => throw new PersistenceException($"Unknown transcript kind '{kind}'.")
        };

    public static MeetingTranscriptKind TranscriptKind(string value) =>
        value switch
        {
            "raw" => MeetingTranscriptKind.Raw,
            "edited" => MeetingTranscriptKind.Edited,
            _ => throw new PersistenceException($"Unknown transcript kind '{value}'.")
        };

    public static string ToStorage(FollowUpStatus status) =>
        status switch
        {
            FollowUpStatus.Open => "open",
            FollowUpStatus.Done => "done",
            FollowUpStatus.Dismissed => "dismissed",
            _ => throw new PersistenceException($"Unknown follow-up status '{status}'.")
        };

    public static FollowUpStatus FollowUp(string value) =>
        value switch
        {
            "open" => FollowUpStatus.Open,
            "done" => FollowUpStatus.Done,
            "dismissed" => FollowUpStatus.Dismissed,
            _ => throw new PersistenceException($"Unknown follow-up status '{value}'.")
        };

    public static string ToStorage(MeetingSessionState state) => state.ToString();

    /// <summary>
    /// An unrecognised state reads as <see cref="MeetingSessionState.Completed"/>. A finished
    /// meeting written by a newer build must still be listed and playable by this one; refusing to
    /// read the row would hide the recording entirely.
    /// </summary>
    public static MeetingSessionState SessionState(string value) =>
        Enum.TryParse<MeetingSessionState>(value, ignoreCase: false, out var parsed)
            ? parsed
            : MeetingSessionState.Completed;

    public static string ToStorage(SearchRecordKind kind) =>
        kind switch
        {
            SearchRecordKind.Dictation => PersistenceSchema.DictationKind,
            SearchRecordKind.Meeting => PersistenceSchema.MeetingKind,
            SearchRecordKind.Folder => PersistenceSchema.FolderKind,
            _ => throw new PersistenceException($"Unknown search record kind '{kind}'.")
        };

    public static SearchRecordKind SearchKind(string value) =>
        value switch
        {
            PersistenceSchema.DictationKind => SearchRecordKind.Dictation,
            PersistenceSchema.MeetingKind => SearchRecordKind.Meeting,
            PersistenceSchema.FolderKind => SearchRecordKind.Folder,
            _ => throw new PersistenceException($"Unknown search record kind '{value}'.")
        };
}
