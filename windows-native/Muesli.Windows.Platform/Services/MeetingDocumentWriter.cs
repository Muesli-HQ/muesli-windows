using System.IO;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
namespace Muesli.Windows.Services;
public static class MeetingDocumentWriter
{
    /// <summary>
    /// Release gate for the QuestPDF-backed PDF export. QuestPDF's Community-license eligibility is
    /// a release-owner decision (EXP-01); until that conclusion is recorded, PDF is not offered.
    /// The export system is retained and can be enabled with this switch (or the
    /// <c>MUESLI_PDF_EXPORT_APPROVED=1</c> development override) once eligibility is approved.
    /// </summary>
    public static bool PdfExportApproved { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("MUESLI_PDF_EXPORT_APPROVED"), "1", StringComparison.Ordinal);

    /// <summary>Save formats the export picker may offer, in order.</summary>
    public static string[] ApprovedExportExtensions =>
        PdfExportApproved ? [".md", ".pdf"] : [".md"];

    /// <summary>Writes the chosen save format. PDF when the picker selected .pdf, Markdown otherwise.</summary>
    public static void Write(string markdown, string path)
    {
        if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (!PdfExportApproved)
            {
                throw new InvalidOperationException(
                    "PDF export is disabled until QuestPDF Community-license eligibility is approved (EXP-01). " +
                    "Markdown export remains available.");
            }

            GeneratePdf(markdown, path);
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
    /// Renders deterministic Markdown to PDF bytes for callers that own their own atomic write
    /// (for example the automatic exporter). Honors the same EXP-01 release gate as
    /// <see cref="Write"/>.
    /// </summary>
    public static byte[] GeneratePdfBytes(string markdown)
    {
        if (!PdfExportApproved)
        {
            throw new InvalidOperationException(
                "PDF export is disabled until QuestPDF Community-license eligibility is approved (EXP-01). " +
                "Markdown export remains available.");
        }

        return CreatePdfDocument(markdown).GeneratePdf();
    }

    private static void GeneratePdf(string markdown, string outputPath)
        => CreatePdfDocument(markdown).GeneratePdf(outputPath);

    private static IDocument CreatePdfDocument(string markdown)
    {
        // This build selects QuestPDF's Community license. The distributing legal
        // entity must confirm that it satisfies QuestPDF's current eligibility terms.
        QuestPDF.Settings.License = LicenseType.Community;

        var lines = markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(1.5f, Unit.Inch);
                page.DefaultTextStyle(TextStyle.Default.FontSize(11).FontFamily("Inter"));

                page.Content().PaddingVertical(20).Column(column =>
                {
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.TrimEnd();
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            column.Item().Height(8);
                            continue;
                        }

                        if (line.StartsWith("# "))
                        {
                            column.Item().Text(line.Substring(2)).FontSize(22).Bold();
                        }
                        else if (line.StartsWith("## "))
                        {
                            column.Item().PaddingTop(12).Text(line.Substring(3)).FontSize(17).Bold();
                        }
                        else if (line.StartsWith("**") && line.EndsWith("**"))
                        {
                            // Bold metadata line
                            var text = line.Trim('*');
                            var parts = text.Split(new[] { ": " }, 2, StringSplitOptions.None);
                            if (parts.Length == 2)
                            {
                                column.Item().Row(row =>
                                {
                                    row.AutoItem().Text(parts[0] + ": ").Bold();
                                    row.RelativeItem().Text(parts[1]);
                                });
                            }
                            else
                            {
                                column.Item().Text(text).Bold();
                            }
                        }
                        else if (line.StartsWith("---"))
                        {
                            column.Item().PaddingVertical(8).LineHorizontal(1).LineColor(Colors.Grey.Medium);
                        }
                        else if (line.StartsWith("*") && line.EndsWith("*"))
                        {
                            // Italic (placeholder text)
                            column.Item().Text(line.Trim('*')).Italic().FontColor(Colors.Grey.Medium);
                        }
                        else
                        {
                            column.Item().Text(line);
                        }
                    }
                });
            });
        });
    }

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

