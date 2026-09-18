using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Interop;
using Muesli.Windows.Services.Persistence;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Focused tests for the shared Swift dictation bridge. These exercise the real native library, so
/// <c>MuesliCoreABI.dll</c> and the Swift runtime must be resolvable on the test process PATH.
/// </summary>
public sealed class SwiftDictationBridgeTests : IDisposable
{
    private readonly string _directory;

    public SwiftDictationBridgeTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "muesli-swift-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DatabasePath => SharedDictationCorePaths.DatabasePathFor(_directory);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));

    [Fact]
    public void AbiVersionMatchesExpectation()
    {
        Assert.Equal(1u, SharedDictationStore.ExpectedAbiVersion);
        using var store = new SharedDictationStore(DatabasePath);
        Assert.Equal(Path.GetFullPath(DatabasePath), store.DatabasePath);
    }

    [Fact]
    public void InsertThenRecentRoundTripPreservesFields()
    {
        using var store = new SharedDictationStore(DatabasePath);
        var id = store.Insert(new SharedDictationInsert(
            Text: "Hello Zürich café 東京 — punctuation!",
            DurationSeconds: 3.5,
            AppContext: "com.example.editor",
            Source: "dictation",
            TargetAppName: "Editor",
            TargetAppBundleId: "com.example.editor",
            StartedAt: Utc(2026, 9, 18, 10, 0, 0),
            EndedAt: Utc(2026, 9, 18, 10, 0, 3)));

        Assert.True(id > 0);
        var recent = store.Recent(10);
        var record = Assert.Single(recent);
        Assert.Equal(id.ToString(), record.Id);
        Assert.Equal("Hello Zürich café 東京 — punctuation!", record.Text);
        Assert.Equal(3500, record.DurationMs);
        Assert.Equal(Utc(2026, 9, 18, 10, 0, 3), record.Timestamp);
    }

    [Fact]
    public void OrderingAndLimitMatchNewestFirst()
    {
        using var store = new SharedDictationStore(DatabasePath);
        for (var index = 0; index < 5; index++)
        {
            store.Insert(new SharedDictationInsert(
                Text: $"item {index}",
                DurationSeconds: 1.0,
                StartedAt: Utc(2026, 9, 10 + index, 10, 0, 0),
                EndedAt: Utc(2026, 9, 10 + index, 10, 0, 1)));
        }

        Assert.Equal(new[] { "item 4", "item 3", "item 2", "item 1", "item 0" },
            store.Recent(10).Select(item => item.Text));
        Assert.Equal(new[] { "item 4", "item 3" }, store.Recent(2).Select(item => item.Text));
    }

    [Fact]
    public void EmptyAndLargeTranscriptsRoundTrip()
    {
        using var store = new SharedDictationStore(DatabasePath);
        store.Insert(new SharedDictationInsert("", 0.1));
        var large = string.Concat(Enumerable.Repeat("word ", 20_000));
        store.Insert(new SharedDictationInsert(large, 12.0));

        var recent = store.Recent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(string.Empty, recent[1].Text);
        Assert.Equal(large.Length, recent[0].Text.Length);
    }

    [Fact]
    public void InsertRejectsNullArgument()
    {
        using var store = new SharedDictationStore(DatabasePath);
        Assert.Throws<ArgumentNullException>(() => store.Insert(null!));
    }

    [Fact]
    public void RecentRejectsNonPositiveLimit()
    {
        using var store = new SharedDictationStore(DatabasePath);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Recent(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Recent(-3));
    }

    [Fact]
    public void OpenRejectsUnusablePathWithDomainException()
    {
        var parentIsFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(parentIsFile, "x");
        var invalid = Path.Combine(parentIsFile, "muesli-core.db");
        var exception = Assert.Throws<SwiftDictationBridgeException>(() => new SharedDictationStore(invalid));
        Assert.NotEqual(SwiftDictationErrorCode.Ok, exception.Code);
    }

    [Fact]
    public void DataPersistsAcrossReopen()
    {
        using (var first = new SharedDictationStore(DatabasePath))
        {
            first.Insert(new SharedDictationInsert("persisted row", 1.0, EndedAt: Utc(2026, 9, 18, 12, 0, 0)));
        }

        using var reopened = new SharedDictationStore(DatabasePath);
        Assert.Equal(new[] { "persisted row" }, reopened.Recent(10).Select(item => item.Text));
    }

