using Microsoft.Data.Sqlite;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

public sealed class SqliteDefaultStorageTests
{
    private static readonly DictionaryEntryRecord Rule = new()
    {
        Id = "rule-α", Phrase = "mues lee", Replacement = "Muesli", MatchingThreshold = 0.9123456789
    };
    private static readonly PersistedDictation Dictation = new("d", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        "mues lee helps", 1234, "model");

    [Fact]
    public void FreshProductionCompositionCreatesSqliteAuthorityWithoutLegacySnapshots()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);
        using (var context = new WinUiLibraryContext(profile))
        {
            var adapter = Assert.IsType<SqliteLibraryHistoryAdapter>(context.History);
            Assert.Equal(PersistenceAuthority.Sqlite, PersistenceCutoverState.Read(adapter.Store.Database).Authority);
            Assert.True(DictionaryMigration.IsComplete(adapter.Store.Database));
            Assert.Empty(adapter.LoadDictations());
            adapter.AppendDictation(Dictation);
        }
        Assert.True(SqliteAuthorityMarker.Exists(profile.DataDirectory));
        Assert.Empty(Directory.EnumerateDirectories(profile.DataDirectory, PersistenceCutover.JsonSnapshotDirectoryPrefix + "*"));
        Assert.False(File.Exists(Path.Combine(profile.DataDirectory, JsonHistorySnapshotReader.DictationsFileName)));
        using var restarted = new WinUiLibraryContext(profile);
        Assert.Equal(Dictation, Assert.Single(restarted.History.LoadDictations()));
    }

    [Fact]
    public void DictionaryMigratesEveryPersistedFieldAndNeverReimportsRetainedJson()
    {
        using var directory = new TestDirectory();
        var legacy = new AppDataStore(directory.Path);
        legacy.SaveDictations([Dictation]);
        legacy.SaveDictionary([Rule]);
        var original = File.ReadAllBytes(directory.File(PersistenceCutover.DictionaryFileName));
        var first = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(first.Succeeded, first.Failure);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first.JsonSnapshotDirectory!, PersistenceCutover.DictionaryFileName)));
        using (var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path))
        {
            Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
            adapter.SaveDictionary([Rule with { Replacement = "new value", MatchingThreshold = 0.98 }]);
        }
        File.WriteAllText(directory.File(PersistenceCutover.DictionaryFileName), "{ corrupt retained JSON");
        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, new PersistenceCutover(directory.Path).EnsureMigrated().Outcome);
        using var reopened = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal("new value", Assert.Single(reopened.LoadDictionary()).Replacement);
        Assert.Equal(Dictation, Assert.Single(reopened.LoadDictations()));
    }

    [Fact]
    public void VersionTwoSqliteUpgradesDictionaryWithoutReadingStaleHistory()
    {
        using var directory = new TestDirectory();
        CreateVersionTwoLibrary(directory);
        var legacy = new AppDataStore(directory.Path);
        legacy.SaveDictionary([Rule]);
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName), "unreadable stale JSON");
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(result.Succeeded, result.Failure);
        using var store = MuesliPersistenceStore.Open(result.DatabasePath);
        Assert.Equal(3, store.SchemaVersion);
        Assert.Equal("newer SQLite text", store.Dictations.Find("d")!.Text);
        Assert.Equal(Rule, Assert.Single(store.Dictionary.List()));
        Assert.Equal(new[] { 1, 2, 3 }, store.Database.Read(SchemaMigrator.ReadHistory).Select(item => item.Version));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedDictionaryCommitRollsBackAndRetriesExactlyOnce(bool existingSqlite)
    {
        using var directory = new TestDirectory();
        if (existingSqlite) CreateVersionTwoLibrary(directory);
        else new AppDataStore(directory.Path).SaveDictations([Dictation]);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        var failed = new PersistenceCutover(directory.Path, options: new PersistenceCutoverOptions
        {
            BeforeDictionaryCommit = () => throw new IOException("Simulated interruption")
        }).EnsureMigrated();
        Assert.False(failed.Succeeded);
        if (existingSqlite)
        {
            using var store = MuesliPersistenceStore.Open(failed.DatabasePath);
            Assert.False(DictionaryMigration.IsComplete(store.Database));
            Assert.Empty(store.Dictionary.List());
            Assert.Equal("newer SQLite text", store.Dictations.Find("d")!.Text);
        }
        else Assert.False(File.Exists(failed.DatabasePath));
        var retry = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(retry.Succeeded, retry.Failure);
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
        Assert.Single(adapter.LoadDictations());
        Assert.Single(Directory.EnumerateDirectories(directory.Path, PersistenceCutover.JsonSnapshotDirectoryPrefix + "*"));
    }

    [Fact]
    public void CommittedHistoryImportWithoutAuthorityIsSafelyResumed()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        Assert.True(new JsonToSqliteMigrationService(directory.Path).Migrate().Succeeded);
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Single(adapter.LoadDictations());
        Assert.Single(adapter.LoadDictionary());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("newer")]
    public void UnavailableAuthoritativeDatabaseNeverCreatesAnEmptyLibraryOrUsesJson(string kind)
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        var first = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(first.Succeeded);
        if (kind == "missing") File.Move(first.DatabasePath, directory.File("explicit-recovery.db"));
        if (kind == "corrupt") File.WriteAllText(first.DatabasePath, "broken database");
        if (kind == "newer")
        {
            using var connection = new SqliteConnection($"Data Source={first.DatabasePath};Pooling=False");
            connection.Open();
            PersistenceSchema.Execute(connection, "PRAGMA user_version = 999;");
        }
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Failure!);
        Assert.Single(new AppDataStore(directory.Path).LoadDictations());
        if (kind == "missing") Assert.False(File.Exists(first.DatabasePath));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("{\"schemaVersion\":999,\"data\":[]}")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"same\",\"phrase\":\"a\"},{\"id\":\"same\",\"phrase\":\"b\"}]")]
    public void InvalidDictionaryBlocksStartupInsteadOfSilentlyLosingRules(string content)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File(PersistenceCutover.DictionaryFileName), content);
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(result.DatabasePath));
        Assert.Equal(content, File.ReadAllText(directory.File(PersistenceCutover.DictionaryFileName)));
    }

    [Fact]
    public void MissingPrimaryHistoryAndDictionaryRecoverTheirSurvivingBackups()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        foreach (var name in new[] { JsonHistorySnapshotReader.DictationsFileName, PersistenceCutover.DictionaryFileName })
            File.Move(directory.File(name), directory.File(name + ".bak"));
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal(Dictation, Assert.Single(adapter.LoadDictations()));
        Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
    }

    [Fact]
    public void PendingFolderMutationIsImportedWithoutRewritingItsOriginals()
    {
        using var directory = new TestDirectory();
        var meeting = new PersistedMeeting { Id = "m", Title = "preserved", FolderId = "new-folder", Transcript = "raw" };
        new AtomicJsonFile().Save(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName), new
        {
            Folders = new[] { new PersistedMeetingFolder("new-folder", "new") }, Meetings = new[] { meeting }
        });
        var original = File.ReadAllBytes(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName));
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(result.Succeeded, result.Failure);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal("new-folder", Assert.Single(adapter.LoadMeetings()).FolderId);
        Assert.Equal("new-folder", Assert.Single(adapter.LoadMeetingFolders()).Id);
        Assert.Equal(original, File.ReadAllBytes(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName)));
    }

    [Fact]
    public void EditAndRememberCommitTogetherPreserveMetadataAndPersistThroughRestartAndExchange()
    {
        using var directory = new TestDirectory();
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using (var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path))
        {
            adapter.Store.Dictations.Upsert(new DictationRecord { Id = "d", Text = Dictation.Text,
                Title = "title", AudioPath = "owned.wav", AudioRetained = true });
            Assert.True(adapter.SaveCorrection("d", Dictation.Text, "Muesli helps", Rule.Phrase, Rule.Replacement).Succeeded);
            var saved = adapter.Store.Dictations.Find("d")!;
            Assert.Equal("title", saved.Title);
            Assert.Equal("owned.wav", saved.AudioPath);
            Assert.True(saved.AudioRetained);
            Assert.Equal(2, saved.WordCount);
            Assert.Single(adapter.Search(new SearchQuery { Text = "Muesli" }));
            Assert.Empty(adapter.Search(new SearchQuery { Text = "lee" }));
            new DictionaryExchangeService().Export(directory.File("export.json"), adapter.LoadDictionary());
        }
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var reopened = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal("Muesli helps", reopened.Store.Dictations.Find("d")!.Text);
        var preview = new DictionaryExchangeService().PreviewImport(directory.File("export.json"), []);
        Assert.True(preview.IsValid);
        Assert.Equal(reopened.LoadDictionary(), preview.Incoming);
        Assert.Equal("Muesli helps", DictionaryCorrectionService.Apply(Dictation.Text, reopened.LoadDictionary()));
        Assert.True(reopened.SaveCorrection("d", "Muesli helps", "One-off edit", null, null).Succeeded);
        Assert.Single(reopened.LoadDictionary());
    }

    [Fact]
    public void FailedTextWriteRollsBackNewRuleAndSearchIndexTogether()
    {
        using var directory = new TestDirectory();
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        adapter.AppendDictation(Dictation);
        adapter.Store.Database.Write(connection => PersistenceSchema.Execute(connection, """
            CREATE TRIGGER reject_edit BEFORE UPDATE ON dictations BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """));
        var result = adapter.SaveCorrection("d", Dictation.Text, "Muesli helps", Rule.Phrase, Rule.Replacement);
        Assert.Equal(DictationCorrectionOutcome.Failed, result.Outcome);
        Assert.Empty(adapter.LoadDictionary());
        Assert.Equal(Dictation.Text, adapter.Store.Dictations.Find("d")!.Text);
        Assert.Single(adapter.Search(new SearchQuery { Text = "lee" }));
        Assert.Empty(adapter.Search(new SearchQuery { Text = "Muesli" }));
    }

    [Fact]
    public void ConflictsAndStaleConfirmationsNeverOverwriteRulesOrEditDictation()
    {
        using var directory = new TestDirectory();
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        adapter.AppendDictation(Dictation);
        adapter.SaveDictionary([Rule with { Replacement = "other" }]);
        var conflict = adapter.SaveCorrection("d", Dictation.Text, "Muesli helps", Rule.Phrase, Rule.Replacement);
        Assert.Equal(DictationCorrectionOutcome.Conflict, conflict.Outcome);
        Assert.Equal(Dictation.Text, adapter.Store.Dictations.Find("d")!.Text);
        adapter.SaveDictionary([Rule with { Replacement = "new conflict" }]);
        Assert.Equal(DictationCorrectionOutcome.Conflict,
            adapter.SaveCorrection("d", Dictation.Text, "Muesli helps", Rule.Phrase, Rule.Replacement, conflict.Conflicts).Outcome);
        var confirmed = adapter.LoadDictionary();
        Assert.True(adapter.SaveCorrection("d", Dictation.Text, "Muesli helps", Rule.Phrase, Rule.Replacement, confirmed).Succeeded);
        Assert.Equal(Rule.Id, Assert.Single(adapter.LoadDictionary()).Id);
        Assert.Equal(Rule.MatchingThreshold, Assert.Single(adapter.LoadDictionary()).MatchingThreshold);
        Assert.Equal(DictationCorrectionOutcome.Stale,
            adapter.SaveCorrection("d", Dictation.Text, "stale", "new phrase", "new rule").Outcome);
        Assert.Single(adapter.LoadDictionary());
    }

    [Fact]
    public void ExplicitDeletionRedactsOnlyDeletedRowsFromRecoveryCopiesAndSearch()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation, Dictation with { Id = "keep", Text = "keep recovery" }]);
        var migration = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(migration.Succeeded);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.True(adapter.DeleteDictation("d"));
        Assert.Empty(adapter.Search(new SearchQuery { Text = "lee" }));
        Assert.Single(adapter.LoadDictations());
        Assert.Equal("keep", Assert.Single(JsonHistorySnapshotReader.Read(directory.Path).Dictations).Id);
        Assert.Equal("keep", Assert.Single(JsonHistorySnapshotReader.Read(migration.JsonSnapshotDirectory!).Dictations).Id);
        Assert.True(Directory.Exists(migration.JsonSnapshotDirectory));
    }

    private static void CreateVersionTwoLibrary(TestDirectory directory)
    {
        using var connection = new SqliteConnection($"Data Source={PersistencePaths.DatabasePathFor(directory.Path)};Pooling=False");
        connection.Open();
        SchemaMigrator.Migrate(connection, PersistenceSchema.Migrations.Take(2).ToList());
        PersistenceSchema.Execute(connection, """
            INSERT INTO dictations (id, text, created_at_utc, updated_at_utc) VALUES ('d', 'newer SQLite text', 0, 0);
            UPDATE persistence_cutover_state SET authority = 'sqlite', activated_at_utc = 1 WHERE id = 1;
            """);
    }

    [Fact]
    public void RecoveryRetentionStartsAtDictionaryUpgradeAndDoesNotRemoveUserExports()
    {
        using var directory = new TestDirectory();
        CreateVersionTwoLibrary(directory);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(result.Succeeded);
        Assert.True(File.Exists(directory.File(PersistenceCutover.DictionaryFileName)));
        Assert.NotNull(result.JsonSnapshotDirectory);
        File.WriteAllText(directory.File("my-transcript-export.json"), "user-owned");
        using (var database = MuesliDatabase.Open(result.DatabasePath))
            database.Write(connection => Db.Command(connection,
                "UPDATE dictionary_import_state SET completed_at_utc = $old;")
                .Bind("$old", DateTimeOffset.UtcNow.AddDays(-31)).Execute());
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        Assert.False(File.Exists(directory.File(PersistenceCutover.DictionaryFileName)));
        Assert.False(Directory.Exists(result.JsonSnapshotDirectory));
        Assert.Equal("user-owned", File.ReadAllText(directory.File("my-transcript-export.json")));
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
        Assert.Equal("newer SQLite text", adapter.Store.Dictations.Find("d")!.Text);
    }

    [Fact]
    public void IncompleteRecoverySnapshotIsCompletedOnRetry()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        var stopped = new PersistenceCutover(directory.Path, options: new PersistenceCutoverOptions
        { FailAfterJsonSnapshot = true }).EnsureMigrated();
        Assert.False(stopped.Succeeded);
        File.Delete(Path.Combine(stopped.JsonSnapshotDirectory!, PersistenceCutover.DictionaryFileName));
        var partial = Path.Combine(stopped.JsonSnapshotDirectory!, JsonHistorySnapshotReader.DictationsFileName);
        File.WriteAllBytes(partial, File.ReadAllBytes(partial)[..12]);
        var resumed = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.True(resumed.Succeeded, resumed.Failure);
        Assert.Equal(stopped.JsonSnapshotDirectory, resumed.JsonSnapshotDirectory);
        Assert.True(File.Exists(Path.Combine(resumed.JsonSnapshotDirectory!, PersistenceCutover.DictionaryFileName)));
    }

    [Fact]
    public void InterruptedAuthorityMarkerWriteIsCompletedFromTheAuthoritativeDatabase()
    {
        using var directory = new TestDirectory();
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        File.Delete(SqliteAuthorityMarker.PathFor(directory.Path));
        File.WriteAllText(SqliteAuthorityMarker.PathFor(directory.Path) + ".pending", "partial marker");
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        Assert.True(SqliteAuthorityMarker.Exists(directory.Path));
        Assert.False(File.Exists(SqliteAuthorityMarker.PathFor(directory.Path) + ".pending"));
    }

    [Fact]
    public void LockedLegacySourceIsNotMistakenForAnEmptyProfile()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        using var locked = new FileStream(directory.File(JsonHistorySnapshotReader.DictationsFileName),
            FileMode.Open, FileAccess.Read, FileShare.None);
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(result.DatabasePath));
    }

    [Fact]
    public void QuarantinedHistoryWithoutPrimaryOrBackupIsNotAnEmptyProfile()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName + ".corrupt-previous"), "broken");
        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(result.DatabasePath));
    }

    [Fact]
    public void LockedRecoveryCopyMakesDeletionFailAndKeepsTheSqliteRecord()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        var migration = new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        using var locked = new FileStream(Path.Combine(migration.JsonSnapshotDirectory!, JsonHistorySnapshotReader.DictationsFileName),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.ThrowsAny<IOException>(() => adapter.DeleteDictation("d"));
        Assert.NotNull(adapter.Store.Dictations.Find("d"));
        Assert.Single(adapter.Search(new SearchQuery { Text = "lee" }));
    }
}
