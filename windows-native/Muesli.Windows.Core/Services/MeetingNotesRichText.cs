using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public sealed record MeetingNoteRun(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);
public sealed record MeetingNoteLine(int Heading, IReadOnlyList<MeetingNoteRun> Runs);

/// <summary>Portable Markdown storage for the native notes editor; matches the macOS editing subset.</summary>
public static class MeetingNotesRichText
{
    public static MeetingNoteLine ParseLine(string line)
    {
        var heading = Regex.Match(line, @"^(#{1,6}) ");
        var level = heading.Success ? heading.Groups[1].Length : 0;
        if (level > 0 && line.Length > heading.Length) line = line[heading.Length..];
        else level = 0; // Keep an empty typed heading marker until it has content.
        if (line.StartsWith("- [ ] ")) line = "☐ " + line[6..];
        else if (line.StartsWith("- [x] ") || line.StartsWith("- [X] ")) line = "☑ " + line[6..];
        else if (line.StartsWith("- ")) line = "• " + line[2..];
        var runs = new List<MeetingNoteRun>();
        ParseInline(line, runs, false, false);
        return new(level, runs);
    }

    private static void ParseInline(string text, List<MeetingNoteRun> runs, bool bold, bool italic, int depth = 0)
    {
        // Bound recursion for externally imported notes; unmatched delimiters remain visible text.
        if (depth >= 16) { runs.Add(new(text, bold, italic)); return; }
        var plain = new StringBuilder();
        void Flush() { if (plain.Length > 0) { runs.Add(new(plain.ToString(), bold, italic)); plain.Clear(); } }
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '\\' && index + 1 < text.Length && "*_`[]\\".Contains(text[index + 1]))
            { plain.Append(text[index + 1]); index += 2; continue; }
            if (text[index] == '[')
            {
                var endLabel = text.IndexOf("](", index, StringComparison.Ordinal);
                var endLink = endLabel < 0 ? -1 : text.IndexOf(')', endLabel + 2);
                if (endLink > endLabel + 2)
                {
                    Flush(); var linked = new List<MeetingNoteRun>();
                    ParseInline(text[(index + 1)..endLabel], linked, bold, italic, depth + 1);
                    runs.AddRange(linked.Select(run => run with { Link = text[(endLabel + 2)..endLink] }));
                    index = endLink + 1; continue;
                }
            }
            var marker = text.AsSpan(index).StartsWith("**") ? "**" : text.AsSpan(index).StartsWith("__") ? "__"
                : text[index] is '*' or '_' or '`' ? text[index].ToString() : "";
            var end = marker.Length == 0 ? -1 : text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
            if (end > index + marker.Length)
            {
                Flush(); var inner = text[(index + marker.Length)..end];
                if (marker == "`") runs.Add(new(inner, bold, italic, true));
                else ParseInline(inner, runs, bold || marker.Length == 2, italic || marker.Length == 1, depth + 1);
                index = end + marker.Length; continue;
            }
            plain.Append(text[index++]);
        }
        Flush();
    }

    public static string SerializeLine(MeetingNoteLine line)
    {
        var runs = line.Runs.ToList();
        var prefix = line.Heading > 0 ? new string('#', line.Heading) + " " : "";
        if (runs.Count > 0)
        {
            var text = runs[0].Text;
            foreach (var (display, markdown) in new[] { ("☐ ", "- [ ] "), ("☑ ", "- [x] "), ("• ", "- ") })
                if (text.StartsWith(display)) { prefix = markdown; runs[0] = runs[0] with { Text = text[display.Length..] }; break; }
        }
        return prefix + string.Concat(runs.Select(run =>
        {
            if (run.Text.Length == 0) return "";
            var value = run.Code ? $"`{run.Text}`" : Regex.Replace(run.Text, @"([\\*_`\[\]])", @"\$1");
            if (run.Italic) value = $"_{value}_";
            if (run.Bold) value = $"**{value}**";
            return run.Link is { Length: > 0 } link ? $"[{value}]({link})" : value;
        }));
    }
}
