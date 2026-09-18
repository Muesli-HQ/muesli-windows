using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class DictionaryExchangeServiceTests
{
    [Fact]
    public void ExportPreviewAndMergePreserveLosslessFieldsAndExposeConflicts()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "dictionary.json");
        var service = new DictionaryExchangeService();
        var incoming = new[]
        {
            Entry("incoming-1", "Muesli", "muesli", .73),
            Entry("incoming-2", "Raheja", "Rahija", .91)
        };
        service.Export(path, incoming);

        var existing = new[] { Entry("existing-1", "muesli", "Muesli", .85) };
        var preview = service.PreviewImport(path, existing);

        Assert.True(preview.IsValid);
        Assert.Single(preview.Additions);
        Assert.Single(preview.Conflicts);
        var merged = DictionaryExchangeService.Merge(existing, preview, replaceConflicts: true);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, item => item.Id == "incoming-1" && item.MatchingThreshold == .73);
        Assert.Contains(merged, item => item.Id == "incoming-2" && item.Replacement == "Rahija");
    }

    [Fact]
    public void PreviewRejectsUnsupportedVersionDuplicatesAndInvalidThreshold()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "dictionary.json");
        var atomic = new AtomicJsonFile();
        atomic.Save(path, new DictionaryExchangeDocument(99, DateTimeOffset.UtcNow, new[]
        {
            Entry("one", "same", "", .8),
            Entry("two", "SAME", "", 1.4)
        }));

        var preview = new DictionaryExchangeService().PreviewImport(path, []);

        Assert.False(preview.IsValid);
        Assert.Contains(preview.Errors, error => error.Contains("version", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Errors, error => error.Contains("threshold", StringComparison.OrdinalIgnoreCase));
    }

    private static DictionaryEntryRecord Entry(string id, string phrase, string replacement, double threshold) => new()
    {
        Id = id,
        Phrase = phrase,
        Replacement = replacement,
        MatchingThreshold = threshold
    };
}
