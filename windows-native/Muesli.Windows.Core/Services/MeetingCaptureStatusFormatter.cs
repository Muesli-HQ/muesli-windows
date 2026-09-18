namespace Muesli.Windows.Services;

public sealed record MeetingCaptureSnapshot(
    MeetingSessionState State,
    string SystemCaptureMode,
    bool MicrophoneAvailable,
    bool SystemAudioAvailable,
    IReadOnlyList<string> HealthWarnings,
    bool IsPaused,
    bool RecoveredFromInterruption);

/// <summary>Truthful labels for microphone and system-audio capture. Never invents a working loopback.</summary>
public static class MeetingCaptureStatusFormatter
{
    public static string Describe(MeetingCaptureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = snapshot.State switch
        {
            MeetingSessionState.Recording => "Recording",
            MeetingSessionState.DegradedRecording => "Recording with a missing channel",
            MeetingSessionState.Preparing => "Preparing capture",
            MeetingSessionState.Stopping => "Stopping",
            MeetingSessionState.Finalizing => "Finalizing transcript",
            MeetingSessionState.RecoverableInterruption => snapshot.IsPaused
                ? "Paused · audio retained"
                : "Interrupted · audio retained",
            MeetingSessionState.Completed => "Completed",
            MeetingSessionState.Cancelled => "Cancelled",
            MeetingSessionState.Failed => "Failed",
            _ => "Idle"
        };
        var mic = snapshot.MicrophoneAvailable ? "Microphone capturing" : "Microphone unavailable";
        var system = DescribeSystemAudio(snapshot.SystemCaptureMode, snapshot.SystemAudioAvailable);
        var recovered = snapshot.RecoveredFromInterruption ? "Recovered from an interruption." : null;
        var warnings = snapshot.HealthWarnings.Count == 0
            ? null
            : string.Join(" ", snapshot.HealthWarnings);
        return string.Join(" · ", new[] { state, mic, system, recovered, warnings }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string DescribeSystemAudio(string? mode, bool available)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "processtreeloopback" or "process-loopback" or "process-tree-loopback" =>
                available
                    ? "System audio: process-tree loopback"
                    : "System audio: process-tree loopback requested but not capturing",
            "endpointloopback" or "endpoint-loopback" =>
                available
                    ? "System audio: render-endpoint loopback (not process-targeted)"
                    : "System audio: render-endpoint loopback unavailable",
            "imported-media" => "System audio: imported media file",
            "unavailable" or "pending" or "idle" or "" =>
                available
                    ? "System audio capturing"
                    : "System audio not captured",
            _ => available
                ? $"System audio: {mode}"
                : $"System audio unavailable ({mode})"
        };
    }

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
