using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS TranscriptionEngineArtifactsFilter.swift: strips model control tokens and
/// non-speech annotations (e.g. <c>[BLANK_AUDIO]</c>) and prompt leakage, keeping real speech.
/// </summary>
public static class TranscriptionEngineArtifactsFilter
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Artifacts =
    [
        new(@"\[\s*(?:blank[_\s-]*audio|no[_\s-]*speech|silence|inaudible|music|applause|laughter|screaming|cheering)\s*\]", Options),
        new(@"[\[(<]\s*(?:speaking\s+in\s+)?(?:a\s+)?foreign\s+language\s*[\])>]", Options),
        new(@"<\s*(?:eou|eob|unk|blank|pad)\s*>", Options),
        new(@"<\|\s*(?:endoftext|nospeech|notimestamp|nodiarize|noitn|pnc|startofcontext|startoftranscript)\s*\|>", Options),
    ];

    private static readonly string[] PromptLeaks =
    [
        @"\btranscribe the spoken audio accurately\.?",
        @"\bif a word is unclear,?\s*use the most likely word that fits well within the context of the overall sentence(?:\s+transcription)?\.?",
    ];

    public static string Apply(string text)
    {
        var stripped = text.Trim();
        var removedArtifact = false;
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

        stripped = Regex.Replace(stripped, @"\s{2,}", " ");
        stripped = Regex.Replace(stripped, @"\s+([,.;:!?])", "$1");
        return stripped.Trim();
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
