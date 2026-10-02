using Muesli.Windows.Core.Insights;

namespace Muesli.Windows.Services;

/// <summary>Matches the macOS finished-meeting resume and follow-up policies.</summary>
public static class MeetingContinuation
{
    public const string ResumeSeparator = "\n\n— Resumed —\n\n";
    public static bool CanContinue(PersistedMeeting meeting) => meeting.SessionState == MeetingSessionState.Completed;

    public static string FollowUpTitle(string title)
    {
        var text = title.Trim();
        while (text.StartsWith("Follow-up:", StringComparison.Ordinal)) text = text[10..].Trim();
        return text.Length == 0 ? "Follow-up meeting" : $"Follow-up: {text}";
    }

    public static string? CarriedNotes(PersistedMeeting meeting)
    {
        var notes = meeting.Summary.Trim();
        return notes.Length == 0 ? null : notes.Length <= 6000 ? notes : notes[..6000] + "\n[…previous notes truncated]";
    }

    public static PersistedMeeting AppendRecording(PersistedMeeting prior, PersistedMeeting recording)
    {
        var hasSpeech = !string.IsNullOrWhiteSpace(recording.Transcript);
        var transcript = !hasSpeech ? prior.Transcript : string.IsNullOrWhiteSpace(prior.Transcript)
            ? recording.Transcript : prior.Transcript + ResumeSeparator + recording.Transcript;
        return recording with
        {
            Id = prior.Id, CreatedAt = prior.CreatedAt, Title = prior.Title, TitleIsManual = prior.TitleIsManual,
            Transcript = transcript, Summary = hasSpeech ? "" : prior.Summary, TemplateName = prior.TemplateName,
            SessionState = hasSpeech ? recording.SessionState : prior.SessionState,
            RecoveredFromInterruption = prior.RecoveredFromInterruption || recording.RecoveredFromInterruption,
            DurationMs = checked(prior.DurationMs + recording.DurationMs), WordCount = LibraryMetrics.CountWords(transcript),
            ManualNotes = prior.ManualNotes, FolderId = prior.FolderId, SpeakerAliases = prior.SpeakerAliases,
            SourcePath = string.Join("; ", new[] { prior.SourcePath, prior.MicrophoneAudioPath, prior.SystemAudioPath,
                recording.SourcePath, recording.MicrophoneAudioPath, recording.SystemAudioPath }
                .Where(path => !string.IsNullOrWhiteSpace(path)).SelectMany(path => path!.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)),
            MicrophoneAudioPath = recording.MicrophoneAudioPath ?? prior.MicrophoneAudioPath,
            SystemAudioPath = recording.SystemAudioPath ?? prior.SystemAudioPath,
            HealthWarnings = prior.HealthWarnings.Concat(recording.HealthWarnings).Distinct().ToList()
        };
    }
}
