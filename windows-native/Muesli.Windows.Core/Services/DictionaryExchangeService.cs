using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed record DictionaryExchangeDocument(
    int Version,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyList<DictionaryEntryRecord> Entries)
{
    public const int CurrentVersion = 1;
}

public sealed record DictionaryImportConflict(
    DictionaryEntryRecord Existing,
    DictionaryEntryRecord Incoming);

public sealed record DictionaryImportPreview(
    IReadOnlyList<DictionaryEntryRecord> Incoming,
    IReadOnlyList<DictionaryEntryRecord> Additions,
    IReadOnlyList<DictionaryImportConflict> Conflicts,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Versioned, lossless dictionary JSON exchange with validation before mutation.</summary>
public sealed class DictionaryExchangeService
{
    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly AtomicJsonFile _json;

    public DictionaryExchangeService(Action<string>? report = null) => _json = new AtomicJsonFile(report);

    public void Export(string path, IEnumerable<DictionaryEntryRecord> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);
        var document = new DictionaryExchangeDocument(
            DictionaryExchangeDocument.CurrentVersion,
            DateTimeOffset.UtcNow,
            entries.Select(Clone).ToList());
        _json.Save(Path.GetFullPath(path), document, AtomicJsonSaveMode.Recoverable);
    }

    public DictionaryImportPreview PreviewImport(
        string path,
        IEnumerable<DictionaryEntryRecord> existing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(existing);
        DictionaryExchangeDocument? document = null;
        IReadOnlyList<DictionaryEntryRecord>? legacyEntries = null;
        try
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                legacyEntries = ParseLegacyEntries(root);
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "custom_words", out var words))
            {
                legacyEntries = ParseLegacyEntries(words);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var payload = root;
                if (TryGetProperty(root, "data", out var data))
                {
                    if (!TryGetProperty(root, "schemaVersion", out var schema) ||
                        !schema.TryGetInt32(out var version) ||
                        version != AtomicJsonFile.CurrentSchemaVersion)
                    {
                        return new([], [], [], ["The dictionary file has an unsupported envelope version."]);
                    }
                    payload = data;
                }
                document = payload.Deserialize<DictionaryExchangeDocument>(ImportJsonOptions);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new([], [], [], [$"The dictionary file has invalid content: {exception.Message}"]);
        }
        if (document is null && legacyEntries is null)
        {
            return new([], [], [], ["The dictionary file has an unsupported format."]);
        }

        var errors = new List<string>();
        if (document is not null && document.Version != DictionaryExchangeDocument.CurrentVersion)
        {
            errors.Add($"Dictionary format version {document.Version} is not supported.");
        }

        var incoming = (document?.Entries ?? legacyEntries ?? []).Select(Clone).ToList();
        var duplicatePhrases = incoming
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Phrase))
            .GroupBy(entry => entry.Phrase.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var duplicate in duplicatePhrases)
        {
            errors.Add($"The import contains duplicate phrase '{duplicate}'.");
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var entry = incoming[index];
            if (string.IsNullOrWhiteSpace(entry.Id)) errors.Add($"Entry {index + 1} has no id.");
            if (string.IsNullOrWhiteSpace(entry.Phrase)) errors.Add($"Entry {index + 1} has no phrase.");
            if (!double.IsFinite(entry.MatchingThreshold) || entry.MatchingThreshold is < 0 or > 1)
            {
                errors.Add($"Entry {index + 1} has an invalid matching threshold.");
            }
        }

        var existingByPhrase = existing
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Phrase))
            .GroupBy(entry => entry.Phrase.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var additions = new List<DictionaryEntryRecord>();
        var conflicts = new List<DictionaryImportConflict>();
        foreach (var entry in incoming)
        {
            var phrase = entry.Phrase.Trim();
            if (!existingByPhrase.TryGetValue(phrase, out var current))
            {
                additions.Add(entry);
            }
            else if (current != entry)
            {
                conflicts.Add(new DictionaryImportConflict(current, entry));
            }
        }

        return new(incoming, additions, conflicts, errors);
    }

    private static IReadOnlyList<DictionaryEntryRecord> ParseLegacyEntries(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException("custom_words must be an array.");
        var entries = new List<DictionaryEntryRecord>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new JsonException("Dictionary entries must be objects.");
            var phrase = TryGetProperty(item, "word", out var word) ? word.GetString()?.Trim() ?? "" : "";
            if (phrase.Length == 0) continue;
            var replacement = TryGetProperty(item, "replacement", out var value)
                ? value.GetString()?.Trim() ?? phrase : phrase;
            var id = TryGetProperty(item, "id", out var key) ? key.GetString() : null;
            var threshold = TryGetProperty(item, "matching_threshold", out var setting) ||
                            TryGetProperty(item, "matchingThreshold", out setting)
                ? setting.GetDouble() : .85;
            entries.Add(new DictionaryEntryRecord
            {
                Id = string.IsNullOrWhiteSpace(id) ? $"dictentry_{Guid.NewGuid():N}" : id,
                Phrase = phrase,
                Replacement = replacement,
                MatchingThreshold = Math.Clamp(threshold, .70, .95)
            });
        }
        return entries;
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

    public static IReadOnlyList<DictionaryEntryRecord> Merge(
        IEnumerable<DictionaryEntryRecord> existing,
        DictionaryImportPreview preview,
        bool replaceConflicts)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.IsValid) throw new InvalidDataException("Invalid dictionary imports cannot be merged.");

        var merged = existing.Select(Clone).ToList();
        foreach (var addition in preview.Additions)
        {
            merged.Add(Clone(addition));
        }
        if (replaceConflicts)
        {
            foreach (var conflict in preview.Conflicts)
            {
                var index = merged.FindIndex(entry =>
                    entry.Phrase.Trim().Equals(conflict.Existing.Phrase.Trim(), StringComparison.OrdinalIgnoreCase));
                if (index >= 0) merged[index] = Clone(conflict.Incoming);
            }
        }
        return merged;
    }

    private static DictionaryEntryRecord Clone(DictionaryEntryRecord entry) => new()
    {
        Id = entry.Id,
        Phrase = entry.Phrase,
        Replacement = entry.Replacement,
        MatchingThreshold = entry.MatchingThreshold
    };
}
