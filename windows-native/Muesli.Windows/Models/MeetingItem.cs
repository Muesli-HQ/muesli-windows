using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed record MeetingItem(
    string Id,
    string Title,
    DateTime CreatedAt,
    string Transcript,
    string Summary,
    string SourcePath,
    string ModelProfile,
    int DurationMs,
    string? FolderId,
    int WordCount = 0,
    string TemplateName = "",
    Dictionary<string, string>? SpeakerAliases = null,
    List<string>? HealthWarnings = null,
    MeetingSessionState SessionState = MeetingSessionState.Completed,
    string? MicrophoneAudioPath = null,
    string? SystemAudioPath = null,
    string SystemCaptureMode = "legacy-unknown",
    bool RecoveredFromInterruption = false,
    string? LivePreviewModelId = null,
    string LiveTranscriptOwnership = "off",
    string FinalTranscriptOwnerModelId = "",
    string? GapRecoveryModelId = null,
    string ManualNotes = "",
    bool TitleIsManual = false,
    PostMeetingAutomationResult? AutomationResult = null)
{
    public string Metadata => $"{CreatedAt:yyyy-MM-dd HH:mm} • {DurationLabel} • {SessionStateLabel}";

    public string SessionStateLabel => SessionState switch
    {
        MeetingSessionState.Completed => RecoveredFromInterruption ? "Recovered" : "Completed",
        MeetingSessionState.Failed => "Needs attention",
        MeetingSessionState.RecoverableInterruption => "Recoverable",
        MeetingSessionState.Cancelled => "Cancelled",
        _ => SessionState.ToString()
    };

    public string DurationLabel
    {
        get
        {
            var seconds = Math.Max(0, (int)Math.Round(DurationMs / 1000.0));
            if (seconds >= 3600)
                return $"{seconds / 3600}h {(seconds % 3600) / 60}m";

            if (seconds >= 60)
            {
                var minutes = seconds / 60;
                var remainingSeconds = seconds % 60;
                return remainingSeconds == 0 ? $"{minutes}m" : $"{minutes}m {remainingSeconds}s";
            }

            return $"{seconds}s";
        }
    }

    public string PreviewText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Summary) ? Transcript : Summary;
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length > 200 ? text[..200] + "…" : text;
        }
    }
}
