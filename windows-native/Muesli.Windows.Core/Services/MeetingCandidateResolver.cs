namespace Muesli.Windows.Services;

/// <summary>How strong the app/window/URL evidence is, independent of sensor activity.</summary>
public enum MeetingEvidenceStrength
{
    /// <summary>Nothing meeting-shaped was observed.</summary>
    None,

    /// <summary>A window title mentions a platform. Titles are user content and lie constantly.</summary>
    Weak,

    /// <summary>A dedicated meeting process, or a validated meeting join URL, is present.</summary>
    Strong
}

public sealed record MeetingPresenceSnapshot(
    MeetingEvidenceStrength Evidence,
    bool MicrophoneInUse,
    bool CameraInUse,
    string? CandidateKey);

public enum MeetingCandidateAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Evidence crossed the confirmation threshold; prompt the user once.</summary>
    Prompt,

    /// <summary>A previously confirmed meeting is gone.</summary>
    Ended
}

public sealed record MeetingCandidateDecision(
    MeetingCandidateAction Action,
    string? Key,
    string Reason);

/// <summary>
/// Turns a stream of presence snapshots into at most one prompt per meeting.
///
/// The policy is deliberately conservative, because a false prompt interrupts the user and a false
/// recording captures audio they did not ask to capture:
///
/// <list type="bullet">
/// <item>Camera activity alone never triggers. A webcam is used for photos, Windows Hello, and
/// camera apps, and treating it as a meeting is the single worst false positive.</item>
/// <item>Microphone activity alone never triggers. Dictation, voice notes, and games use it.</item>
/// <item>Weak evidence — a window merely titled like a platform — needs a live sensor to corroborate
/// it, because an article, a calendar invite, or a chat about Zoom is not a meeting.</item>
/// <item>Strong evidence — a dedicated meeting process or a validated join URL — stands on its own,
/// but still has to persist across consecutive scans so a transient window does not prompt.</item>
/// </list>
/// </summary>
public sealed class MeetingCandidateResolver
{
    /// <summary>Consecutive confirming scans before prompting.</summary>
    public const int RequiredConfirmations = 2;

    /// <summary>Consecutive empty scans before a confirmed meeting is treated as ended.</summary>
    public const int RequiredAbsences = 3;

    private readonly HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingKey;
    private int _confirmations;
    private string? _confirmedKey;
    private int _absences;

    public string? ConfirmedKey => _confirmedKey;

    /// <summary>True when the snapshot alone justifies treating this as a meeting, before hysteresis.</summary>
    public static bool QualifiesAsMeeting(MeetingPresenceSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.CandidateKey)) return false;
        return snapshot.Evidence switch
        {
            MeetingEvidenceStrength.Strong => true,
            // A title-only match needs a real sensor in use to corroborate it.
            MeetingEvidenceStrength.Weak => snapshot.MicrophoneInUse,
            _ => false
        };
    }

    public static string DescribeSuppression(MeetingPresenceSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.CandidateKey))
        {
            return snapshot switch
            {
                { CameraInUse: true, MicrophoneInUse: true } => "camera and microphone in use without any meeting app or link",
                { CameraInUse: true } => "camera in use without any meeting app or link",
                { MicrophoneInUse: true } => "microphone in use without any meeting app or link",
                _ => "no meeting evidence"
            };
        }
        return snapshot.Evidence == MeetingEvidenceStrength.Weak && !snapshot.MicrophoneInUse
            ? "window title mentions a meeting platform but no microphone activity corroborates it"
            : "no meeting evidence";
    }

    public MeetingCandidateDecision Observe(MeetingPresenceSnapshot snapshot)
    {
        var qualifies = QualifiesAsMeeting(snapshot);
        var key = snapshot.CandidateKey;

        if (!qualifies || key is null)
        {
            _pendingKey = null;
            _confirmations = 0;
            if (_confirmedKey is null) return new(MeetingCandidateAction.None, null, DescribeSuppression(snapshot));

            _absences++;
            if (_absences < RequiredAbsences)
            {
                return new(MeetingCandidateAction.None, _confirmedKey, $"confirmed meeting missing for {_absences} scan(s)");
            }
            var ended = _confirmedKey;
            _confirmedKey = null;
            _absences = 0;
            return new(MeetingCandidateAction.Ended, ended, "confirmed meeting absent through the grace window");
        }

        _absences = 0;

        if (_dismissed.Contains(key))
        {
            return new(MeetingCandidateAction.None, key, "candidate was dismissed by the user");
        }

        if (_confirmedKey is not null && _confirmedKey.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return new(MeetingCandidateAction.None, key, "already prompted for this meeting");
        }

        if (_pendingKey is null || !_pendingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            _pendingKey = key;
            _confirmations = 1;
        }
        else
        {
            _confirmations++;
        }

        if (_confirmations < RequiredConfirmations)
        {
            return new(MeetingCandidateAction.None, key, $"awaiting confirmation {_confirmations}/{RequiredConfirmations}");
        }

        _confirmedKey = key;
        _pendingKey = null;
        _confirmations = 0;
        return new(MeetingCandidateAction.Prompt, key, $"{snapshot.Evidence} evidence confirmed across {RequiredConfirmations} scans");
    }

    /// <summary>Suppresses further prompts for this meeting until it ends and reappears.</summary>
    public void Dismiss(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _dismissed.Add(key);
        if (_confirmedKey is not null && _confirmedKey.Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            _confirmedKey = null;
        }
        _pendingKey = null;
        _confirmations = 0;
    }

    /// <summary>Clears dismissal memory when a meeting genuinely ends, so a later session can prompt again.</summary>
    public void Forget(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _dismissed.Remove(key);
    }

    public void Reset()
    {
        _dismissed.Clear();
        _pendingKey = null;
        _confirmedKey = null;
        _confirmations = 0;
        _absences = 0;
    }
}
