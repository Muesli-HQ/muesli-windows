namespace Muesli.Windows.Services;

/// <summary>
/// Durable-save sequencing for meeting journals and owned-audio cleanup.
/// Working capture files live in the in-progress session directory. Retained recordings are
/// copied to the recordings root before that directory is deleted. Audio is never deleted
/// before the meeting record is durably persisted, cancelled, or discarded.
/// </summary>
internal sealed class MeetingPersistenceBoundary
{
    private readonly MeetingSessionJournalStore _store;

    public MeetingPersistenceBoundary(MeetingSessionJournalStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public MeetingSessionJournalStore Store => _store;

    public MeetingSessionJournal Create(
        string sessionId,
        string title,
        DateTimeOffset startedAtUtc,
        string microphoneName,
        string modelId,
        bool retainRecording,
        int? targetProcessId,
        string? liveModelId = null,
        LiveTranscriptOwnershipMode? liveOwnership = null) =>
        _store.Create(
            sessionId,
            title,
            startedAtUtc,
            microphoneName,
            modelId,
            retainRecording,
            targetProcessId,
            liveModelId,
            liveOwnership);

    public void Save(MeetingSessionJournal journal) => _store.Save(journal);

    public string GetSessionDirectory(string sessionId) => _store.GetSessionDirectory(sessionId);

    public MeetingSessionJournal AppendPart(
        MeetingSessionJournal journal,
        MeetingAudioChannel channel,
        string transcriptionPath,
        long? startedAtMs = null) =>
        _store.AppendPart(journal, channel, transcriptionPath, startedAtMs);

    public MeetingAudioPaths BuildFinalTracks(MeetingSessionJournal journal) =>
        _store.BuildFinalTracks(journal);

    public IReadOnlyList<MeetingTrackPart> BuildTrackTimeline(
        MeetingSessionJournal journal,
        MeetingAudioChannel channel) =>
        _store.BuildTrackTimeline(journal, channel);

    public IReadOnlyList<RecoverableMeetingSession> DiscoverRecoverable() =>
        _store.DiscoverRecoverable();

    public MeetingSessionJournal ApplyState(
        MeetingSessionJournal journal,
        MeetingSessionState state,
        IReadOnlyList<string> warnings,
        string? failureCategory = null) =>
        journal with
        {
            State = state,
            Warnings = warnings.ToList(),
            LastFailureCategory = failureCategory,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

    public static bool HasAudio(MeetingSessionJournal? journal) =>
        journal is not null &&
        (journal.MicrophoneParts.Count > 0 || journal.SystemParts.Count > 0);

    public static string? OwnedMicrophonePath(bool retainRecording, MeetingAudioPaths tracks) =>
        retainRecording ? tracks.MicrophonePath : null;

    public static string? OwnedSystemPath(bool retainRecording, MeetingAudioPaths tracks) =>
        retainRecording ? tracks.SystemPath : null;

    public static bool CanDeleteWorkingSession(MeetingAudioCleanupReason reason) =>
        reason is MeetingAudioCleanupReason.DurableMeetingPersisted
            or MeetingAudioCleanupReason.Cancelled
            or MeetingAudioCleanupReason.Discarded
            or MeetingAudioCleanupReason.FailedStartWithoutAudio;

    public void DeleteWorkingSession(string sessionId, MeetingAudioCleanupReason reason)
    {
        if (!CanDeleteWorkingSession(reason))
        {
            throw new InvalidOperationException(
                $"Working meeting audio cannot be deleted for {reason}; the meeting record is not durably persisted.");
        }

        _store.DeleteSession(sessionId);
    }
}

internal enum MeetingAudioCleanupReason
{
    DurableMeetingPersisted,
    Cancelled,
    Discarded,
    FailedStartWithoutAudio
}
