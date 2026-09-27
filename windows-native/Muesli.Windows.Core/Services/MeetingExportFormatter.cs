using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Framework-neutral meeting export formatting. File picking, PDF rendering, and shell activation
/// remain host responsibilities; this formatter is shared by WPF, WinUI, and CommandHost.
/// </summary>
public static class MeetingExportFormatter
{
    public static string BuildMarkdown(MeetingItem meeting, MeetingExportMode mode, Dictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var summary = ApplyAliasesToNotes(meeting.Summary ?? "", aliases);
        var transcript = ApplyAliases(meeting.Transcript ?? "", aliases);

        var parts = new List<string>
        {
            $"# {meeting.Title}",
            "",
            $"**Date:** {meeting.CreatedAt:yyyy-MM-dd HH:mm}",
            $"**Duration:** {meeting.DurationLabel}"
        };
        if (meeting.WordCount > 0)
        {
            parts.Add($"**Words:** ~{meeting.WordCount:N0}");
        }
        if (!string.IsNullOrWhiteSpace(meeting.TemplateName))
        {
            parts.Add($"**Template:** {meeting.TemplateName}");
        }
        parts.Add("");
        parts.Add("---");
        parts.Add("");

        switch (mode)
        {
            case MeetingExportMode.Notes:
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    parts.Add(summary);
                    AppendManualNotes(parts, meeting);
                }
                else
                {
                    parts.Add("*No structured notes available. Raw transcript included below.*");
                    AppendManualNotes(parts, meeting);
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
                parts.Add(string.IsNullOrWhiteSpace(summary)
                    ? "*No structured notes available.*"
                    : summary);
                AppendManualNotes(parts, meeting);
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

    public static string SuggestFilename(MeetingItem meeting, MeetingExportMode mode)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var sanitized = SanitizeFilename(meeting.Title);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "meeting";
        }

        var suffix = mode switch
        {
            MeetingExportMode.Notes => "-notes",
            MeetingExportMode.Transcript => "-transcript",
            _ => ""
        };

        return $"{sanitized}{suffix}.pdf";
    }

    private static void AppendManualNotes(List<string> parts, MeetingItem meeting)
    {
        if (string.IsNullOrWhiteSpace(meeting.ManualNotes))
        {
            return;
        }

        parts.Add("");
        parts.Add(MeetingNotesDocument.ManualHeading);
        parts.Add("");
        parts.Add(meeting.ManualNotes.Trim());
    }

    private static string ApplyAliases(string text, Dictionary<string, string>? aliases)
    {
        if (string.IsNullOrWhiteSpace(text) || aliases is null || aliases.Count == 0)
        {
            return text;
        }

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
        {
            return text;
        }

        var result = text;
        foreach (var pair in aliases.OrderByDescending(p => p.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Key == pair.Value)
            {
                continue;
            }

            var escaped = Regex.Escape(pair.Key);
            result = new Regex($@"(?<!\w){escaped}(?!\w)").Replace(result, pair.Value);
        }

        return result;
    }

    private static string SanitizeFilename(string title)
    {
        var result = Regex.Replace(title ?? "", @"[^\w\s-]", "-");
        result = Regex.Replace(result, @"\s+", "-");
        result = Regex.Replace(result, @"-+", "-");
        return result.Trim('-').ToLowerInvariant();
    }
}
