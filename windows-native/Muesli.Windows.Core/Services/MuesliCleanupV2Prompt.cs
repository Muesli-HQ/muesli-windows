using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// macOS MuesliCleanupV2Prompt (2f10b23d): the inference contract for the v12 cleanup fine-tunes. Keep the system prompt
/// byte-identical to macOS; the models were trained on it.
/// </summary>
public static class MuesliCleanupV2Prompt
{
    public const string SystemPrompt =
        "Clean up speech-to-text transcription. Only make changes when there is a clear error. If the text is already correct, output it exactly as-is.\n" +
        "\n" +
        "The user input may include an <APP-CONTEXT> section with focused app, document, URL, selected text, or OCR screen text. Use it only to resolve obvious transcription errors, names, acronyms, and formatting intent. Never copy app context into the output unless the user dictated it.\n" +
        "\n" +
        "You may: fix obvious misspellings, remove filler words (um, uh, like), apply 'scratch that' deletions, and format numbered or bullet lists when dictated.\n" +
        "\n" +
        "Do not: paraphrase, reword, add words, remove meaningful words, change the meaning in any way, wrap the output in markdown, code fences, tags, labels, or commentary, or repeat the output more than once. Preserve the speaker's original phrasing.";

    public const string EndOfTurn = "<|im_end|>";

    public static string Request(string text, string? appContext = null)
    {
        var context = "";
        var lines = appContext?.Split('\n').Where(line => line.Trim() is not ("Document context:" or "OCR screen text:"));
        var joined = lines is null ? null : string.Join("\n", lines);
        if (!string.IsNullOrEmpty(joined)) context = $"<APP-CONTEXT>\n{joined[..Math.Min(1_200, joined.Length)]}\n</APP-CONTEXT>\n\n";
        return SystemPrompt + "\n\n" + context + text;
    }

    /// <param name="addsBos">True when llama.cpp already inserts the model's BOS token, so LFM's explicit one isn't doubled.</param>
    public static string GgufRequest(string text, CleanupPromptContract contract, string? appContext = null, bool addsBos = false)
    {
        var prefix = contract == CleanupPromptContract.TrainedV12Lfm && !addsBos ? "<|startoftext|>" : "";
        var suffix = contract == CleanupPromptContract.TrainedV12Qwen ? "<think>\n\n</think>\n\n" : "";
        return prefix + "<|im_start|>user\n" + Request(text, appContext) + "<|im_end|>\n<|im_start|>assistant\n" + suffix;
    }

    /// <summary>
    /// macOS guardResult: never replace a complete dictation with a budget-truncated prefix, an empty answer or a runaway.
    /// The caller still applies its own transcript safety check afterwards.
    /// </summary>
    public static (string Text, bool DidFallback) Guard(string output, string input, bool generationCompleted)
    {
        var cleaned = Tidy(CleanOutput(output, input));
        var rejected = !generationCompleted || cleaned.Length == 0 || cleaned.Length > input.Length * 3;
        return rejected ? (input, true) : (cleaned, false);
    }

    /// <summary>macOS cleanOutput: strips only exact leading markers or verbatim training instructions.</summary>
    public static string CleanOutput(string output, string input = "")
    {
        var text = output.Replace(EndOfTurn, "").Trim();
        text = Regex.Replace(text, @"^<think>[\s\S]*?</think>", "").Trim();
        // A dictated opening word is content, even when formatting puts it on its own line.
        var inputStartsWithModel = Regex.IsMatch(input.Trim(), @"^model\b", RegexOptions.IgnoreCase);
        var paragraphs = SystemPrompt.Split("\n\n").Where(paragraph => paragraph.Length >= 30).ToArray();
        while (text.Length > 0)
        {
            var previous = text;
            if (!inputStartsWithModel)
            {
                if (text == "model") text = "";
                else if (text.StartsWith("model\n", StringComparison.Ordinal)) text = text[6..];
            }
            foreach (var marker in new[] { "</APP-CONTEXT>", "<APP-CONTEXT>" })
                if (text.StartsWith(marker, StringComparison.Ordinal)) text = text[marker.Length..].Trim();
            foreach (var paragraph in paragraphs)
                if (text == paragraph || text.StartsWith(paragraph + "\n", StringComparison.Ordinal)) text = text[paragraph.Length..].Trim();
            text = text.Trim();
            if (text == previous) break;
        }
        return text;
    }

    /// <summary>macOS TranscriptCleanupClient.cleanOutput whitespace rules.</summary>
    private static string Tidy(string text)
    {
        if (text.Length == 0) return text;
        text = Regex.Replace(text, @"\r\n?", "\n");
        text = Regex.Replace(text, @"[ \t]+([,;:!?]|\.(?![./\w]))", "$1");
        text = Regex.Replace(text, @"(?<=\S)[ \t]{2,}", " ");
        text = Regex.Replace(text, @"(?m)[ \t]+$", "");
        text = Regex.Replace(text, @"[ \t]*\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
