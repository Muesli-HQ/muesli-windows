namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Meetings and the rows that hang off them. Transcripts and notes are addressed by kind rather than
/// by a single blob column: a regeneration rewrites the generated note and must be unable to touch
/// the manual one, and an edited transcript must never overwrite the raw model output.
/// </summary>
public interface IMeetingRepository
{
    MeetingRecord? Find(string id);

    /// <summary>Loads the meeting with its notes, transcripts, aliases and follow-ups.</summary>
    MeetingDetail? FindDetail(string id);

    IReadOnlyList<MeetingRecord> List(MeetingQuery query);

    int Count();

    /// <summary>Writes the meeting head row, leaving notes, transcripts and aliases untouched.</summary>
    void Upsert(MeetingRecord meeting);

    /// <summary>
    /// Writes the meeting and replaces its child rows in one transaction. Child collections are
    /// authoritative: a kind absent from <paramref name="detail"/> is deleted.
    /// </summary>
    void Save(MeetingDetail detail);

    void SaveRange(IEnumerable<MeetingDetail> details);

    bool Delete(string id);

    int DeleteAll();

    void MoveToFolder(string id, string? folderId);

    void SetNote(string meetingId, MeetingNoteKind kind, string content);

    IReadOnlyList<MeetingNote> ListNotes(string meetingId);

    void SetTranscript(string meetingId, MeetingTranscriptKind kind, string content);

    IReadOnlyList<MeetingTranscript> ListTranscripts(string meetingId);

    /// <summary>Replaces the meeting's alias map wholesale; an empty map clears it.</summary>
    void ReplaceSpeakerAliases(string meetingId, IReadOnlyDictionary<string, string> aliases);

    IReadOnlyDictionary<string, string> GetSpeakerAliases(string meetingId);

    void UpsertFollowUp(FollowUpRecord followUp);

    IReadOnlyList<FollowUpRecord> ListFollowUps(string meetingId);

    /// <summary>Follow-ups that point back at this meeting as the one that resolved them.</summary>
    IReadOnlyList<FollowUpRecord> ListFollowUpsLinkedTo(string meetingId);

    bool DeleteFollowUp(string id);
}
