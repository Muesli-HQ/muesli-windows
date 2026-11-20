using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

// Port of macOS MeetingChatModels.swift and MeetingChatPassages.swift (Muesli-HQ/muesli 2f10b23d).
// Meeting IDs are the Windows GUID strings. JSON bodies use the macOS camelCase field names, so the
// SQL json_extract paths ('$.kind', '$.state', '$.originalAnswer', ...) read the same.

public enum MeetingChatSelectionKind { All, Folder, Meetings }

public sealed record MeetingChatSelection(MeetingChatSelectionKind Kind, string? FolderId = null, IReadOnlyList<string>? MeetingIds = null)
{
    public static MeetingChatSelection All { get; } = new(MeetingChatSelectionKind.All);
    public static MeetingChatSelection Folder(string id) => new(MeetingChatSelectionKind.Folder, id);
    public static MeetingChatSelection Meetings(IEnumerable<string> ids) => new(MeetingChatSelectionKind.Meetings, null, ids.ToList());

    [JsonIgnore] public IReadOnlyList<string> Ids => MeetingIds ?? [];

    public bool Equals(MeetingChatSelection? other) =>
        other is not null && Kind == other.Kind && FolderId == other.FolderId && Ids.SequenceEqual(other.Ids);

    public override int GetHashCode() => HashCode.Combine(Kind, FolderId, Ids.Count);
}

public sealed record MeetingChatScope(MeetingChatSelection Selection, DateTimeOffset? StartDate = null, DateTimeOffset? EndDateExclusive = null)
{
    public static MeetingChatScope All { get; } = new(MeetingChatSelection.All);
}

public enum MeetingChatTurnState { Finding, Writing, Completed, Failed, Stopped, Interrupted, SourceDeleted }

public enum MeetingChatSourceKind { Transcript, ManualNotes, GeneratedNotes }

public sealed record MeetingChatTextRange(int Location, int Length);

public sealed record MeetingChatDependency(string MeetingId, string Revision);

public sealed record MeetingChatCitation(
    string SourceKey, string MeetingId, string Title, DateTimeOffset StartDate, MeetingChatSourceKind Kind,
    string Excerpt, MeetingChatTextRange Range, string? Timestamp, string Revision);

public sealed record MeetingChatCoverage(int EligibleMeetingCount, int EvidenceMeetingCount, bool IsPartialRecap);

/// <summary>Derived conversation memory bound to the exact completed prefix it summarizes.</summary>
public sealed record MeetingChatRollingSummary(
    string Text, int ThroughOrdinal, IReadOnlyDictionary<string, string> CoveredTurnFingerprints,
    IReadOnlyList<MeetingChatDependency> Dependencies);

public sealed record MeetingChatSession
{
    public required Guid Id { get; init; }
    public required string Title { get; set; }
    public required MeetingChatScope Scope { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public int ContextStartOrdinal { get; set; }
    /// <summary>Where the thread started; hydrated from normalized membership, never stored in the body.</summary>
    [JsonIgnore] public string? AnchorMeetingId { get; set; }
    /// <summary>Null denotes a legacy title, conservatively preserved as user-authored.</summary>
    public bool? TitleIsAutomatic { get; set; }
    public Guid? GeneratedTitleTurnId { get; set; }
    public MeetingChatRollingSummary? RollingSummary { get; set; }
}

public sealed record MeetingChatTurn
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required int Ordinal { get; init; }
    public required string Question { get; init; }
    public required MeetingChatScope Scope { get; init; }
    public required MeetingChatTurnState State { get; set; }
    public string? OriginalAnswer { get; set; }
    public string? EditableDraft { get; set; }
    public List<MeetingChatCitation> Citations { get; set; } = [];
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? Error { get; set; }
    public MeetingChatCoverage? Coverage { get; set; }
    public bool IsDraft { get; set; }
    public Guid? AttemptId { get; set; }
    public string? ScopeLabel { get; init; }

    [JsonIgnore] public bool IsPending => State is MeetingChatTurnState.Finding or MeetingChatTurnState.Writing;
}

public sealed record MeetingChatSourceSnapshot(
    string MeetingId, string Title, DateTimeOffset StartDate, string? FolderId, string Transcript, string ManualNotes,
    string GeneratedNotes, IReadOnlyList<string> ParticipantNames, string Revision);

public sealed record MeetingChatSourceChoice(string Id, string Title, DateTimeOffset StartDate, string? FolderId);

public sealed record MeetingChatPassage
{
    public required string Id { get; init; }
    public string SourceKey { get; set; } = "";
    public required string MeetingId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartDate { get; init; }
    public required MeetingChatSourceKind Kind { get; init; }
    public required string Excerpt { get; init; }
    public required MeetingChatTextRange Range { get; init; }
    public string? Timestamp { get; init; }
    public required string Revision { get; init; }

    [JsonIgnore] public MeetingChatCitation Citation => new(SourceKey, MeetingId, Title, StartDate, Kind, Excerpt, Range, Timestamp, Revision);
}

/// <summary>Work counters with no meeting content; tests use them to prove warm queries avoid source scans.</summary>
public sealed record MeetingChatRetrievalMetrics
{
    public int SourceSnapshotCount { get; set; }
    public int ReindexedMeetingCount { get; set; }
    public int DecodedCandidateCount { get; set; }
}

public sealed record MeetingChatEvidence(
    MeetingChatScope Scope, IReadOnlyList<MeetingChatPassage> Passages, IReadOnlyList<MeetingChatDependency> Dependencies,
    MeetingChatCoverage Coverage, MeetingChatRetrievalMetrics Metrics);

