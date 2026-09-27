using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public static partial class DictionaryCorrectionService
{
    public const double DefaultThreshold = 0.90;

    /// <summary>Loosest stored matching threshold the matcher will honour.</summary>
    public const double MinimumThreshold = 0.85;

    /// <summary>Strictest stored matching threshold the matcher will honour.</summary>
    public const double MaximumThreshold = 0.98;

    public static string Apply(string text, IEnumerable<DictionaryEntryRecord> entries)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var candidates = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Phrase) && !string.IsNullOrWhiteSpace(entry.Replacement))
            .Select(entry => new Candidate(
                TokenizePhrase(entry.Phrase),
                entry.Replacement.Trim(),
                ClampThreshold(entry.MatchingThreshold)))
            .Where(candidate => candidate.PhraseTokens.Count > 0)
            .OrderByDescending(candidate => candidate.PhraseTokens.Count)
            .ThenByDescending(candidate => candidate.PhraseTokens.Sum(token => token.Length))
            .ToList();
        if (candidates.Count == 0)
        {
            return text;
        }

        var matches = WordToken().Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        var output = new System.Text.StringBuilder(text.Length + 16);
        var tokenIndex = 0;
        var sourceOffset = 0;
        while (tokenIndex < matches.Count)
        {
            Candidate? selected = null;
            var selectedCount = 0;
            foreach (var candidate in candidates)
            {
                var count = candidate.PhraseTokens.Count;
                if (tokenIndex + count > matches.Count ||
                    !OnlySeparatorsBetween(text, matches, tokenIndex, count) ||
                    !MatchesCandidate(matches, tokenIndex, candidate))
                {
                    continue;
                }

                selected = candidate;
                selectedCount = count;
                break;
            }

            if (selected is null)
            {
                tokenIndex++;
                continue;
            }

            var first = matches[tokenIndex];
            var last = matches[tokenIndex + selectedCount - 1];
            output.Append(text, sourceOffset, first.Index - sourceOffset);
            output.Append(PreserveSimpleCasing(selected.Replacement, first.Value));
            sourceOffset = last.Index + last.Length;
            tokenIndex += selectedCount;
        }

        output.Append(text, sourceOffset, text.Length - sourceOffset);
        return output.ToString();
    }

    private static bool MatchesCandidate(MatchCollection matches, int start, Candidate candidate)
    {
        for (var offset = 0; offset < candidate.PhraseTokens.Count; offset++)
        {
            var actual = matches[start + offset].Value;
            var expected = candidate.PhraseTokens[offset];
            if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var conservativeThreshold = ConservativeThreshold(expected, candidate.Threshold);
            if (conservativeThreshold >= 1 ||
                Math.Abs(actual.Length - expected.Length) > 1 ||
                JaroWinkler(actual, expected) < conservativeThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static bool OnlySeparatorsBetween(string text, MatchCollection matches, int start, int count)
    {
        for (var offset = 0; offset < count - 1; offset++)
        {
            var left = matches[start + offset];
            var right = matches[start + offset + 1];
            var separator = text.AsSpan(left.Index + left.Length, right.Index - left.Index - left.Length);
            foreach (var character in separator)
            {
                if (character is not (' ' or '\t' or '\r' or '\n' or '-' or '\'' or '\u2019'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IReadOnlyList<string> TokenizePhrase(string phrase) =>
        WordToken().Matches(phrase.Trim()).Select(match => match.Value).ToList();

    private static double ConservativeThreshold(string expected, double configured)
    {
        var length = expected.Length;
        if (length <= 3)
        {
            return 1;
        }

        return Math.Max(configured, length switch
        {
            4 => 0.94,
            <= 6 => 0.91,
            _ => 0.88
        });
    }

    private static double ClampThreshold(double threshold)
    {
        if (double.IsNaN(threshold) || threshold <= 0)
        {
            return DefaultThreshold;
        }

        return Math.Clamp(threshold, MinimumThreshold, MaximumThreshold);
    }

    internal static double JaroWinkler(string left, string right)
    {
        var a = left.ToLowerInvariant();
        var b = right.ToLowerInvariant();
        if (a == b)
        {
            return 1;
        }
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        var distance = Math.Max(a.Length, b.Length) / 2 - 1;
        var aMatches = new bool[a.Length];
        var bMatches = new bool[b.Length];
        var matches = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var start = Math.Max(0, i - distance);
            var end = Math.Min(i + distance + 1, b.Length);
            for (var j = start; j < end; j++)
            {
                if (bMatches[j] || a[i] != b[j])
                {
                    continue;
                }
                aMatches[i] = true;
                bMatches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        for (int i = 0, j = 0; i < a.Length; i++)
        {
            if (!aMatches[i])
            {
                continue;
            }
            while (!bMatches[j])
            {
                j++;
            }
            if (a[i] != b[j])
            {
                transpositions++;
            }
            j++;
        }

        var m = (double)matches;
        var jaro = (m / a.Length + m / b.Length + (m - transpositions / 2.0) / m) / 3.0;
        var prefix = 0;
        while (prefix < Math.Min(4, Math.Min(a.Length, b.Length)) && a[prefix] == b[prefix])
        {
            prefix++;
        }
        return jaro + prefix * 0.1 * (1 - jaro);
    }

    private static string PreserveSimpleCasing(string replacement, string source)
    {
        if (source.Length == 0 || replacement.Length == 0)
        {
            return replacement;
        }

        return char.IsUpper(source[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['\u2019-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordToken();

    private sealed record Candidate(IReadOnlyList<string> PhraseTokens, string Replacement, double Threshold);
}