    [Fact]
    public void RepeatedOpenInsertReadDisposeDoesNotLeakOrCorrupt()
    {
        for (var index = 0; index < 25; index++)
        {
            using var store = new SharedDictationStore(DatabasePath);
            store.Insert(new SharedDictationInsert($"cycle {index}", 0.5));
            Assert.Equal(index + 1, store.Recent(100).Count);
        }
    }

    [Fact]
    public void ConcurrentReadsAndWritesAreSafe()
    {
        using var store = new SharedDictationStore(DatabasePath);
        Parallel.For(0, 40, index =>
        {
            store.Insert(new SharedDictationInsert($"thread {index}", 1.0));
            _ = store.Recent(5);
        });

        Assert.Equal(40, store.Recent(100).Count);
    }

    [Fact]
    public void DisposedStoreRejectsUse()
    {
        var store = new SharedDictationStore(DatabasePath);
        store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => store.Recent(1));
        Assert.Throws<ObjectDisposedException>(() => store.Insert(new SharedDictationInsert("x", 1.0)));
    }

    /// <summary>
    /// The Windows SQLite schema and the shared Swift schema are not interchangeable. Opening a
    /// Windows-schema database must not silently adopt or rewrite its rows: the shared insert fails
    /// deterministically and the legacy row is left intact. This is the evidence behind the
    /// migration product decision.
    /// </summary>
    [Fact]
    public void WindowsSchemaDatabaseIsNotSilentlyAdopted()
    {
        var databasePath = Path.Combine(_directory, "windows-schema.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText =
                "CREATE TABLE dictations (id TEXT PRIMARY KEY NOT NULL, created_at_utc INTEGER NOT NULL, " +
                "updated_at_utc INTEGER NOT NULL, title TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '', " +
                "duration_ms INTEGER NOT NULL DEFAULT 0, model_profile TEXT NOT NULL DEFAULT '', folder_id TEXT NULL, " +
                "word_count INTEGER NOT NULL DEFAULT 0, audio_path TEXT NULL, audio_retained INTEGER NOT NULL DEFAULT 0);";
            create.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO dictations (id, created_at_utc, updated_at_utc, text, duration_ms) " +
                "VALUES ('win-1', 1, 1, 'legacy row', 1000);";
            insert.ExecuteNonQuery();
        }

        // The shared store cannot even migrate a Windows-schema database: its indices and columns
        // (timestamp/duration_seconds/raw_text) do not exist here. It must fail rather than adopt it.
        var exception = Assert.Throws<SwiftDictationBridgeException>(() => new SharedDictationStore(databasePath));
        Assert.NotEqual(SwiftDictationErrorCode.Ok, exception.Code);

        using var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        verify.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT text FROM dictations WHERE id = 'win-1';";
        Assert.Equal("legacy row", read.ExecuteScalar() as string);
    }

    /// <summary>
    /// Parity for the supported slice: the same text, duration and instant produce the same
    /// user-visible row whether it is written through the existing JSON history path or the shared
    /// Swift core.
    /// </summary>
    [Fact]
    public void SupportedSliceMatchesExistingJsonHistoryBehavior()
    {
        var jsonStore = new JsonLibraryHistoryAdapter(new AppDataStore(_directory));
        var ended = Utc(2026, 9, 18, 10, 0, 3);
        jsonStore.SaveDictations(new[]
        {
            new PersistedDictation("json-1", ended.UtcDateTime, "parity text", 3500, ""),
        });

        using var swift = new SharedDictationStore(DatabasePath);
        swift.Insert(new SharedDictationInsert(
            Text: "parity text",
            DurationSeconds: 3.5,
            StartedAt: Utc(2026, 9, 18, 10, 0, 0),
            EndedAt: ended));

        var jsonRow = Assert.Single(jsonStore.LoadDictations());
        var swiftRow = Assert.Single(swift.Recent(1));
        Assert.Equal(jsonRow.Text, swiftRow.Text);
        Assert.Equal(jsonRow.DurationMs, swiftRow.DurationMs);
        Assert.Equal(jsonRow.Timestamp, swiftRow.Timestamp);
    }
}
