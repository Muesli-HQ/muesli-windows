namespace Muesli.Windows.Services;

public sealed record MeetingCaptureSnapshot(
    MeetingSessionState State,
    string SystemCaptureMode,
    bool MicrophoneAvailable,
    bool SystemAudioAvailable,
    IReadOnlyList<string> HealthWarnings,
    bool IsPaused,
    bool RecoveredFromInterruption);

/// <summary>
/// Plain-language disclosure of what a meeting is capturing. It always names both sources
/// truthfully (microphone, sound from this computer) and never claims a source that is not
/// running; the capture mechanism itself stays in the logs.
/// </summary>
public static class MeetingCaptureStatusFormatter
{
    public static string Describe(MeetingCaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State switch
        {
            MeetingSessionState.Recording or MeetingSessionState.DegradedRecording =>
                DescribeSources(snapshot.MicrophoneAvailable, snapshot.SystemAudioAvailable, snapshot.SystemCaptureMode),
            MeetingSessionState.Preparing => "Starting…",
            MeetingSessionState.Stopping or MeetingSessionState.Finalizing => "Saving and transcribing…",
            MeetingSessionState.RecoverableInterruption => snapshot.IsPaused
                ? "Paused. Nothing is being recorded."
                : "Recording was interrupted. What was captured is saved.",
            _ => "Not recording"
        };
    }

    /// <summary>What is being recorded right now, in the words a person would use.</summary>
    public static string DescribeSources(bool microphone, bool systemAudio, string? systemCaptureMode)
    {
        var system = IsAppTargeted(systemCaptureMode)
            ? "sound from the meeting app"
            : "all sound playing on this computer";
        return (microphone, systemAudio) switch
        {
            (true, true) => $"Recording your microphone and {system}.",
            (true, false) => "Recording your microphone only. Sound from this computer isn't being recorded, so other people may be missing.",
            (false, true) => $"Recording {system} only. Your microphone isn't being recorded.",
            _ => "Nothing is being recorded."
        };
    }

    private static bool IsAppTargeted(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() is "processtreeloopback" or "process-loopback" or "process-tree-loopback";

    public static string DescribeDetection(string? candidateKey, MeetingEvidenceStrength evidence, bool microphoneInUse, bool cameraInUse)
    {
        if (!string.IsNullOrWhiteSpace(candidateKey) && evidence == MeetingEvidenceStrength.Strong)
        {
            return $"Detected now: {candidateKey}. Calendar join is unavailable on Windows.";
        }
        if (!string.IsNullOrWhiteSpace(candidateKey) && evidence == MeetingEvidenceStrength.Weak)
        {
            return microphoneInUse
                ? $"Possible meeting window ({candidateKey}) with microphone activity."
                : $"A window title mentions {candidateKey}, but no microphone activity corroborates it.";
        }
        if (cameraInUse || microphoneInUse)
        {
            return MeetingCandidateResolver.DescribeSuppression(
                new MeetingPresenceSnapshot(evidence, microphoneInUse, cameraInUse, candidateKey));
        }
        return "No current Windows meeting evidence. Upcoming calendar meetings are not available.";
    }
}
