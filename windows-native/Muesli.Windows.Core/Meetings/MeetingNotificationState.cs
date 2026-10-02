namespace Muesli.Windows.Services;

/// <summary>
/// Pure countdown state for the notification's auto-dismiss timer and progress bar. The WinUI
/// window advances it from a dispatcher timer; keeping it here makes pause/resume testable without
/// a UI thread. Mirrors the macOS controller's dismiss deadline / paused-offset behaviour.
/// </summary>
public sealed class MeetingNotificationCountdown
{
    private double _totalSeconds;
    private double _remainingSeconds;
    private bool _paused;

    public double TotalSeconds => _totalSeconds;
    public double RemainingSeconds => _remainingSeconds;
    public bool IsPaused => _paused;
    public bool IsComplete => _totalSeconds > 0 && _remainingSeconds <= 0;

    /// <summary>Remaining fraction in [0,1]; the bar drains from full to empty.</summary>
    public double Progress => _totalSeconds <= 0 ? 0 : Math.Clamp(_remainingSeconds / _totalSeconds, 0, 1);

    public void Start(double totalSeconds)
    {
        _totalSeconds = Math.Max(0, totalSeconds);
        _remainingSeconds = _totalSeconds;
        _paused = false;
    }

    public void Stop()
    {
        _totalSeconds = 0;
        _remainingSeconds = 0;
        _paused = false;
    }

    /// <summary>Pauses without losing the exact remaining duration. No-op if already paused.</summary>
    public void Pause()
    {
        if (_totalSeconds <= 0) return;
        _paused = true;
    }

    /// <summary>Resumes from the exact remaining duration.</summary>
    public void Resume()
    {
        if (_totalSeconds <= 0 || _remainingSeconds <= 0) return;
        _paused = false;
    }

    /// <summary>
    /// Advances the countdown. Returns true exactly once when it reaches zero. A paused countdown
    /// does not advance.
    /// </summary>
    public bool Advance(TimeSpan delta)
    {
        if (_paused || _totalSeconds <= 0 || _remainingSeconds <= 0) return false;
        _remainingSeconds = Math.Max(0, _remainingSeconds - Math.Max(0, delta.TotalSeconds));
        return _remainingSeconds <= 0;
    }
}

/// <summary>
/// macOS-verified callback rules: an auto-dismiss that becomes paused during the fade-out still
/// closes, but must not fire the auto-dismiss callback; and when an auto-dismiss owns the cleanup
/// path the close callback is suppressed so it cannot double-run cleanup.
/// </summary>
public static class MeetingNotificationAutoDismissPolicy
{
    public static bool FiresAutoDismissAfterFade(bool wasPaused) => !wasPaused;

    public static bool SuppressesCloseCallbackDuringAutoDismiss(bool hasAutoDismissHandler) =>
        hasAutoDismissHandler;
}

/// <summary>The decision produced by the dedup/suppression state.</summary>
public sealed record MeetingNotificationDecision(
    bool Show,
    MeetingNotificationOutcome Outcome,
    string Reason,
    string? PromptId)
{
    public static MeetingNotificationDecision Suppress(MeetingNotificationOutcome outcome, string reason, string? promptId) =>
        new(false, outcome, reason, promptId);
}

/// <summary>
/// Framework-neutral dedup and suppression for meeting notifications. A prompt is suppressed while
/// its episode continues after an explicit or automatic dismissal; once the meeting genuinely ends,
/// <see cref="Forget"/> clears the suppression so a later episode can prompt again.
/// </summary>
public sealed class MeetingNotificationSuppressionState
{
    private readonly HashSet<string> _userDismissed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoDismissed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _recordingStarted = new(StringComparer.OrdinalIgnoreCase);

    public string? VisiblePromptId { get; private set; }

    public bool IsVisible => VisiblePromptId is not null;

    /// <summary>Decides whether a request may be shown, naming the sanitized suppression reason.</summary>
    public MeetingNotificationDecision Evaluate(
        MeetingNotificationRequest request,
        bool detectionEnabled,
        bool isRecording,
        bool isBusy)
    {
        if (!detectionEnabled && request.Kind != MeetingNotificationKind.SignalLost)
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.SuppressedDisabled, "meeting detection is disabled", request.PromptId);
        }

        if (isRecording && request.Kind != MeetingNotificationKind.SignalLost)
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.RecordingAlreadyActive, "a meeting is already recording", request.PromptId);
        }

        if (isBusy)
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.SuppressedBusy, "another meeting operation is busy", request.PromptId);
        }

        if (request.Kind == MeetingNotificationKind.SignalLost && !isRecording)
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.SuppressedBusy, "no active recording", request.PromptId);

        if (IsVisible && string.Equals(VisiblePromptId, request.PromptId, StringComparison.OrdinalIgnoreCase))
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.SuppressedDuplicate, "the same meeting prompt is already visible", request.PromptId);
        }

        if (_recordingStarted.Contains(request.PromptId))
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.RecordingAlreadyActive, "recording already started for this meeting episode", request.PromptId);
        }

        if (_userDismissed.Contains(request.PromptId) || _autoDismissed.Contains(request.PromptId))
        {
            return MeetingNotificationDecision.Suppress(
                MeetingNotificationOutcome.SuppressedDuplicate, "this meeting episode was already dismissed", request.PromptId);
        }

        return new MeetingNotificationDecision(true, request.Kind == MeetingNotificationKind.SignalLost
            ? MeetingNotificationOutcome.MeetingSignalLost : request.IsScheduled
            ? MeetingNotificationOutcome.UpcomingScheduledMeeting
            : MeetingNotificationOutcome.ActiveMeetingDetected,
            "prompt allowed", request.PromptId);
    }

    public void MarkShown(string promptId) => VisiblePromptId = promptId;

    public void MarkHidden() => VisiblePromptId = null;

    public void SuppressForUser(string promptId) => _userDismissed.Add(promptId);

    public void SuppressForAuto(string promptId) => _autoDismissed.Add(promptId);

    public void MarkRecordingStarted(string promptId)
    {
        _recordingStarted.Add(promptId);
        _userDismissed.Remove(promptId);
        _autoDismissed.Remove(promptId);
    }

    /// <summary>Clears suppression after the meeting genuinely ends, so a later episode may prompt.</summary>
    public bool Forget(string promptId)
    {
        var removed = _userDismissed.Remove(promptId) | _autoDismissed.Remove(promptId) | _recordingStarted.Remove(promptId);
        if (string.Equals(VisiblePromptId, promptId, StringComparison.OrdinalIgnoreCase))
        {
            VisiblePromptId = null;
        }

        return removed;
    }

    public bool IsSuppressed(string promptId) =>
        _userDismissed.Contains(promptId) || _autoDismissed.Contains(promptId) || _recordingStarted.Contains(promptId);

    public void Reset()
    {
        _userDismissed.Clear();
        _autoDismissed.Clear();
        _recordingStarted.Clear();
        VisiblePromptId = null;
    }
}
