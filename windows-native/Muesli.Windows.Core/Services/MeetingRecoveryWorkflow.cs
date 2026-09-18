namespace Muesli.Windows.Services;

internal sealed record MeetingRecoveryHost(
    Action<MeetingSessionTrigger, string> Transition,
    Func<MeetingSessionTrigger, bool> CanApply,
    Action<string> AddWarning,
    Action<MeetingSessionState, string?> SaveState,
    Action<string> LogDiagnostic);

internal sealed record MeetingRecoveryHydration(
    MeetingSessionJournal Journal,
    string MeetingId,
    DateTime StartedAt,
    string? MicrophoneName,
    int? TargetProcessId,
    MeetingLiveTranscriptionResult? LiveResult,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Journal discovery, recoverable-session hydration, interruption preservation, retry, and
/// cancellation. Does not own capture or ASR; those stay on the capture session and pipeline.
/// </summary>
internal sealed class MeetingRecoveryWorkflow
{
    internal const string RecoveredSessionWarning = "Recording was recovered after an interrupted Muesli session.";
    internal const string FinalizationInterruptedWarning =
        "Meeting finalization was interrupted; local audio is retained for retry.";

    private readonly MeetingPersistenceBoundary _persistence;
    private readonly AppLogService? _logService;

    public MeetingRecoveryWorkflow(MeetingPersistenceBoundary persistence, AppLogService? logService = null)
    {
        _persistence = persistence;
        _logService = logService;
    }

    public IReadOnlyList<RecoverableMeetingSession> Discover() => _persistence.DiscoverRecoverable();

    public static MeetingRecoveryHydration Hydrate(RecoverableMeetingSession recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        var journal = recovery.Journal with { RecoveredFromInterruption = true };
        var warnings = journal.Warnings.ToList();
        warnings.Add(RecoveredSessionWarning);
        return new MeetingRecoveryHydration(
            journal,
            journal.SessionId,
            journal.StartedAtUtc.LocalDateTime,
            journal.MicrophoneName,
            journal.TargetProcessId,
            BuildRecoveredLiveResult(journal),
            warnings);
    }

    public static MeetingLiveTranscriptionResult? BuildRecoveredLiveResult(MeetingSessionJournal journal)
    {
        if (journal.LiveModelId is null ||
            !journal.LiveTranscriptOwnership.Equals(
                LiveTranscriptOwnershipDescriptor.UnifiedSettingValue,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new MeetingLiveTranscriptionResult(
            journal.LiveTranscriptSegments,
            journal.LiveTranscriptGaps.Concat(
            [
                new LiveTranscriptGap(LiveTranscriptChannel.Microphone, 0, long.MaxValue, "recoverable-interruption"),
                new LiveTranscriptGap(LiveTranscriptChannel.System, 0, long.MaxValue, "recoverable-interruption")
            ]).ToList(),
            journal.LiveDroppedPacketCount,
            journal.LiveModelId,
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
    }

    public void PreserveRecoverable(MeetingRecoveryHost host, string failureCategory)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.CanApply(MeetingSessionTrigger.Interrupt))
        {
            host.Transition(MeetingSessionTrigger.Interrupt, "recoverable finalization interruption");
        }
        host.AddWarning(FinalizationInterruptedWarning);
        host.SaveState(MeetingSessionState.RecoverableInterruption, failureCategory);
        host.LogDiagnostic("recoverable-interruption");
    }

    public void PreserveInterrupted(
        MeetingRecoveryHost host,
        string reason,
        string warning,
        string failureCategory)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Transition(MeetingSessionTrigger.Interrupt, reason);
        host.AddWarning(warning);
        host.SaveState(MeetingSessionState.RecoverableInterruption, failureCategory);
    }

    public void FailWithoutAudio(MeetingRecoveryHost host, string reason, string failureCategory)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Transition(MeetingSessionTrigger.Fail, $"{reason} without recoverable audio");
        host.SaveState(MeetingSessionState.Failed, $"{failureCategory}-no-audio");
    }

    public void DiscardRecoverable(string meetingId) =>
        _persistence.DeleteWorkingSession(meetingId, MeetingAudioCleanupReason.Discarded);

    public void DeleteWorkingSession(string meetingId, MeetingAudioCleanupReason reason) =>
        _persistence.DeleteWorkingSession(meetingId, reason);

    public void TryDeleteWorkingSession(string meetingId, MeetingAudioCleanupReason reason, string failureLog)
    {
        try
        {
            _persistence.DeleteWorkingSession(meetingId, reason);
        }
        catch (Exception exception)
        {
            _logService?.Info($"{failureLog} category={exception.GetType().Name}");
        }
    }
}
