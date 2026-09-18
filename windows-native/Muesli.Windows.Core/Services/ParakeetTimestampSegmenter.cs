using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

internal static partial class ParakeetTimestampSegmenter
{
    private const int PauseBoundaryMs = 700;
    private const int MaximumSegmentDurationMs = 15_000;
    private const int MaximumTokenDurationMs = 2_000;
    private const int MaximumTokensPerSegment = 64;
    private const int DefaultTokenDurationMs = 80;

    public static ParakeetSegmentationResult Build(
        string title,
        string text,
        IReadOnlyList<string>? tokens,
        IReadOnlyList<float>? timestamps,
        IReadOnlyList<float>? durations,
        int audioDurationMs)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParakeetSegmentationResult([], "no speech", 0);
        }

        var speaker = title.Contains("system", StringComparison.OrdinalIgnoreCase)
            ? "System audio"
            : "You";
        var alignedTokenCount = Math.Min(tokens?.Count ?? 0, timestamps?.Count ?? 0);
        if (alignedTokenCount == 0)
        {
            return new ParakeetSegmentationResult(
                BuildProportionalFallback(text, speaker, audioDurationMs),
                "sentence-proportional fallback (model returned no aligned tokens)",
                0);
        }

        var segments = BuildTokenAlignedSegments(
            tokens!,
            timestamps!,
            durations,
            alignedTokenCount,
            speaker,
            audioDurationMs);
        if (segments.Count == 0)
        {
            return new ParakeetSegmentationResult(
                BuildProportionalFallback(text, speaker, audioDurationMs),
                "sentence-proportional fallback (aligned tokens were empty)",
                alignedTokenCount);
        }

        return new ParakeetSegmentationResult(
            segments,
            "token-aligned sentence and pause boundaries",
            alignedTokenCount);
    }

    private static List<TranscriptSegment> BuildTokenAlignedSegments(
        IReadOnlyList<string> tokens,
        IReadOnlyList<float> timestamps,
        IReadOnlyList<float>? durations,
        int alignedTokenCount,
        string speaker,
        int audioDurationMs)
    {
        var audioEndMs = Math.Max(1, audioDurationMs);
        var segments = new List<TranscriptSegment>();
        var textBuilder = new StringBuilder();
        var segmentStartMs = -1;
        var segmentEndMs = -1;
        var segmentTokenCount = 0;

        for (var index = 0; index < alignedTokenCount; index++)
        {
            var token = tokens[index] ?? "";
            var tokenStartMs = ClampTimestamp(timestamps[index], audioEndMs);
            var tokenEndMs = ResolveTokenEndMs(
                index,
                tokenStartMs,
                timestamps,
                durations,
                alignedTokenCount,
                audioEndMs);

            if (segmentStartMs < 0)
            {
                segmentStartMs = tokenStartMs;
            }

            segmentEndMs = Math.Max(segmentEndMs, tokenEndMs);
            textBuilder.Append(token);
            segmentTokenCount++;

            var nextStartMs = index + 1 < alignedTokenCount
                ? ClampTimestamp(timestamps[index + 1], audioEndMs)
                : audioEndMs;
            var pauseAfterTokenMs = Math.Max(0, nextStartMs - tokenEndMs);
            var segmentDurationMs = Math.Max(1, segmentEndMs - segmentStartMs);
            var hasSentenceBoundary = HasTerminalPunctuation(token);
            var hasPauseBoundary = pauseAfterTokenMs >= PauseBoundaryMs;
            var exceededDuration = segmentDurationMs >= MaximumSegmentDurationMs;
            var exceededTokenCount = segmentTokenCount >= MaximumTokensPerSegment;

            if (hasSentenceBoundary ||
                hasPauseBoundary ||
                exceededDuration ||
                exceededTokenCount ||
                index == alignedTokenCount - 1)
            {
                AddSegment(
                    segments,
                    speaker,
                    segmentStartMs,
                    Math.Max(segmentStartMs + 1, segmentEndMs),
                    textBuilder.ToString());
                textBuilder.Clear();
                segmentStartMs = -1;
                segmentEndMs = -1;
                segmentTokenCount = 0;
            }
        }

        return segments;
    }

    private static int ResolveTokenEndMs(
        int index,
        int tokenStartMs,
        IReadOnlyList<float> timestamps,
        IReadOnlyList<float>? durations,
        int alignedTokenCount,
        int audioEndMs)
    {
        var durationMs = 0;
        if (durations is not null && index < durations.Count)
        {
            durationMs = SecondsToMilliseconds(durations[index]);
        }

        if (durationMs <= 0 && index + 1 < alignedTokenCount)
        {
            durationMs = ClampTimestamp(timestamps[index + 1], audioEndMs) - tokenStartMs;
        }

        if (durationMs <= 0)
        {
            durationMs = DefaultTokenDurationMs;
        }

        durationMs = Math.Min(durationMs, MaximumTokenDurationMs);
        if (tokenStartMs >= audioEndMs)
        {
            return audioEndMs;
        }

        return Math.Clamp(tokenStartMs + durationMs, tokenStartMs + 1, audioEndMs);
    }

    private static List<TranscriptSegment> BuildProportionalFallback(
        string text,
        string speaker,
        int audioDurationMs)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count == 0)
        {
            return [];
        }

        var audioEndMs = Math.Max(1, audioDurationMs);
        var totalWeight = Math.Max(1, sentences.Sum(sentence => sentence.Length));
        var consumedWeight = 0;
        var segments = new List<TranscriptSegment>(sentences.Count);

        for (var index = 0; index < sentences.Count; index++)
        {
            var startMs = (int)Math.Round(audioEndMs * (consumedWeight / (double)totalWeight));
            consumedWeight += sentences[index].Length;
            var endMs = index == sentences.Count - 1
                ? audioEndMs
                : (int)Math.Round(audioEndMs * (consumedWeight / (double)totalWeight));
            AddSegment(segments, speaker, startMs, Math.Max(startMs + 1, endMs), sentences[index]);
        }

        return segments;
    }

    private static List<string> SplitSentences(string text)
    {
        var normalized = WhitespaceRegex().Replace(text.Trim(), " ");
        var sentences = new List<string>();
        var builder = new StringBuilder();

        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            builder.Append(character);
            var terminal = IsTerminalPunctuation(character);
            var longEnough = builder.Length >= 240 && char.IsWhiteSpace(character);
            if (!terminal && !longEnough)
            {
                continue;
            }

            var sentence = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                sentences.Add(sentence);
            }

            builder.Clear();
        }

        var remainder = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(remainder))
        {
            sentences.Add(remainder);
        }

        return sentences;
    }

    private static void AddSegment(
        ICollection<TranscriptSegment> segments,
        string speaker,
        int startMs,
        int endMs,
        string text)
    {
        var normalizedText = WhitespaceRegex().Replace(text.Trim(), " ");
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return;
        }

        if (segments.Count > 0)
        {
            startMs = Math.Max(startMs, segments.Last().EndMs);
        }

        segments.Add(new TranscriptSegment(
            $"parakeet_seg_{segments.Count}",
            speaker,
            Math.Max(0, startMs),
            Math.Max(startMs + 1, endMs),
            normalizedText));
    }

    private static bool HasTerminalPunctuation(string token)
    {
        var trimmed = token.AsSpan().TrimEnd();
        for (var index = trimmed.Length - 1; index >= 0; index--)
        {
            var character = trimmed[index];
            if (character is '"' or '\'' or '’' or '”' or ')' or ']' or '}')
            {
                continue;
            }

            return IsTerminalPunctuation(character);
        }

        return false;
    }

    private static bool IsTerminalPunctuation(char character)
    {
        return character is '.' or '!' or '?' or '。' or '！' or '？';
    }

    private static int ClampTimestamp(float seconds, int audioEndMs)
    {
        return Math.Clamp(SecondsToMilliseconds(seconds), 0, audioEndMs);
    }

    private static int SecondsToMilliseconds(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0)
        {
            return 0;
        }

        return (int)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

internal sealed record ParakeetSegmentationResult(
    List<TranscriptSegment> Segments,
    string Mode,
    int TimestampedTokenCount);
