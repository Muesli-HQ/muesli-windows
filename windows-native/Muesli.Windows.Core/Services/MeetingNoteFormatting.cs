namespace Muesli.Windows.Services;

public static class MeetingNoteFormatting
{
    public static (int Start, int Length, string Replacement) Apply(string text, int start, int length, string marker)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        if (marker is "**" or "_")
        {
            var selection = text.Substring(start, length);
            var replacement = selection.Length >= 2 * marker.Length && selection.StartsWith(marker, StringComparison.Ordinal) && selection.EndsWith(marker, StringComparison.Ordinal)
                ? selection[marker.Length..^marker.Length] : marker + selection + marker;
            return (start, length, replacement);
        }
        if (marker is not ("## " or "- " or "- [ ] ")) throw new ArgumentException("Unsupported notes format.", nameof(marker));
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var searchFrom = start + length;
        if (length > 0 && text[searchFrom - 1] == '\n') searchFrom--;
        var lineEnd = text.IndexOf('\n', searchFrom);
        if (lineEnd < 0) lineEnd = text.Length;
        var lines = text[lineStart..lineEnd].Split('\n');
        var remove = lines.All(line => line.StartsWith(marker, StringComparison.Ordinal));
        return (lineStart, lineEnd - lineStart, string.Join('\n', lines.Select(line => remove ? line[marker.Length..] : marker + line)));
    }
}
