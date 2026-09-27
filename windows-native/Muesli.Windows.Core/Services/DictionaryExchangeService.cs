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
        var loaded = _json.Load<DictionaryExchangeDocument?>(Path.GetFullPath(path), null);
        if (loaded.Value is null)
        {
            return new([], [], [], [loaded.Warning ?? "The dictionary file is empty or invalid JSON."]);
        }

        var document = loaded.Value;
        var errors = new List<string>();
        if (document.Version != DictionaryExchangeDocument.CurrentVersion)
        {
            errors.Add($"Dictionary format version {document.Version} is not supported.");
        }

        var incoming = document.Entries?.Select(Clone).ToList() ?? [];
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
