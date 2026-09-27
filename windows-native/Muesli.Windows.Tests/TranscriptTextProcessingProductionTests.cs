using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.Services.Text;
using Xunit;

namespace Muesli.Windows.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "muesli-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Proves the production dictation path runs through the shared Swift text processor while the
/// existing Windows SQLite schema remains the persistence authority and no Swift database appears.
/// </summary>
[Collection("TranscriptTextProcessing")]
public sealed class TranscriptTextProcessingProductionTests
{
    private static string BridgePath() =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "MuesliCoreABI.dll");

    [BridgeFact]
    public async Task CompletedDictationNormalizesThroughSwiftAndStoresInWindowsSqlite()
    {
        using var temp = new TempDirectory();
        TranscriptTextProcessing.Initialize(new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor()));
        try
        {
            var pipeline = new TranscriptionPipelineService(
                new NativeTextCleanupService(),
                logService: null,
                textProcessor: TranscriptTextProcessing.Current);

            var normalized = await pipeline.PrepareDictationTextAsync(
                "  Hello   Zürich \n  world!  ",
                enableCleanup: false,
                removeFillerWords: true,
                dictionaryEntries: Array.Empty<DictionaryEntryRecord>());

            Assert.Equal("Hello Zürich world!", normalized);

            var dbPath = System.IO.Path.Combine(temp.Path, "muesli.db");
            using (var store = MuesliPersistenceStore.Open(dbPath))
            {
                store.Dictations.Upsert(new DictationRecord
                {
                    Id = "dict-e2e-1",
                    Text = normalized,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });

                var stored = Assert.Single(store.Dictations.List(new DictationQuery { Limit = 10 }));
                Assert.Equal(normalized, stored.Text);
            }

            using (var reopened = MuesliPersistenceStore.Open(dbPath))
            {
                Assert.Equal(normalized, Assert.Single(reopened.Dictations.List(new DictationQuery { Limit = 10 })).Text);
            }

            Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "muesli-core.db")));
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
        }
    }

    [BridgeFact]
    public void TextProcessingDoesNotAlterExistingWindowsRecordsOrCreateASwiftDatabase()
    {
        using var temp = new TempDirectory();
        var dbPath = System.IO.Path.Combine(temp.Path, "muesli.db");
        using (var store = MuesliPersistenceStore.Open(dbPath))
        {
            store.Dictations.Upsert(new DictationRecord
            {
                Id = "legacy-1",
                Text = "legacy  text  stays",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        TranscriptTextProcessing.Initialize(new SwiftTranscriptTextProcessor(BridgePath(), new ManagedTranscriptTextProcessor()));
        try
        {
            _ = TranscriptTextProcessing.Current.NormalizeTranscript("  other   text ");
            _ = TranscriptTextProcessing.Current.CountWords("one two");
            _ = Muesli.Windows.Core.Insights.LibraryMetrics.CountWords("one two three");
        }
        finally
        {
            TranscriptTextProcessing.ResetToManaged();
        }

        using (var reopened = MuesliPersistenceStore.Open(dbPath))
        {
            Assert.Equal("legacy  text  stays", Assert.Single(reopened.Dictations.List(new DictationQuery { Limit = 10 })).Text);
        }

        Assert.False(File.Exists(System.IO.Path.Combine(temp.Path, "muesli-core.db")));
    }
}
