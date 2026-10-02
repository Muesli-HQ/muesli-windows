using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public sealed record TranscriptChatMessage(string Text, string? Speaker, string? Timestamp)
{
    public bool IsYou => string.Equals(Speaker, "You", StringComparison.OrdinalIgnoreCase);

    public static TranscriptChatMessage[] Parse(string transcript) => transcript.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
        .Select(line => line.Trim()).Where(line => line.Length > 0).Select(line =>
        {
            string? timestamp = null;
            if (line.StartsWith('[') && line.IndexOf(']') is > 0 and var end)
            { timestamp = line[1..end]; line = line[(end + 1)..].Trim(); }
            string? speaker = null;
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var label = line[..colon].Trim();
                if (label.Length <= 32 && (new[] { "You", "Others", "Multiple speakers", "Unknown speaker" }
                    .Contains(label, StringComparer.OrdinalIgnoreCase) || Regex.IsMatch(label, @"^Speaker\s+\d+$", RegexOptions.IgnoreCase)))
                { speaker = label; var body = line[(colon + 1)..].Trim(); if (body.Length > 0) line = body; }
            }
            return new TranscriptChatMessage(line, speaker, string.IsNullOrEmpty(timestamp) ? null : timestamp);
        }).ToArray();
}
