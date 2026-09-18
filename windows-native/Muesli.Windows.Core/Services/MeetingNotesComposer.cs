using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Deterministic meeting titles derived from the transcript, with manual titles held sacred.
/// </summary>
public static class MeetingTitleService
{
    public const int MaxTitleLength = 72;
    private static readonly Regex SpeakerPrefix = new(
        @"^\s*(?:\[\d{2}:\d{2}:\d{2}\]\s*)?(?:\[[^\]]+\]|[^:\r\n]{1,40}):\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static readonly string[] Filler =
        ["um", "uh", "okay", "ok", "so", "yeah", "right", "hello", "hi", "hey"];

    /// <summary>
    /// Builds a title from the first substantive sentence of the transcript. Deterministic, local,
    /// and never a network call, so a meeting always gets a usable title even with no provider.
    /// </summary>
    public static string Generate(string? transcript, DateTime recordedAt, string? fallback = null)
    {
        var candidate = FirstSubstantiveSentence(transcript);
        if (!string.IsNullOrWhiteSpace(candidate)) return Trim(candidate);
        if (!string.IsNullOrWhiteSpace(fallback)) return Trim(fallback!);
        return $"Meeting {recordedAt:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Chooses the title to persist. A manual title always wins; a generated one is only applied
    /// when the user has not taken ownership of the title.
    /// </summary>
    public static string Resolve(string? currentTitle, bool titleIsManual, string generated)
    {
        if (titleIsManual && !string.IsNullOrWhiteSpace(currentTitle)) return currentTitle!;
        return string.IsNullOrWhiteSpace(generated) ? currentTitle ?? "" : generated;
    }

    public static bool IsAcceptableManualTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Trim().Length <= 200;

    private static string? FirstSubstantiveSentence(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        foreach (var rawLine in transcript.Replace("\r\n", "\n").Split('\n'))
        {
            var line = SpeakerPrefix.Replace(rawLine, "").Trim();
            if (line.Length == 0) continue;
            var sentence = line.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            var words = Whitespace.Split(sentence).Where(word => word.Length > 0).ToList();
            // A line of greetings and filler is not what the meeting was about.
            if (words.Count < 3) continue;
            if (words.All(word => Filler.Contains(word.Trim(',', '.', '!', '?').ToLowerInvariant()))) continue;
            return sentence;
        }
        return null;
    }

    private static string Trim(string value)
    {
        var normalized = Whitespace.Replace(value.Trim(), " ");
        if (normalized.Length <= MaxTitleLength) return normalized;
        var cut = normalized[..MaxTitleLength];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace > 24 ? cut[..lastSpace] : cut).TrimEnd(',', ';', ':', '-') + "…";
    }
}

public sealed record MeetingNotesDocument(string GeneratedNotes, string ManualNotes)
{
    public const string ManualHeading = "## My notes";

    /// <summary>What the user reads: generated notes first, their own writing kept clearly separate.</summary>
    public string Rendered
    {
        get
        {
            var generated = GeneratedNotes?.Trim() ?? "";
            var manual = ManualNotes?.Trim() ?? "";
            if (manual.Length == 0) return generated;
            if (generated.Length == 0) return $"{ManualHeading}{Environment.NewLine}{manual}";
            var builder = new StringBuilder(generated);
            builder.Append(Environment.NewLine).Append(Environment.NewLine);
            builder.Append(ManualHeading).Append(Environment.NewLine).Append(manual);
            return builder.ToString();
        }
    }
}

/// <summary>
/// Applies a regenerated summary to an existing meeting.
///
/// The one invariant that matters: re-summarization replaces generated notes only. Manual notes and
/// a manually chosen title are user-authored content and are carried across untouched, because a
/// regeneration the user triggered for the notes must never silently destroy their own writing.
/// </summary>
public static class MeetingNotesComposer
{
    public static PersistedMeeting ApplyResummarization(
        PersistedMeeting meeting,
        string regeneratedNotes,
        string templateName,
        string? regeneratedTitle = null)
    {
        var title = MeetingTitleService.Resolve(
            meeting.Title,
            meeting.TitleIsManual,
            string.IsNullOrWhiteSpace(regeneratedTitle) ? meeting.Title : regeneratedTitle!);

        return meeting with
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Summary = regeneratedNotes ?? "",
            // Deliberately untouched by regeneration.
            ManualNotes = meeting.ManualNotes,
            TitleIsManual = meeting.TitleIsManual,
            Title = title,
            TemplateName = string.IsNullOrWhiteSpace(templateName) ? meeting.TemplateName : templateName
        };
    }

    public static PersistedMeeting ApplyManualTitle(PersistedMeeting meeting, string? title) =>
        MeetingTitleService.IsAcceptableManualTitle(title)
            ? meeting with
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Title = title!.Trim(),
                TitleIsManual = true
            }
            : meeting;

    public static PersistedMeeting ApplyManualNotes(PersistedMeeting meeting, string? notes) =>
        meeting with
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            ManualNotes = notes?.Trim() ?? ""
        };

    public static MeetingNotesDocument Document(PersistedMeeting meeting) =>
        new(meeting.Summary ?? "", meeting.ManualNotes ?? "");
}
