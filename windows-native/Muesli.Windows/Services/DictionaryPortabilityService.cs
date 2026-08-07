using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace Muesli.Windows.Services;

public static class DictionaryPortabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<DictionaryEntryRecord> Import(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var entriesElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "custom_words", out var customWords)
                ? customWords
                : throw new JsonException("Expected a JSON array or an object containing custom_words.");
        if (entriesElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("custom_words must be a JSON array.");

        var imported = entriesElement.Deserialize<List<PortableDictionaryEntry>>(JsonOptions) ?? [];
        var normalized = new List<DictionaryEntryRecord>();
        foreach (var entry in imported)
        {
            var phrase = entry.Word?.Trim() ?? "";
            if (phrase.Length == 0)
                continue;
            normalized.Add(new DictionaryEntryRecord
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? $"dictentry_{Guid.NewGuid():N}" : entry.Id,
                Phrase = phrase,
                Replacement = entry.Replacement?.Trim() ?? phrase,
                MatchingThreshold = Math.Clamp(entry.MatchingThreshold ?? 0.85, 0.70, 0.95)
            });
        }
        return normalized;
    }

    public static void Export(string path, IEnumerable<DictionaryEntryRecord> entries)
    {
        var portable = entries.Select(entry => new PortableDictionaryEntry
        {
            Id = entry.Id,
            Word = entry.Phrase,
            Replacement = entry.Replacement,
            MatchingThreshold = entry.MatchingThreshold
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(portable, JsonOptions));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed record PortableDictionaryEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
        [JsonPropertyName("word")]
        public string? Word { get; init; }
        [JsonPropertyName("replacement")]
        public string? Replacement { get; init; }
        [JsonPropertyName("matching_threshold")]
        public double? MatchingThreshold { get; init; }
    }
}
