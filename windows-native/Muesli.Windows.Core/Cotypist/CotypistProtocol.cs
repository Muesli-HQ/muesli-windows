using System.Text.Json.Nodes;

namespace Muesli.Windows.Cotypist;

/// <summary>
/// Port of macOS CotypistRequestLedger (2f10b23d): the newest keystroke generation seen from each
/// helper client. Once a newer request arrives, older ones still queued or running are superseded.
/// </summary>
public sealed class CotypistRequestLedger
{
    private const int MaxClients = 64;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _latest = [];
    private readonly Dictionary<string, ulong> _lastSeen = [];
    private ulong _clock;

    /// <summary>Records a request and returns whether it is still the newest from its client. Requests
    /// without a client are always current.</summary>
    public Func<bool> Note(string? client, int? generation)
    {
        if (client is null || generation is not { } current) return () => true;
        lock (_lock)
        {
            _latest[client] = Math.Max(_latest.GetValueOrDefault(client, current), current);
            _lastSeen[client] = ++_clock;
            // One helper per app that has had a text field focused: forget the least recently active.
            if (_latest.Count > MaxClients)
            {
                var oldest = _lastSeen.MinBy(pair => pair.Value).Key;
                _latest.Remove(oldest);
                _lastSeen.Remove(oldest);
            }
        }
        return () =>
        {
            lock (_lock) return _latest.GetValueOrDefault(client, current) <= current;
        };
    }
}

/// <summary>macOS CotypistAgentPrompt: Claude Code's input box in a terminal screen plus the
/// conversation above it, for terminals that give an input method no text.</summary>
public static class CotypistAgentPrompt
{
    public static string? Context(string screen)
    {
        var lines = screen.Split('\n').TakeLast(120).ToArray();
        for (var i = lines.Length - 1; i >= 1; i--)
        {
            if (!IsPrompt(lines[i]) || !IsRule(lines[i - 1])) continue;
            var typed = lines[i][2..];
            var j = i + 1;
            while (j < lines.Length && !IsRule(lines[j]))
            {
                typed += lines[j].Length > 2 ? lines[j][2..] : "";
                j++;
            }
            if (j >= lines.Length) return null;
            // Terminals pad lines to their width; beyond a single space it's padding.
            while (typed.EndsWith("  ", StringComparison.Ordinal)) typed = typed[..^1];
            var above = lines[Math.Max(0, i - 41)..(i - 1)].Select(line => line.Trim(' ', '\t')).Where(line => line.Length > 0);
            return string.Join('\n', above) + "\n\n" + typed;
        }
        return null;
    }

    public static bool IsRule(string line)
    {
        var body = line.Trim(' ', '\t');
        return body.Length >= 10 && body.All(character => character == '─');
    }

    public static bool IsPrompt(string line) => line.Length >= 2 && line[0] == '❯' && line[1] is ' ' or ' ';
}

public static class CotypistMessages
{
    /// <summary>The sentence being written at the caret, for showing where a suggestion was accepted:
    /// from the last sentence end or line break, one sentence further back when the current one has
    /// only just started, at most <paramref name="maxCharacters"/>, cut at a word.</summary>
    public static string Sentence(string text, int maxCharacters = 120)
    {
        var line = text.Split('\n')[^1];
        var starts = new List<int> { 0 };
        for (var index = 0; index < line.Length - 1; index++)
            if (".!?".Contains(line[index]) && line[index + 1] == ' ') starts.Add(index + 2);
        var current = starts[^1];
        var start = line[current..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 ? current : starts[Math.Max(starts.Count - 2, 0)];
        line = line[start..].Trim('\r', '\n');
        if (line.Length <= maxCharacters) return line.TrimStart(' ');
        var tail = line[^maxCharacters..];
        var space = tail.IndexOf(' ');
        return "…" + (space >= 0 ? tail[(space + 1)..] : tail);
    }

    /// <summary>An outcome message from the helper as a history event; null when malformed.</summary>
    public static CotypistSuggestionEvent? Event(JsonObject message, string contextBefore, string model, string appName, DateTimeOffset date)
    {
        var text = (string?)message["text"];
        if (string.IsNullOrEmpty(text) || !Enum.TryParse<CotypistOutcome>((string?)message["outcome"], ignoreCase: true, out var outcome)
            || !Enum.IsDefined(outcome)) return null;
        int? Int(string key) => message[key] is JsonValue value && value.TryGetValue<double>(out var number) ? (int)number : null;
        return new CotypistSuggestionEvent(
            date,
            (string?)message["app"] ?? "",
            appName,
            (string?)message["trigger"] ?? "boundary",
            text,
            (string?)message["accepted"] ?? (outcome == CotypistOutcome.Accepted ? text : ""),
            outcome,
            message["first_word_logprob"] is JsonValue logprob && logprob.TryGetValue<double>(out var value) ? value : null,
            Int("first_ms"),
            Int("full_ms"),
            (string?)message["source"] ?? "",
            model,
            contextBefore);
    }
}
