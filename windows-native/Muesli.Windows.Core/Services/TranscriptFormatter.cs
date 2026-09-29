namespace Muesli.Windows.Services;

public static class TranscriptFormatter
{
    private const int ConsolidationGapThresholdMs = 2000;

    public static string Merge(
        List<TranscriptSegment> micSegments,
        List<TranscriptSegment> systemSegments,
        List<DiarizedSegment> diarizationSegments,
        DateTime meetingStart)
    {
        if (diarizationSegments is null || diarizationSegments.Count == 0)
        {
            // Fallback to legacy merge when diarization is unavailable
            return LegacyMerge(micSegments, systemSegments);
        }

        // 1. Map raw diarization speaker IDs → "Speaker 1", "Speaker 2" in first-appearance order
        var speakerLabelMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int nextSpeakerNumber = 1;
        foreach (var seg in diarizationSegments.OrderBy(s => s.StartMs))
        {
            if (!speakerLabelMap.ContainsKey(seg.SpeakerId))
            {
                speakerLabelMap[seg.SpeakerId] = $"Speaker {nextSpeakerNumber++}";
            }
        }

        // 2. Assign speaker labels to system transcript segments by time overlap
        var taggedSystem = systemSegments
            .Select(seg => new TaggedSegment(
                seg,
                FindSpeakerByOverlap(seg, diarizationSegments, speakerLabelMap) ?? "Others"))
            .ToList();

        // 3. Tag mic segments as "You"
        var taggedMic = micSegments
            .Select(seg => new TaggedSegment(seg, "You"))
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

    private static string? FindSpeakerByOverlap(
        TranscriptSegment transcriptSegment,
        List<DiarizedSegment> diarizationSegments,
        Dictionary<string, string> speakerLabelMap)
    {
        // Find diarization segment with maximum time overlap
        DiarizedSegment? bestMatch = null;
        double bestOverlap = 0;

        foreach (var diarSeg in diarizationSegments)
        {
            var overlap = CalculateOverlap(
                transcriptSegment.StartMs, transcriptSegment.EndMs,
                diarSeg.StartMs, diarSeg.EndMs);

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestMatch = diarSeg;
            }
        }

        if (bestMatch is null || bestOverlap <= 0)
        {
            return null;
        }

        return speakerLabelMap.TryGetValue(bestMatch.SpeakerId, out var label)
            ? label
            : "Others";
    }

    private static double CalculateOverlap(int startA, int endA, int startB, int endB)
    {
        var overlapStart = Math.Max(startA, startB);
        var overlapEnd = Math.Min(endA, endB);
        return Math.Max(0, overlapEnd - overlapStart);
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
