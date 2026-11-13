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

    [Theory]
    [InlineData("[{\"word\":\"museli\",\"replacement\":\"Muesli\",\"matching_threshold\":0.9}]")]
    [InlineData("{\"custom_words\":[{\"word\":\"museli\",\"replacement\":\"Muesli\",\"matching_threshold\":0.9}]}")]
    public void PreviewAcceptsMacAndCliDictionaryFormats(string json)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "dictionary.json");
        File.WriteAllText(path, json);

        var preview = new DictionaryExchangeService().PreviewImport(path, []);

        Assert.True(preview.IsValid, string.Join(" ", preview.Errors));
        var entry = Assert.Single(preview.Additions);
        Assert.Equal("museli", entry.Phrase);
        Assert.Equal("Muesli", entry.Replacement);
        Assert.Equal(.9, entry.MatchingThreshold);
        Assert.StartsWith("dictentry_", entry.Id, StringComparison.Ordinal);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidImportDoesNotMoveOrRewriteTheSourceFile()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "dictionary.json");
        File.WriteAllText(path, "{broken json");

        var preview = new DictionaryExchangeService().PreviewImport(path, []);

        Assert.False(preview.IsValid);
        Assert.Equal("{broken json", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    private static DictionaryEntryRecord Entry(string id, string phrase, string replacement, double threshold) => new()
    {
        Id = id,
        Phrase = phrase,
        Replacement = replacement,
        MatchingThreshold = threshold
    };
}
