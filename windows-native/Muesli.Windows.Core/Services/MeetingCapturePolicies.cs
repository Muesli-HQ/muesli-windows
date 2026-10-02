namespace Muesli.Windows.Services;

public enum MeetingCaptureRepairAction
{
    RestartNow,
    RestartAfterBackoff,
    ContinueDegraded,
    PreserveForRecovery
}

public sealed record MeetingCaptureRepairDecision(
    MeetingCaptureRepairAction Action,
    TimeSpan Delay,
    int Attempt);

public static class MeetingCaptureRepairPolicy
{
    public const int MaximumAttempts = 3;

    public static MeetingCaptureRepairDecision Decide(
        int completedAttempts,
        bool peerChannelAvailable)
    {
        if (completedAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAttempts));
        }

        if (completedAttempts >= MaximumAttempts)
        {
            return new MeetingCaptureRepairDecision(
                peerChannelAvailable
                    ? MeetingCaptureRepairAction.ContinueDegraded
                    : MeetingCaptureRepairAction.PreserveForRecovery,
                TimeSpan.Zero,
                completedAttempts);
        }

        var delay = completedAttempts switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            _ => TimeSpan.FromSeconds(5)
        };
        return new MeetingCaptureRepairDecision(
            delay == TimeSpan.Zero
                ? MeetingCaptureRepairAction.RestartNow
                : MeetingCaptureRepairAction.RestartAfterBackoff,
            delay,
            completedAttempts + 1);
    }
}

public enum MeetingRecordingStartOrigin
{
    Manual,
    DetectedMeeting
}

public sealed class MeetingAutoStopTracker
{
    private readonly MeetingRecordingStartOrigin _origin;
    private readonly string? _sourceKey;
    private readonly TimeSpan _gracePeriod;
    private readonly int _minimumMissingObservations;
    private bool _observedDuringRecording;
    private DateTimeOffset? _missingSince;
    private int _missingObservations;
    private DateTimeOffset? _lastTranscriptActivity;
    private bool _warningSuppressed;
    private bool _dismissed;

    public MeetingAutoStopTracker(
        MeetingRecordingStartOrigin origin,
        string? sourceKey,
        TimeSpan? gracePeriod = null,
        int minimumMissingObservations = 3)
    {
        _origin = origin;
        _sourceKey = string.IsNullOrWhiteSpace(sourceKey) ? null : sourceKey;
        _gracePeriod = gracePeriod ?? TimeSpan.FromSeconds(20);
        _minimumMissingObservations = Math.Max(1, minimumMissingObservations);
    }

    public bool IsArmed => _origin == MeetingRecordingStartOrigin.DetectedMeeting && _sourceKey is not null;

    public void NoteTranscriptActivity(DateTimeOffset now) => _lastTranscriptActivity = now;
    public void SuppressWarning() => _warningSuppressed = true;
    public void DismissWarning() => _dismissed = true;

    public bool Observe(string? visibleSourceKey, DateTimeOffset now)
    {
        if (!IsArmed)
        {
            return false;
        }

        if (string.Equals(visibleSourceKey, _sourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _observedDuringRecording = true;
            _missingSince = null;
            _missingObservations = 0;
            _warningSuppressed = false;
            return false;
        }

        if (!_observedDuringRecording)
        {
            return false;
        }

        _missingSince ??= now;
        _missingObservations++;
        // Losing the source is a warning, never authorization to stop capture. Match macOS's
        // 45-second speech quiet period and dismissal for the rest of this recording.
        return !_dismissed && !_warningSuppressed &&
               (_lastTranscriptActivity is null || now - _lastTranscriptActivity >= TimeSpan.FromSeconds(45)) &&
               _missingObservations >= _minimumMissingObservations &&
               now - _missingSince >= _gracePeriod;
    }
}
