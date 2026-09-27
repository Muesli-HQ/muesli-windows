using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public static partial class FillerWordFilter
{
    private static readonly string[] PhraseFillers =
    [
        "you know",
        "i mean",
        "sort of",
        "kind of"
    ];

    private static readonly string[] WordFillers =
    [
        "uh", "um", "uhh", "umm", "er", "err", "ah", "ahh", "hmm", "hm", "mm", "mmm"
    ];

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var filtered = text;
        filtered = Regex.Replace(
            filtered,
            @"(?<![\p{L}\p{N}])like\s*,(?![\p{L}\p{N}])",
            "",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var phrase in PhraseFillers)
        {
            filtered = Regex.Replace(
                filtered,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?:\s*,)?(?![\p{{L}}\p{{N}}])",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        foreach (var word in WordFillers)
        {
            filtered = Regex.Replace(
                filtered,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?:\s*,)?(?![\p{{L}}\p{{N}}])",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        filtered = WhitespaceBeforePunctuation().Replace(filtered, "$1");
        filtered = RepeatedWhitespace().Replace(filtered, " ").Trim();
        if (filtered.Length == 0)
        {
            return filtered;
        }

        return char.IsLower(filtered[0])
            ? char.ToUpperInvariant(filtered[0]) + filtered[1..]
            : filtered;
    }

    [GeneratedRegex(@"\s+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceBeforePunctuation();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWhitespace();
}
