using System.IO;
using System.Text.Json;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Maps repository records onto the FeatureRuntime-facing <see cref="ILibraryHistoryAdapter"/>
/// shapes without going through whole-collection <see cref="IMeetingRepository.Save"/>.
/// </summary>
internal static class LibraryHistoryMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PersistedDictation ToPersisted(DictationRecord record) =>
        new(
            record.Id,
            PersistenceTime.ToDateTime(record.CreatedAtUtc),
            record.Text,
            record.DurationMs,
            record.ModelProfile);

    public static DictationRecord ToRecord(PersistedDictation dictation)
    {
        var createdAt = PersistenceTime.Normalize(dictation.Timestamp);
        var text = dictation.Text ?? "";
        return new DictationRecord
        {
            Id = dictation.Id,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = createdAt,
            Title = "",
            Text = text,
            DurationMs = dictation.DurationMs,
            ModelProfile = dictation.ModelProfile ?? "",
            FolderId = null,
            WordCount = CountWords(text),
            AudioPath = null,
            AudioRetained = false
        };
    }

    public static PersistedMeeting ToPersisted(MeetingDetail detail)
    {
        var meeting = detail.Meeting;
        var edited = detail.Transcript(MeetingTranscriptKind.Edited);
        var raw = detail.Transcript(MeetingTranscriptKind.Raw);
        return new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = meeting.Id,
            Title = meeting.Title,
            TitleIsManual = meeting.TitleIsManual,
            CreatedAt = PersistenceTime.ToDateTime(meeting.CreatedAtUtc),
            DurationMs = meeting.DurationMs,
            Transcript = edited ?? raw ?? "",
            Summary = detail.Note(MeetingNoteKind.Generated) ?? "",
            ManualNotes = detail.Note(MeetingNoteKind.Manual) ?? "",
            SourcePath = meeting.Audio.SourcePath,
            ModelProfile = meeting.ModelProfile,
            FolderId = meeting.FolderId,
            WordCount = meeting.WordCount,
            TemplateName = meeting.TemplateName,
            SpeakerAliases = detail.SpeakerAliases
                .ToDictionary(alias => alias.SpeakerKey, alias => alias.Alias, StringComparer.Ordinal),
            HealthWarnings = meeting.HealthWarnings.ToList(),
            SessionState = meeting.SessionState,
            MicrophoneAudioPath = meeting.Audio.MicrophoneAudioPath,
            SystemAudioPath = meeting.Audio.SystemAudioPath,
            SystemCaptureMode = meeting.Audio.SystemCaptureMode,
            RecoveredFromInterruption = meeting.RecoveredFromInterruption,
            LivePreviewModelId = meeting.Audio.LivePreviewModelId,
            LiveTranscriptOwnership = meeting.Audio.LiveTranscriptOwnership,
            FinalTranscriptOwnerModelId = meeting.Audio.FinalTranscriptOwnerModelId,
            GapRecoveryModelId = meeting.Audio.GapRecoveryModelId,
            AutomationResult = DeserializeAutomation(meeting.AutomationResultJson)
        };
    }

    public static MeetingDetail ToNewDetail(PersistedMeeting meeting)
    {
        var createdAt = PersistenceTime.Normalize(meeting.CreatedAt);
        return new MeetingDetail
        {
            Meeting = ToHead(meeting, createdAt, createdAt, meeting.AutomationResult is null
                ? null
                : JsonSerializer.Serialize(meeting.AutomationResult, JsonOptions)),
            Notes = Notes(meeting),
            Transcripts = string.IsNullOrEmpty(meeting.Transcript)
                ? []
                : [new MeetingTranscript
                {
                    MeetingId = meeting.Id,
                    Kind = MeetingTranscriptKind.Raw,
                    Content = meeting.Transcript
                }],
            SpeakerAliases = Aliases(meeting),
            FollowUps = []
        };
    }

    public static MeetingRecord MergeHead(MeetingDetail existing, PersistedMeeting incoming)
    {
        var meeting = existing.Meeting;
        var incomingAudioEmpty = string.IsNullOrWhiteSpace(incoming.SourcePath) &&
                                 string.IsNullOrWhiteSpace(incoming.MicrophoneAudioPath) &&
                                 string.IsNullOrWhiteSpace(incoming.SystemAudioPath);
        return meeting with
        {
            Title = incoming.Title ?? meeting.Title,
            TitleIsManual = incoming.TitleIsManual || meeting.TitleIsManual,
            DurationMs = incoming.DurationMs != 0 ? incoming.DurationMs : meeting.DurationMs,
            ModelProfile = string.IsNullOrWhiteSpace(incoming.ModelProfile)
                ? meeting.ModelProfile
                : incoming.ModelProfile,
            FolderId = incoming.FolderId,
            WordCount = incoming.WordCount != 0 ? incoming.WordCount : meeting.WordCount,
            TemplateName = incoming.TemplateName ?? meeting.TemplateName,
            SessionState = incoming.SessionState,
            RecoveredFromInterruption = incoming.RecoveredFromInterruption,
            HealthWarnings = incoming.HealthWarnings.Count > 0
                ? incoming.HealthWarnings
                : meeting.HealthWarnings,
            Audio = incomingAudioEmpty
                ? meeting.Audio
                : new MeetingAudioOwnership
                {
                    SourcePath = incoming.SourcePath ?? meeting.Audio.SourcePath,
                    MicrophoneAudioPath = incoming.MicrophoneAudioPath ?? meeting.Audio.MicrophoneAudioPath,
                    SystemAudioPath = incoming.SystemAudioPath ?? meeting.Audio.SystemAudioPath,
                    SystemCaptureMode = string.IsNullOrWhiteSpace(incoming.SystemCaptureMode)
                        ? meeting.Audio.SystemCaptureMode
                        : incoming.SystemCaptureMode,
                    LivePreviewModelId = incoming.LivePreviewModelId ?? meeting.Audio.LivePreviewModelId,
                    LiveTranscriptOwnership = string.IsNullOrWhiteSpace(incoming.LiveTranscriptOwnership)
                        ? meeting.Audio.LiveTranscriptOwnership
                        : incoming.LiveTranscriptOwnership,
                    FinalTranscriptOwnerModelId = string.IsNullOrWhiteSpace(incoming.FinalTranscriptOwnerModelId)
                        ? meeting.Audio.FinalTranscriptOwnerModelId
                        : incoming.FinalTranscriptOwnerModelId,
                    GapRecoveryModelId = incoming.GapRecoveryModelId ?? meeting.Audio.GapRecoveryModelId
                },
            AutomationResultJson = meeting.AutomationResultJson
                ?? (incoming.AutomationResult is null
                    ? null
                    : JsonSerializer.Serialize(incoming.AutomationResult, JsonOptions)),
            SourceSchemaVersion = meeting.SourceSchemaVersion,
            CreatedAtUtc = meeting.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public static MeetingRecord ToHead(
        PersistedMeeting meeting,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? automationJson) =>
        new()
        {
            Id = meeting.Id,
            Title = meeting.Title ?? "",
            TitleIsManual = meeting.TitleIsManual,
            CreatedAtUtc = createdAt,
            UpdatedAtUtc = updatedAt,
            DurationMs = meeting.DurationMs,
            ModelProfile = meeting.ModelProfile ?? "",
            FolderId = string.IsNullOrWhiteSpace(meeting.FolderId) ? null : meeting.FolderId,
            WordCount = meeting.WordCount,
            TemplateName = meeting.TemplateName ?? "",
            SessionState = meeting.SessionState,
            RecoveredFromInterruption = meeting.RecoveredFromInterruption,
            HealthWarnings = meeting.HealthWarnings
                .Where(warning => !string.IsNullOrEmpty(warning))
                .ToList(),
            Audio = new MeetingAudioOwnership
            {
                SourcePath = meeting.SourcePath ?? "",
                MicrophoneAudioPath = meeting.MicrophoneAudioPath,
                SystemAudioPath = meeting.SystemAudioPath,
                SystemCaptureMode = meeting.SystemCaptureMode ?? "",
                LivePreviewModelId = meeting.LivePreviewModelId,
                LiveTranscriptOwnership = meeting.LiveTranscriptOwnership ?? "off",
                FinalTranscriptOwnerModelId = meeting.FinalTranscriptOwnerModelId ?? "",
                GapRecoveryModelId = meeting.GapRecoveryModelId
            },
            AutomationResultJson = automationJson,
            SourceSchemaVersion = meeting.SchemaVersion == 0
                ? meeting.SchemaVersion
                : AppDataStore.CurrentMeetingSchemaVersion
        };

    public static PersistedMeetingFolder ToPersisted(FolderRecord folder) =>
        new(folder.Id, folder.Name);

    public static PersistedMeetingTemplate ToPersisted(TemplateRecord template) =>
        new()
        {
            Id = template.Id,
            Name = template.Name,
            Prompt = template.Prompt,
            Icon = template.Icon
        };

    public static TemplateRecord ToRecord(PersistedMeetingTemplate template, int sortOrder, TemplateRecord? existing)
    {
        var now = DateTimeOffset.UtcNow;
        return new TemplateRecord
        {
            Id = template.Id,
            Name = template.Name ?? "",
            Prompt = template.Prompt ?? "",
            Icon = template.Icon ?? "",
            SortOrder = sortOrder,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };
    }

    public static string DisplayedTranscript(MeetingDetail detail) =>
        detail.Transcript(MeetingTranscriptKind.Edited)
        ?? detail.Transcript(MeetingTranscriptKind.Raw)
        ?? "";

    private static List<MeetingNote> Notes(PersistedMeeting meeting)
    {
        var notes = new List<MeetingNote>(2);
        if (!string.IsNullOrEmpty(meeting.Summary))
        {
            notes.Add(new MeetingNote
            {
                MeetingId = meeting.Id,
                Kind = MeetingNoteKind.Generated,
                Content = meeting.Summary
            });
        }

        if (!string.IsNullOrEmpty(meeting.ManualNotes))
        {
            notes.Add(new MeetingNote
            {
                MeetingId = meeting.Id,
                Kind = MeetingNoteKind.Manual,
                Content = meeting.ManualNotes
            });
        }

        return notes;
    }

    private static List<SpeakerAliasRecord> Aliases(PersistedMeeting meeting) =>
        meeting.SpeakerAliases
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => new SpeakerAliasRecord
            {
                MeetingId = meeting.Id,
                SpeakerKey = pair.Key,
                Alias = pair.Value ?? ""
            })
            .ToList();

    private static PostMeetingAutomationResult? DeserializeAutomation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PostMeetingAutomationResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
