using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS SpeakerAttribution (2f10b23d): matches transcript segments to diarizer speakers by
/// time. The formatter labels with it, so remote and in-room attribution follow one rule.
/// </summary>
public static partial class SpeakerAttribution
{
    public const int NearestSpeakerMaxGapMs = 2_000;

    /// <summary>Minimum evidence for a separate in-room identity. No fraction of the meeting is
    /// required: brief guests stay legitimate speakers even in long meetings.</summary>
    public const int MinimumLocalSpeakerMs = 5_000;

    /// <summary>Diarizer IDs that spoke long enough to count as a distinct in-room voice.</summary>
    public static IReadOnlySet<string> SignificantSpeakerIds(IEnumerable<DiarizedSegment> segments)
    {
        var durations = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var segment in segments)
            durations[segment.SpeakerId] = durations.GetValueOrDefault(segment.SpeakerId) + Math.Max(0, segment.EndMs - segment.StartMs);
        return durations.Where(pair => pair.Value >= MinimumLocalSpeakerMs).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The single "was the room really multi-speaker" decision for the mic channel: null when
    /// it had one significant voice (the mic stays "You"), otherwise only the significant speakers.</summary>
    public static IReadOnlyList<DiarizedSegment>? LocalDiarization(IReadOnlyList<DiarizedSegment>? micDiarization)
    {
        if (micDiarization is null) return null;
        var speakers = SignificantSpeakerIds(micDiarization);
        return speakers.Count > 1 ? micDiarization.Where(segment => speakers.Contains(segment.SpeakerId)).ToList() : null;
    }

    /// <summary>The speaker with the largest total overlap (a turn spanning A–B–A belongs to A), falling
    /// back to the nearest diarized speech within two seconds. Ties break by speaker ID.</summary>
    public static string? SpeakerId(TranscriptSegment segment, IReadOnlyList<DiarizedSegment> diarization)
    {
        var start = segment.StartMs;
        var end = Math.Max(segment.EndMs, segment.StartMs + 100);
        var overlap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var speech in diarization)
        {
            var shared = Math.Min(end, speech.EndMs) - Math.Max(start, speech.StartMs);
            if (shared > 0) overlap[speech.SpeakerId] = overlap.GetValueOrDefault(speech.SpeakerId) + shared;
        }
        if (overlap.Count > 0)
            return overlap.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

        var midpoint = (start + end) / 2.0;
        var nearest = diarization.MinBy(speech => Gap(midpoint, speech));
        return nearest is not null && Gap(midpoint, nearest) <= NearestSpeakerMaxGapMs ? nearest.SpeakerId : null;
    }

    private static double Gap(double point, DiarizedSegment speech) =>
        point < speech.StartMs ? speech.StartMs - point : point > speech.EndMs ? point - speech.EndMs : 0;

    /// <summary>macOS MicTurnNormalizer.sentenceSegments: with several local speakers, attribute per
    /// sentence. Time is shared out by visible character count.</summary>
    public static IReadOnlyList<TranscriptSegment> SentenceSegments(IEnumerable<TranscriptSegment> segments) =>
        segments.SelectMany(SentenceSplit).ToList();

    private static IEnumerable<TranscriptSegment> SentenceSplit(TranscriptSegment segment)
    {
        var end = Math.Max(segment.EndMs, segment.StartMs + 100);
        var units = SentenceBoundary().Split(segment.Text.Trim()).Where(unit => unit.Trim().Length > 0).Select(unit => unit.Trim()).ToList();
        if (units.Count <= 1)
        {
            yield return segment;
            yield break;
        }
        var weights = units.Select(unit => unit.Count(character => !char.IsWhiteSpace(character))).ToList();
        var total = Math.Max(weights.Sum(), 1);
        var duration = Math.Max(end - segment.StartMs, 100);
        var cursor = segment.StartMs;
        for (var index = 0; index < units.Count; index++)
        {
            var length = index == units.Count - 1 ? Math.Max(end - cursor, 50) : Math.Max((int)(duration * (double)weights[index] / total), 50);
            var unitEnd = Math.Min(end, cursor + length);
            yield return segment with { StartMs = cursor, EndMs = Math.Max(unitEnd, cursor), Text = units[index] };
            cursor = unitEnd;
        }
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBoundary();
}