public sealed class MeetingChatException(string message) : Exception(message)
{
    public static MeetingChatException MissingSession() => new("This chat is no longer available.");
    public static MeetingChatException InvalidResponse() => new("The answer did not include usable meeting references. Please retry.");
    public static MeetingChatException SourceChanged() => new("A source meeting changed while the answer was being written. Please retry.");
    public static MeetingChatException QuestionTooLong() => new("Keep your question within 2,000 characters.");
    public static MeetingChatException PromptTooLarge() => new("This context is too large. Choose fewer meetings or start a new chat.");
}

public static class MeetingChatJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"Empty {typeof(T).Name}.");

    public static string Label(this MeetingChatSourceKind kind) => kind switch
    {
        MeetingChatSourceKind.Transcript => "Transcript",
        MeetingChatSourceKind.ManualNotes => "Written notes",
        _ => "Generated notes"
    };
}

/// <summary>A shared history representation; historical source markers are removed, they are not current evidence.</summary>
public static partial class MeetingChatHistory
{
    public static string PromptBlock(MeetingChatTurn turn) =>
        $"Question: {turn.Question}\nPrior assistant reply (conversation context, not source evidence): {SourceMarker().Replace(turn.OriginalAnswer ?? "", "")}";

    [GeneratedRegex(@"\[\[[^\]]+\]\]")]
    private static partial Regex SourceMarker();
}

public static partial class MeetingChatPassages
{
    private const int MaximumPassageBytes = 1_500;

    public static int PromptByteCount(MeetingChatPassage passage) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(PromptSource(passage), PromptJson)) + 8;

    /// <summary>Unescaped like macOS JSONSerialization, so non-Latin excerpts reach the model readable.</summary>
    internal static JsonSerializerOptions PromptJson { get; } = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One prompt source object, sorted keys as macOS JSONSerialization(.sortedKeys).</summary>
    internal static SortedDictionary<string, string> PromptSource(MeetingChatPassage passage) => new(StringComparer.Ordinal)
    {
        ["id"] = passage.SourceKey, ["meeting_id"] = passage.MeetingId, ["title"] = Utf8Prefix(passage.Title, 300),
        ["date"] = passage.StartDate.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["kind"] = passage.Kind.Label(), ["timestamp"] = passage.Timestamp ?? "", ["excerpt"] = passage.Excerpt
    };

    /// <summary>macOS utf8Prefix: whole grapheme clusters up to a UTF-8 byte limit.</summary>
    public static string Utf8Prefix(string text, int limit)
    {
        var result = new StringBuilder();
        var count = 0;
        foreach (var element in TextElements(text))
        {
            var size = Encoding.UTF8.GetByteCount(element);
            if (count + size > limit) break;
            result.Append(element);
            count += size;
        }
        return result.ToString();
    }

    /// <summary>Transcripts split by line, notes by paragraph; each trimmed and cut at 1,500 UTF-8 bytes on grapheme boundaries.</summary>
    public static List<MeetingChatPassage> Extract(MeetingChatSourceSnapshot source)
    {
        var passages = new List<MeetingChatPassage>();
        foreach (var (kind, text) in new[]
                 {
                     (MeetingChatSourceKind.Transcript, source.Transcript), (MeetingChatSourceKind.ManualNotes, source.ManualNotes),
                     (MeetingChatSourceKind.GeneratedNotes, source.GeneratedNotes)
                 })
        {
            foreach (var (lineStart, lineEnd) in Lines(text))
            {
                var lower = lineStart;
                var upper = lineEnd;
                while (lower < upper && char.IsWhiteSpace(text[lower])) lower++;
                while (upper > lower && char.IsWhiteSpace(text[upper - 1])) upper--;
                if (lower >= upper) continue;
                var line = text[lower..upper];
                string? timestamp = null;
                if (kind == MeetingChatSourceKind.Transcript && TranscriptTimestamp().Match(line) is { Success: true } match)
                    timestamp = match.Value[1..^1];
                var enumerator = StringInfo.GetTextElementEnumerator(text[lower..upper]);
                var start = lower;
                var cursor = lower;
                var bytes = 0;
                while (enumerator.MoveNext())
                {
                    var element = enumerator.GetTextElement();
                    var size = Encoding.UTF8.GetByteCount(element);
                    if (bytes > 0 && bytes + size > MaximumPassageBytes)
                    {
                        passages.Add(Passage(source, kind, text, start, cursor, timestamp));
                        start = cursor;
                        bytes = 0;
                    }
                    bytes += size;
                    cursor += element.Length;
                }
                passages.Add(Passage(source, kind, text, start, cursor, timestamp));
            }
        }
        return passages;
    }

    private static MeetingChatPassage Passage(MeetingChatSourceSnapshot source, MeetingChatSourceKind kind, string text, int start, int end, string? timestamp) => new()
    {
        Id = $"{source.MeetingId}:{JsonNamingPolicy.CamelCase.ConvertName(kind.ToString())}:{start}",
        MeetingId = source.MeetingId, Title = source.Title, StartDate = source.StartDate, Kind = kind,
        Excerpt = text[start..end], Range = new MeetingChatTextRange(start, end - start), Timestamp = timestamp,
        Revision = source.Revision
    };

    /// <summary>Line ranges without terminators: CR, LF, CRLF and the Unicode line/paragraph separators.</summary>
    private static IEnumerable<(int Start, int End)> Lines(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is not ('\n' or '\r' or (char)0x2028 or (char)0x2029 or '\u0085')) continue;
            yield return (start, index);
            if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
        if (start < text.Length) yield return (start, text.Length);
    }

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]")]
    private static partial Regex TranscriptTimestamp();
}
