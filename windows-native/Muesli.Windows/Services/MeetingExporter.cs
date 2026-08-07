using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using QuestPDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Muesli.Windows.Services;

public enum MeetingExportMode
{
    Notes,
    Transcript,
    FullMeeting
}

public static class MeetingExporter
{
    public static string ExportMarkdownAutomatically(MeetingItem meeting, string directory, Dictionary<string, string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Muesli Meetings");
        Directory.CreateDirectory(directory);
        var baseName = $"{meeting.CreatedAt:yyyy-MM-dd}-{SanitizeFilename(meeting.Title)}".TrimEnd('-');
        if (baseName.Length == 0) baseName = $"meeting-{meeting.CreatedAt:yyyy-MM-dd}";
        var path = Path.Combine(directory, $"{baseName}.md");
        if (File.Exists(path)) path = Path.Combine(directory, $"{baseName}-{meeting.Id[^Math.Min(8, meeting.Id.Length)..]}.md");
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, BuildMarkdown(meeting, MeetingExportMode.FullMeeting, aliases));
            File.Move(temporaryPath, path, overwrite: false);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static void Export(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases = null)
    {
        var markdown = BuildMarkdown(meeting, mode, aliases);
        var suggestedName = SuggestFilename(meeting, mode);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            DefaultExt = ".pdf",
            Filter = "PDF document (*.pdf)|*.pdf|Markdown file (*.md)|*.md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() != true)
            return;

        var path = dialog.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();

        try
        {
            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                GeneratePdf(markdown, path);
            }
            else
            {
                File.WriteAllText(path, markdown);
            }

            // Auto-open the exported file
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Export failed: {ex}");
        }
    }

    private static string BuildMarkdown(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases)
    {
        var summary = ApplyAliasesToNotes(meeting.Summary ?? "", aliases);
        var transcript = ApplyAliases(meeting.Transcript ?? "", aliases);

        var parts = new List<string>();

        parts.Add($"# {meeting.Title}");
        parts.Add("");
        parts.Add($"**Date:** {meeting.CreatedAt:yyyy-MM-dd HH:mm}");
        parts.Add($"**Duration:** {meeting.DurationLabel}");
        if (meeting.WordCount > 0)
        {
            parts.Add($"**Words:** ~{meeting.WordCount:N0}");
        }
        if (!string.IsNullOrWhiteSpace(meeting.TemplateName))
        {
            parts.Add($"**Template:** {meeting.TemplateName}");
        }
        parts.Add("");
        if (!string.IsNullOrWhiteSpace(meeting.ManualNotes))
        {
            parts.Add("## Manual Notes");
            parts.Add("");
            parts.Add(meeting.ManualNotes);
            parts.Add("");
        }
        parts.Add("---");
        parts.Add("");

        switch (mode)
        {
            case MeetingExportMode.Notes:
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    parts.Add(summary);
                }
                else
                {
                    parts.Add("*No structured notes available. Raw transcript included below.*");
                    parts.Add("");
                    parts.Add("## Raw Transcript");
                    parts.Add("");
                    parts.Add(transcript);
                }
                break;

            case MeetingExportMode.Transcript:
                parts.Add("## Raw Transcript");
                parts.Add("");
                parts.Add(transcript);
                break;

            case MeetingExportMode.FullMeeting:
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    parts.Add(summary);
                }
                else
                {
                    parts.Add("*No structured notes available.*");
                }
                parts.Add("");
                parts.Add("---");
                parts.Add("");
                parts.Add("## Raw Transcript");
                parts.Add("");
                parts.Add(transcript);
                break;
        }

        return string.Join(Environment.NewLine, parts);
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

    private static void GeneratePdf(string markdown, string outputPath)
    {
        // Configure QuestPDF license (Community/FOSS — no purchase needed)
        QuestPDF.Settings.License = LicenseType.Community;

        var lines = markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

        Document.Create(container =>
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
        }).GeneratePdf(outputPath);
    }

    private static string SuggestFilename(MeetingItem meeting, MeetingExportMode mode)
    {
        var sanitized = SanitizeFilename(meeting.Title);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "meeting";

        var suffix = mode switch
        {
            MeetingExportMode.Notes => "-notes",
            MeetingExportMode.Transcript => "-transcript",
            _ => ""
        };

        return $"{sanitized}{suffix}.pdf";
    }

    private static string SanitizeFilename(string title)
    {
        // Keep alphanumeric, spaces, hyphens; replace everything else with hyphen
        var result = Regex.Replace(title, @"[^\w\s-]", "-");
        result = Regex.Replace(result, @"\s+", "-");
        result = Regex.Replace(result, @"-+", "-");
        return result.Trim('-').ToLowerInvariant();
    }
}


