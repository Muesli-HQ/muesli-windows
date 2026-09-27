namespace Muesli.Windows.Services;

/// <summary>
/// Framework-neutral result produced when a meeting recording reaches a terminal state.
/// Keeping this record in Core lets the WinUI and WPF shells consume the same finalization
/// pipeline without sharing windowing or capture implementation types.
/// </summary>
public sealed record RecordedMeetingResult(
    string MeetingId,
    string Title,
    DateTime StartedAt,
    int DurationMs,
    string Transcript,
    string? MicAudioPath,
    string? SystemAudioPath,
    List<string>? HealthWarnings = null,
    MeetingSessionState SessionState = MeetingSessionState.Completed,
    string SystemCaptureMode = "unknown",
    bool RecoveredFromInterruption = false,
    string? LivePreviewModelId = null,
    string LiveTranscriptOwnership = "off",
    string FinalTranscriptOwnerModelId = "",
    string? GapRecoveryModelId = null);
