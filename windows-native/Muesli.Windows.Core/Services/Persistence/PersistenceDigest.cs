using System.Security.Cryptography;
using System.Text;

namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Content hashes over records, used to prove a migration moved the history rather than merely
/// counting it.
/// <para>
/// The same projection is computed from the JSON source and again from the imported rows, so the
/// comparison catches a truncated transcript, a dropped manual note, a shifted timestamp or a lost
/// audio path — every one of which a row count would happily call a success. Fields the source does
/// not carry, such as when a note row was written, are excluded: they are properties of the import,
/// not of the user's data.
/// </para>
/// </summary>
public static class PersistenceDigest
{
    private const char FieldSeparator = '\u001f';
    private const char ItemSeparator = '\u001e';
    private const string Null = "\u0000null";

    public static string OfDictations(IEnumerable<DictationRecord> dictations) =>
        Hash(dictations
            .OrderBy(dictation => dictation.Id, StringComparer.Ordinal)
            .Select(Canonical));

    public static string OfMeetings(IEnumerable<MeetingDetail> meetings) =>
        Hash(meetings
            .OrderBy(meeting => meeting.Meeting.Id, StringComparer.Ordinal)
            .Select(Canonical));

    public static string OfFolders(IEnumerable<FolderRecord> folders) =>
        Hash(folders
            .OrderBy(folder => folder.Id, StringComparer.Ordinal)
            .Select(Canonical));

    public static string OfTemplates(IEnumerable<TemplateRecord> templates) =>
        Hash(templates
            .OrderBy(template => template.Id, StringComparer.Ordinal)
            .Select(Canonical));

    public static string Combine(params string[] digests) => Hash(digests);

    internal static string Canonical(DictationRecord dictation) =>
        Fields(
            "dictation",
            dictation.Id,
            Ticks(dictation.CreatedAtUtc),
            Ticks(dictation.UpdatedAtUtc),
            dictation.Title,
            dictation.Text,
            dictation.DurationMs.ToString(),
            dictation.ModelProfile,
            dictation.FolderId,
            dictation.WordCount.ToString(),
            dictation.AudioPath,
            dictation.AudioRetained ? "1" : "0");

    internal static string Canonical(MeetingDetail detail)
    {
        var meeting = detail.Meeting;
        var head = Fields(
            "meeting",
            meeting.Id,
            meeting.Title,
            meeting.TitleIsManual ? "1" : "0",
            Ticks(meeting.CreatedAtUtc),
            Ticks(meeting.UpdatedAtUtc),
            meeting.DurationMs.ToString(),
            meeting.ModelProfile,
            meeting.FolderId,
            meeting.WordCount.ToString(),
            meeting.TemplateName,
            meeting.SessionState.ToString(),
            meeting.RecoveredFromInterruption ? "1" : "0",
            string.Join(ItemSeparator, meeting.HealthWarnings),
            meeting.Audio.SourcePath,
            meeting.Audio.MicrophoneAudioPath,
            meeting.Audio.SystemAudioPath,
            meeting.Audio.SystemCaptureMode,
            meeting.Audio.LivePreviewModelId,
            meeting.Audio.LiveTranscriptOwnership,
            meeting.Audio.FinalTranscriptOwnerModelId,
            meeting.Audio.GapRecoveryModelId,
            meeting.AutomationResultJson,
            meeting.SourceSchemaVersion.ToString());

        var notes = detail.Notes
            .OrderBy(note => note.Kind)
            .Select(note => Fields("note", note.Kind.ToString(), note.Content));
        var transcripts = detail.Transcripts
            .OrderBy(transcript => transcript.Kind)
            .Select(transcript => Fields("transcript", transcript.Kind.ToString(), transcript.Content));
        var aliases = detail.SpeakerAliases
            .OrderBy(alias => alias.SpeakerKey, StringComparer.Ordinal)
            .Select(alias => Fields("alias", alias.SpeakerKey, alias.Alias));
        var followUps = detail.FollowUps
            .OrderBy(followUp => followUp.Id, StringComparer.Ordinal)
            .Select(followUp => Fields(
                "followUp",
                followUp.Id,
                followUp.Text,
                followUp.Owner,
                followUp.DueAtUtc is null ? Null : Ticks(followUp.DueAtUtc.Value),
                followUp.Status.ToString(),
                followUp.LinkedMeetingId,
                followUp.LinkedDictationId));

        return string.Join(
            ItemSeparator,
            new[] { head }.Concat(notes).Concat(transcripts).Concat(aliases).Concat(followUps));
    }

    /// <summary>
    /// Folders and templates hash without their timestamps. The JSON history stores none for them,
    /// so those columns describe when the row was written rather than anything the user owns.
    /// Hashing them would make an unrelated edit elsewhere in the history look like folder
    /// corruption, and would stop an unchanged history from being recognised as already imported.
    /// </summary>
    internal static string Canonical(FolderRecord folder) =>
        Fields(
            "folder",
            folder.Id,
            folder.Name,
            folder.ParentId,
            folder.SortOrder.ToString(),
            folder.Metadata);

    internal static string Canonical(TemplateRecord template) =>
        Fields(
            "template",
            template.Id,
            template.Name,
            template.Prompt,
            template.Icon,
            template.SortOrder.ToString());

    private static string Fields(params string?[] values) =>
        string.Join(FieldSeparator, values.Select(value => value ?? Null));

    private static string Ticks(DateTimeOffset value) =>
        PersistenceTime.ToStorage(value).ToString();

    private static string Hash(IEnumerable<string> lines)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var separator = Encoding.UTF8.GetBytes("\n");
        foreach (var line in lines)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(line));
            hash.AppendData(separator);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
