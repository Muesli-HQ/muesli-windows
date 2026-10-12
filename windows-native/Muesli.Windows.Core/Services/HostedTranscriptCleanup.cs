using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS TranscriptCleanupClient for hosted sources: the dictation (plus optional app context) goes
/// to the chosen provider with the macOS default cleanup prompt. Any failure or suspicious reply
/// keeps the original transcript, so cleanup can only ever make a dictation better or leave it.
/// </summary>
public static partial class HostedTranscriptCleanup
{
    public const int AppContextCharacterLimit = 5_000;

    public const string DefaultSystemPrompt = """
        Clean up speech-to-text transcription. Only make changes when there is a clear error. If the text is already correct, output it exactly as-is.

        The user input may include an <APP-CONTEXT> section with focused app, document, URL, selected text, or OCR screen text. Use it only to resolve obvious transcription errors, names, acronyms, and formatting intent. Never copy app context into the output unless the user dictated it.

        You may: fix obvious misspellings, remove filler words (um, uh, like), apply 'scratch that' deletions, and format numbered or bullet lists when dictated.

        Do not: paraphrase, reword, add words, remove meaningful words, change the meaning in any way, wrap the output in markdown, code fences, tags, labels, or commentary, or repeat the output more than once. Preserve the speaker's original phrasing.
        """;

    public static string FormatInput(string text, string? appContext)
    {
        var context = appContext?.Trim();
        var prefix = string.IsNullOrEmpty(context)
            ? ""
            : $"<APP-CONTEXT>\n{context[..Math.Min(context.Length, AppContextCharacterLimit)]}\n</APP-CONTEXT>\n\n";
        return $"{prefix}<USER-INPUT>\n{text}\n</USER-INPUT>";
    }

    /// <summary>
    /// Returns the cleaned dictation, or <paramref name="text"/> unchanged when the provider fails or
    /// replies with something that is not a cleaned transcript. <paramref name="complete"/> takes
    /// (system prompt, user prompt); <paramref name="systemPrompt"/> is the active cleanup preset.
    /// </summary>
    public static async Task<string> CleanAsync(
        string text,
        string? appContext,
        Func<string, string, CancellationToken, Task<string>> complete,
        Action<string, Exception?>? log = null,
        CancellationToken cancellationToken = default,
        string? systemPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string raw;
        var instructions = string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt;
        try { raw = await complete(instructions, FormatInput(text, appContext), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("Hosted dictation cleanup failed; pasting the original transcript.", exception);
            return text;
        }

        var cleaned = CleanOutput(raw);
        if (cleaned.Length == 0 && ContainsDeletionCue(text)) return "";
        if (ShouldFallBackToInput(cleaned, text))
        {
            log?.Invoke("Hosted dictation cleanup reply was rejected; pasting the original transcript.", null);
            return text;
        }
        return cleaned;
    }

    public static bool ContainsDeletionCue(string text) =>
        new[] { "scratch that", "delete that", "forget that", "never mind" }.Any(text.ToLowerInvariant().Contains);

    /// <summary>macOS Qwen3PostProcessorOutputCleaner + TranscriptCleanupClient.cleanOutput.</summary>
    public static string CleanOutput(string text)
    {
        var result = text;
        result = Regex.Replace(result, @"<think>[\s\S]*?</think>", " ");
        result = Regex.Replace(result, @"(?is)<think\b[^>]*>[\s\S]*$", " ");
        result = Regex.Replace(result, @"<\|im_(?:start|end)\|>", " ");
        result = Regex.Replace(result, @"```[A-Za-z0-9_-]*\s*", " ");
        result = result.Replace("```", " ");
        result = Regex.Replace(result, @"^`+|`+$", " ");
        result = Regex.Replace(result, @"\[end of text\]", " ");
        result = Regex.Replace(result, @"(?im)^\s*(?:[#>*-]+\s*)?(?:\*\*|__)?(?:transcription|cleaned transcription|output|response)(?:\*\*|__)?\s*:\s*", "");
        result = Regex.Replace(result, @"(?im)^\s*(?:\*\*|__)([^*\n_]{1,80})(?:\*\*|__)\s*$", "$1");
        result = Regex.Replace(result, @"</?(?:USER-INPUT|APP-CONTEXT)>", " ");
        // Collapse runs of spaces only, so dictated lists keep their line breaks.
        result = result.Replace("\r\n", "\n").Replace('\r', '\n');
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = Regex.Replace(result, @"[ \t]+([,.;:!?])", "$1");
        result = Regex.Replace(result, @"[ \t]*\n[ \t]*", "\n");
        return result.Trim();
    }

    private static readonly string[] AssistantMarkers =
    [
        "the user is asking", "**analysis:**", "analysis:", "**action plan:**", "action plan:",
        "grammar/spelling:", "meaning:", "remove the filler word"
    ];

    /// <summary>macOS shouldFallbackToInput: empty, placeholder, assistant talk, or runaway growth.</summary>
    public static bool ShouldFallBackToInput(string cleaned, string input)
    {
        var trimmed = cleaned.Trim();
        if (trimmed.Length == 0) return true;
        var placeholder = trimmed.Replace("…", "...");
        if (placeholder is "..." or ". . ." ||
            (placeholder.Length <= 8 && placeholder.All(c => char.IsWhiteSpace(c) || ".…-_,;:!?()[]{}".Contains(c))))
            return true;
        var lower = trimmed.ToLowerInvariant();
        if (AssistantMarkers.Any(lower.Contains)) return true;

        var inputLength = Math.Max(input.Trim().Length, 1);
        var expansion = (double)trimmed.Length / inputLength;
        if (inputLength < 50) return expansion > 4.0 && trimmed.Length > 80;
        if (inputLength < 150) return expansion > 2.5 && trimmed.Length > 150;
        return expansion > 2.0 && trimmed.Length > 200;
    }
}
