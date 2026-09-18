namespace Muesli.Windows.Services;

public sealed record DictionarySuggestion(string Observed, string Replacement)
{
    public string Display => $"{Observed} → {Replacement}";
}

public static class DictionarySuggestionDetector
{
    public static IReadOnlyList<DictionarySuggestion> FromEdit(string original, string edited)
    {
        var before = Tokenize(original);
        var after = Tokenize(edited);
        if (before.Count == 0 || after.Count == 0 || before.Count != after.Count)
        {
            return [];
        }

        var suggestions = new List<DictionarySuggestion>();
        for (var index = 0; index < before.Count; index++)
        {
            if (string.Equals(before[index], after[index], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (before[index].Length < 2 || after[index].Length < 2)
            {
                continue;
            }

            suggestions.Add(new DictionarySuggestion(before[index], after[index]));
        }

        return suggestions;
    }

    private static List<string> Tokenize(string value) =>
        (value ?? "")
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim('"', '\'', '.', ',', ';', ':', '!', '?', '(', ')'))
            .Where(token => token.Length > 0)
            .ToList();
}
