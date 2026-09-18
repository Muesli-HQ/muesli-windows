using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public static class SpeakerAliasService
{
    public static string Apply(string text, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(text) || aliases.Count == 0)
        {
            return text;
        }

        var result = text;
        foreach (var pair in aliases.OrderByDescending(pair => pair.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || pair.Key == pair.Value)
            {
                continue;
            }

            var escaped = Regex.Escape(pair.Key);
            result = Regex.Replace(
                result,
                $@"(?<![\p{{L}}\p{{N}}_]){escaped}(?![\p{{L}}\p{{N}}_])",
                pair.Value,
                RegexOptions.CultureInvariant);
        }
        return result;
    }
}
