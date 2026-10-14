using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS <c>MeetingPreviewText</c>: the meeting list shows notes as plain text, so headings,
/// bullets, checkboxes, links, code, and emphasis markers are stripped before the snippet is cut.
/// </summary>
public static class MeetingPreviewText
{
    public static string Snippet(string? source, int limit = 88)
    {
        var compact = string.Join(' ', PlainText(source ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length == 0) return "No notes yet";
        return compact.Length <= limit ? compact : compact[..Math.Max(0, limit - 3)] + "...";
    }

    public static string PlainText(string markdown)
    {
        var lines = new List<string>();
        var insideFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
                continue;
            }
            if (insideFence || Rule.IsMatch(line)) continue;

            line = Heading.Replace(line, "");
            line = Quote.Replace(line, "");
            line = ListMarker.Replace(line, "");
            line = Checkbox.Replace(line, "");
            line = Image.Replace(line, "$1");
            line = Link.Replace(line, "$1");
            line = Code.Replace(line, "$1");
            foreach (var (pattern, replacement) in Delimiters) line = pattern.Replace(line, replacement);
            line = line.Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join(' ', lines);
    }

    private static readonly Regex Rule = new(@"^\s{0,3}([-*_]{3,}|[=-]{3,})\s*$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s*>+\s*", RegexOptions.Compiled);
    private static readonly Regex ListMarker = new(@"^\s*(?:[-+*]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Checkbox = new(@"^\s*\[[ xX]\]\s+", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Code = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly (Regex, string)[] Delimiters =
    [
        (new(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled), "$1"),
        (new(@"__([^_\n]+)__", RegexOptions.Compiled), "$1"),
        (new(@"~~([^~\n]+)~~", RegexOptions.Compiled), "$1"),
        (new(@"(^|[\s(\[{])\*([^*\n]+)\*($|[\s)\]}.,;:!?])", RegexOptions.Compiled), "$1$2$3"),
        (new(@"(^|[\s(\[{])_([^_\n]+)_($|[\s)\]}.,;:!?])", RegexOptions.Compiled), "$1$2$3")
    ];
}
