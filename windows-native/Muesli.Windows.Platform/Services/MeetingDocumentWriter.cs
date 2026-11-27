using System.IO;
using System.Text.RegularExpressions;
using Muesli.Windows.Services.Pdf;
namespace Muesli.Windows.Services;
public static class MeetingDocumentWriter
{
    /// <summary>Save formats the export picker offers, in order.</summary>
    public static string[] ExportExtensions => [".md", ".pdf"];

    /// <summary>Writes the chosen save format. PDF when the picker selected .pdf, Markdown otherwise.</summary>
    public static void Write(string markdown, string path)
    {
        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllBytes(path, MarkdownPdf.Render(markdown, DateTimeOffset.Now));
        }
        else
        {
            File.WriteAllText(path, markdown);
        }
    }

    internal static string BuildMarkdown(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases)
        => MeetingExportFormatter.BuildMarkdown(meeting, mode, aliases);

    /// <summary>
    /// The user's own notes go into every export that carries notes. They are kept under their own
    /// heading rather than blended into the generated body, matching how the app displays them.
    /// </summary>
    private static void AppendManualNotes(List<string> parts, MeetingItem meeting)
    {
        if (string.IsNullOrWhiteSpace(meeting.ManualNotes)) return;
        parts.Add("");
        parts.Add(MeetingNotesDocument.ManualHeading);
        parts.Add("");
        parts.Add(meeting.ManualNotes.Trim());
    }

    private static string ApplyAliases(string text, Dictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(text) || aliases is null || aliases.Count == 0)
            return text;

        var result = text;
        foreach (var pair in aliases.OrderByDescending(p => p.Key.Length))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value) && pair.Key != pair.Value)
            {
                result = result.Replace(pair.Key, pair.Value);
            }
        }

        return result;
    }

    private static string ApplyAliasesToNotes(string text, Dictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(text) || aliases is null || aliases.Count == 0)
            return text;

        var result = text;
        foreach (var pair in aliases.OrderByDescending(p => p.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Key == pair.Value)
                continue;

            var escaped = Regex.Escape(pair.Key);
            var pattern = new Regex($@"(?<!\w){escaped}(?!\w)");
            result = pattern.Replace(result, pair.Value);
        }

        return result;
    }

    /// <summary>
    /// Renders Markdown to PDF bytes for callers that own their own atomic write (for example the
    /// automatic exporter). The same Markdown and <paramref name="createdAt"/> give the same bytes,
    /// which is what lets the automatic exporter recognise an export it has already published.
    /// Rendering is done by the in-house DirectWrite engine (<see cref="MarkdownPdf"/>): no native
    /// PDF library and no bundled fonts.
    /// </summary>
    public static byte[] GeneratePdfBytes(string markdown, DateTimeOffset createdAt)
        => MarkdownPdf.Render(markdown, createdAt);

    internal static string SuggestFilename(MeetingItem meeting, MeetingExportMode mode)
        => MeetingExportFormatter.SuggestFilename(meeting, mode);

    private static string SanitizeFilename(string title)
    {
        // Keep alphanumeric, spaces, hyphens; replace everything else with hyphen
        var result = Regex.Replace(title, @"[^\w\s-]", "-");
        result = Regex.Replace(result, @"\s+", "-");
        result = Regex.Replace(result, @"-+", "-");
        return result.Trim('-').ToLowerInvariant();
    }
}

