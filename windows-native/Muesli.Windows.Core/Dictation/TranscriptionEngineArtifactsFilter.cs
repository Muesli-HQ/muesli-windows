using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS TranscriptionEngineArtifactsFilter.swift: strips model control tokens and
/// non-speech annotations (e.g. <c>[BLANK_AUDIO]</c>) and prompt leakage, keeping real speech.
/// </summary>
public static class TranscriptionEngineArtifactsFilter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Only decorated, known non-speech labels are cues. Bare words ("cheers", "music", "crickets")
    // and arbitrary parenthetical/Markdown prose survive.
    private static readonly HashSet<string> CueLabels =
    [
        "blank audio", "no speech", "silence", "inaudible", "unintelligible",
        "crickets", "crickets chirping", "chirping", "cheers", "cheering",
        "crowd cheering", "audience cheering", "applause", "clapping",
        "laughter", "laughing", "audience laughing", "chuckles", "chuckling",
        "screaming", "sighs", "sighing", "coughs", "coughing",
        "music", "music playing", "background music", "instrumental music",
        "background noise", "noise", "crowd noise", "audience noise",
        "cheers and applause", "cheering and applause", "applause and cheering",
        "laughter and applause",
    ];

    private static readonly Regex DecoratedCue = new(
        @"\[[^\[\]]*\]|\([^()]*\)|<[^<>]*>|(?<!\*)\*\*(?!\*)[^*]+\*\*(?!\*)|(?<!\*)\*(?!\*)[^*]+\*(?!\*)");

    private static readonly Regex UnfinishedCue = new(@"[\[(<][^\[\]()<>*]*$|\*{1,2}[^*\[\]()<>]*\*?$");

    private static readonly Regex[] Artifacts =
    [
        new(@"\[\s*(?:blank[_\s-]*audio|no[_\s-]*speech|silence|inaudible|music|applause|laughter|screaming|cheering)\s*\]", Options),
        new(@"[\[(<]\s*(?:speaking\s+in\s+)?(?:a\s+)?foreign\s+language\s*[\])>]", Options),
        new(@"<\s*(?:eou|eob|unk|blank|pad)\s*>", Options),
        // Any <|...|> control token (Whisper/Cohere), e.g. <|nocaptions|>, <|nospeech|>, <|no|>.
        new(@"<\|[^|<>]*\|>", Options),
    ];

    private static readonly string[] PromptLeaks =
    [
        @"\btranscribe the spoken audio accurately\.?",
        @"\bif a word is unclear,?\s*use the most likely word that fits well within the context of the overall sentence(?:\s+transcription)?\.?",
    ];

    private static string CueBody(string text) =>
        Regex.Replace(text.Trim('[', ']', '(', ')', '<', '>', '*').ToLowerInvariant(), @"[_\s-]+", " ")
            .Trim().Trim('.', '!').Trim();

    private static string StripCues(string text, bool holdUnfinished, bool removeComplete = true)
    {
        var complete = DecoratedCue.Matches(text);
        var end = text.Length;
        if (holdUnfinished && UnfinishedCue.Match(text) is { Success: true } candidate &&
            // A closing star from ordinary Markdown is not a new cue opener.
            !complete.Any(match => candidate.Index >= match.Index && candidate.Index < match.Index + match.Length))
        {
            var body = CueBody(candidate.Value);
            if (body.Length == 0 || CueLabels.Any(label => label.StartsWith(body, StringComparison.Ordinal)))
                end = candidate.Index;
        }
        if (!removeComplete) return text[..end];
        var result = new System.Text.StringBuilder(text[..end]);
        foreach (var match in complete.Reverse())
        {
            if (match.Index + match.Length <= end && CueLabels.Contains(CueBody(match.Value)))
                result.Remove(match.Index, match.Length).Insert(match.Index, " ");
        }
        return result.ToString();
    }

    /// <summary>
    /// macOS applyToCommittedText. Presentation only: raw words stay available to the streaming
    /// aligner, and holding an unfinished cue avoids first inserting then deleting it.
    /// </summary>
    public static string ApplyToCommittedText(string text) => NormalizeSpacing(StripCues(text, holdUnfinished: true));

    /// <summary>
    /// macOS f1c723f0: meeting transcripts are built from segments, so each segment gets the same cleanup as the
    /// joined text; segments left empty are dropped.
    /// </summary>
    public static TranscriptionResult Apply(TranscriptionResult result)
    {
        var text = Apply(result.Text ?? "");
        var segments = string.IsNullOrWhiteSpace(text) || result.Segments is null
            ? (result.Segments is null ? null : [])
            : result.Segments.Select(segment => segment with { Text = Apply(segment.Text ?? "") })
                .Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).ToList();
        return result with { Text = text, Segments = segments };
    }

    public static string Apply(string text)
    {
        var stripped = StripCues(text, holdUnfinished: false);
        var removedArtifact = stripped != text;
        stripped = stripped.Trim();
        foreach (var artifact in Artifacts)
        {
            var filtered = artifact.Replace(stripped, " ");
            removedArtifact |= filtered != stripped;
            stripped = filtered;
        }

        // Streaming decoders can prefix an annotation with a speaker marker; drop it with it.
        if (removedArtifact) stripped = Regex.Replace(stripped, @"^\s*(?:>>|>|»)+\s*", "");
        if (stripped.Length == 0) return stripped;

        foreach (var leak in PromptLeaks)
        {
            stripped = Regex.Replace(stripped, $@"^\s*(?:{leak})(?:\s+|$)", " ", Options);
            stripped = Regex.Replace(stripped, $@"(?:^|\s+)(?:{leak})\s*$", " ", Options);
        }

        return NormalizeSpacing(stripped);
    }

    private static string NormalizeSpacing(string text)
    {
        text = Regex.Replace(text, @"\s{2,}", " ");
        text = Regex.Replace(text, @"\s+([,.;:!?])", "$1");
        return text.Trim();
    }
}

/// <summary>
/// Port of macOS DictationPasteSpacing.swift: a trailing space after a finished sentence so
/// back-to-back dictations do not run together. Presentation only; saved text is unchanged.
/// </summary>
public static class DictationPasteSpacing
{
    public static string TrailingSeparator(string text)
    {
        if (text.Length == 0 || char.IsWhiteSpace(text[^1])) return "";
        var end = text.Length;
        while (end > 0 && IsClosingPunctuation(text[end - 1])) end--;
        if (end == 0 || !IsSentenceTerminal(text[end - 1])) return "";
        var content = text[..(end - 1)];
        var last = content.LastOrDefault(char.IsLetter);
        if (last == default) last = content.LastOrDefault(char.IsDigit);
        return last == default || IsUnspacedScript(last) ? "" : " ";
    }

    private static bool IsClosingPunctuation(char c) =>
        c is '"' or '\'' ||
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.ClosePunctuation
            or System.Globalization.UnicodeCategory.FinalQuotePunctuation;

    // .NET regex has no \p{Sentence_Terminal}; this covers Latin, CJK full-width, Arabic, Devanagari danda.
    private static bool IsSentenceTerminal(char c) => c is '.' or '!' or '?' or '…' or '。' or '！' or '？' or '؟' or '।' or '॥';

    private static bool IsUnspacedScript(char c) =>
        c is >= '぀' and <= 'ヿ' or >= '㄀' and <= 'ㄯ' or >= '一' and <= '鿿' or >= '㐀' and <= '䶿';
}
