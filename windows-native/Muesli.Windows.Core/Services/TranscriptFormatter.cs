namespace Muesli.Windows.Services;

public static class TranscriptFormatter
{
    private const int ConsolidationGapThresholdMs = 2000;

    public static string Merge(
        List<TranscriptSegment> micSegments,
        List<TranscriptSegment> systemSegments,
        List<DiarizedSegment> diarizationSegments,
        DateTime meetingStart,
        IReadOnlyList<DiarizedSegment>? micDiarizationSegments = null)
    {
        // macOS: a single mic voice is almost always the device owner; split the mic channel only
        // when the room had several significant voices.
        var localDiarization = SpeakerAttribution.LocalDiarization(micDiarizationSegments);
        if ((diarizationSegments is null || diarizationSegments.Count == 0) && localDiarization is null)
        {
            // Fallback to legacy merge when diarization is unavailable
            return LegacyMerge(micSegments, systemSegments);
        }

        // 1–2. Remote speakers: "Speaker N" in first-appearance order, by total time overlap.
        diarizationSegments ??= [];
        var speakerLabelMap = LabelMap(diarizationSegments, "Speaker");
        var taggedSystem = systemSegments
            .Select(seg => new TaggedSegment(seg, diarizationSegments.Count == 0 ? "Others" : FindSpeaker(seg, diarizationSegments, speakerLabelMap, "Others")))
            .ToList();

        // 3. Mic: "You", or per sentence "Local speaker N", numbered separately from remote speakers.
        var taggedMic = localDiarization is null
            ? micSegments.Select(seg => new TaggedSegment(seg, "You")).ToList()
            : SpeakerAttribution.SentenceSegments(micSegments)
                .Select(seg => new TaggedSegment(seg, FindSpeaker(seg, localDiarization, LabelMap(localDiarization, "Local speaker"), "Local speaker")))
                .ToList();

        // 4. Sort chronologically
        var all = taggedMic.Concat(taggedSystem)
            .OrderBy(t => t.Segment.StartMs)
            .ToList();

        if (all.Count == 0)
        {
            return "";
        }

        // 5. Consolidate consecutive same-speaker segments (within 2s gap)
        var consolidated = Consolidate(all, ConsolidationGapThresholdMs);

        // 6. Format: [HH:mm:ss] Speaker: text
        return string.Join("\n", consolidated.Select(t =>
        {
            var timestamp = meetingStart.AddMilliseconds(t.Segment.StartMs);
            var text = t.Segment.Text.Trim();
            return $"[{timestamp:HH:mm:ss}] {t.Speaker}: {text}";
        }));
    }

    private static string LegacyMerge(List<TranscriptSegment> micSegments, List<TranscriptSegment> systemSegments)
    {
        var parts = new List<string>();

        var micText = string.Join(" ", micSegments.Select(s => s.Text.Trim()));
        if (!string.IsNullOrWhiteSpace(micText))
        {
            parts.Add($"[You] {micText.Trim()}");
        }

        var systemText = string.Join(" ", systemSegments.Select(s => s.Text.Trim()));
        if (!string.IsNullOrWhiteSpace(systemText))
        {
            parts.Add($"[System audio] {systemText.Trim()}");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    /// <summary>Raw diarizer ID → "&lt;prefix&gt; 1", "&lt;prefix&gt; 2", … in first-appearance order.</summary>
    private static Dictionary<string, string> LabelMap(IEnumerable<DiarizedSegment> diarization, string prefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in diarization.OrderBy(segment => segment.StartMs))
            map.TryAdd(segment.SpeakerId, $"{prefix} {map.Count + 1}");
        return map;
    }

    private static string FindSpeaker(TranscriptSegment segment, IReadOnlyList<DiarizedSegment> diarization, Dictionary<string, string> labels, string fallback)
    {
        if (labels.Count == 1) return labels.Values.First();
        return SpeakerAttribution.SpeakerId(segment, diarization) is { } id && labels.TryGetValue(id, out var label) ? label : fallback;
    }

    private static List<TaggedSegment> Consolidate(List<TaggedSegment> segments, int gapThresholdMs)
    {
        var result = new List<TaggedSegment>();
        if (segments.Count == 0)
        {
            return result;
        }

        var currentSpeaker = segments[0].Speaker;
        var currentStartMs = segments[0].Segment.StartMs;
        var currentEndMs = segments[0].Segment.EndMs;
        var currentText = segments[0].Segment.Text;

        for (int i = 1; i < segments.Count; i++)
        {
            var seg = segments[i];
            var gap = Math.Max(0, seg.Segment.StartMs - currentEndMs);

            if (seg.Speaker == currentSpeaker && gap <= gapThresholdMs)
            {
                // Same speaker, temporally close — accumulate text
                currentText = AppendText(currentText, seg.Segment.Text, gap);
                currentEndMs = seg.Segment.EndMs;
            }
            else
            {
                // Different speaker or too far apart — flush current
                result.Add(new TaggedSegment(
                    new TranscriptSegment("", currentSpeaker, currentStartMs, currentEndMs, currentText),
                    currentSpeaker));

                currentSpeaker = seg.Speaker;
                currentStartMs = seg.Segment.StartMs;
                currentEndMs = seg.Segment.EndMs;
                currentText = seg.Segment.Text;
            }
        }

        // Flush final accumulated segment
        result.Add(new TaggedSegment(
            new TranscriptSegment("", currentSpeaker, currentStartMs, currentEndMs, currentText),
            currentSpeaker));

        return result;
    }

    private static string AppendText(string current, string next, int gapMs)
    {
        current = current.Trim();
        next = next.Trim();

        if (string.IsNullOrEmpty(current))
        {
            return next;
        }

        if (string.IsNullOrEmpty(next))
        {
            return current;
        }

        // If there's a significant gap (>1s), treat as separate sentences
        if (gapMs > 1000)
        {
            var separator = current.EndsWith('.') || current.EndsWith('!') || current.EndsWith('?')
                ? " "
                : ". ";
            return current + separator + next;
        }

        // Small gap — just space-separate
        return current + " " + next;
    }

    private sealed record TaggedSegment(TranscriptSegment Segment, string Speaker);
}
