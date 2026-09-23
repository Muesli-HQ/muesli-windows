using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public static partial class FillerWordFilter
{
    private static readonly string[] PhraseFillers = ["you know", "i mean", "sort of", "kind of"];
    private static readonly string[] WordFillers = ["uh", "um", "uhh", "umm", "er", "err", "ah", "ahh", "hmm", "hm", "mm", "mmm"];

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var filtered = Regex.Replace(text, @"(?<![\p{L}\p{N}])like\s*,(?![\p{L}\p{N}])", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var phrase in PhraseFillers)
            filtered = Regex.Replace(filtered, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?:\s*,)?(?![\p{{L}}\p{{N}}])", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var word in WordFillers)
            filtered = Regex.Replace(filtered, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?:\s*,)?(?![\p{{L}}\p{{N}}])", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        filtered = WhitespaceBeforePunctuation().Replace(filtered, "$1");
        filtered = RepeatedHorizontalWhitespace().Replace(filtered, " ");
        filtered = HorizontalWhitespaceBeforeLineBreak().Replace(filtered, "$1");
        filtered = ExcessBlankLines().Replace(filtered, Environment.NewLine + Environment.NewLine).Trim();
        return filtered.Length > 0 && char.IsLower(filtered[0])
            ? char.ToUpperInvariant(filtered[0]) + filtered[1..]
            : filtered;
    }

    [GeneratedRegex(@"[^\S\r\n]+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceBeforePunctuation();
    [GeneratedRegex(@"[^\S\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedHorizontalWhitespace();
    [GeneratedRegex(@"[ \t]+(\r?\n)", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespaceBeforeLineBreak();
    [GeneratedRegex(@"(?:\r?\n){3,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessBlankLines();
}
