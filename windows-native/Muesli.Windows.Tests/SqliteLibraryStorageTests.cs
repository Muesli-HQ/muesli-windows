using Microsoft.Data.Sqlite;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// SQLite is the only history store: what a launch does with each state a profile can be in,
/// and the rolling backups.
/// </summary>
public sealed class SqliteLibraryStorageTests
{
    private static readonly DictionaryEntryRecord Rule = new()
    {
        Id = "rule-α", Phrase = "mues lee", Replacement = "Muesli", MatchingThreshold = 0.9123456789
    };
    private static readonly PersistedDictation Dictation = new("d", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        "mues lee helps", 1234, "model");

    // ---- Startup ---------------------------------------------------------------------------

    [Fact]
    public void ANewProfileStartsOnAnEmptySqliteLibraryAndKeepsWhatItSaves()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);
        using (var context = new WinUiLibraryContext(profile))
        {
            var adapter = Assert.IsType<SqliteLibraryHistoryAdapter>(context.History);
            Assert.Equal(PersistenceSchema.CurrentVersion, adapter.Store.SchemaVersion);
            Assert.Empty(adapter.LoadDictations());
            adapter.AppendDictation(Dictation);
        }

        // The only folder the library adds beside the database is its backups.
        Assert.DoesNotContain(Directory.EnumerateDirectories(profile.DataDirectory), path =>
            Path.GetFileName(path) != LibraryDatabaseBackup.DirectoryName);
        using var restarted = new WinUiLibraryContext(profile);
        Assert.Equal(Dictation, Assert.Single(restarted.History.LoadDictations()));
    }

    [Fact]
    public void TheSchemaIsOneVersionStampedWithTheMuesliApplicationId()
    {
        using var directory = new TestDirectory();
        using var database = MuesliDatabase.Open(directory.File("muesli.db"));
        Assert.Equal(1, database.SchemaVersion);
        Assert.Equal(new[] { 1 }, database.Read(SchemaMigrator.ReadHistory).Select(item => item.Version));
        Assert.Equal(PersistenceSchema.ApplicationId, database.Read(connection =>
            Db.Command(connection, "PRAGMA application_id;").ScalarInt32()));
        var tables = database.Read(connection =>
        {
            using var command = Db.Command(connection, "SELECT name FROM sqlite_master WHERE type = 'table';");
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        });
        Assert.Contains("dictionary_entries", tables);
        Assert.DoesNotContain("persistence_cutover_state", tables);
        Assert.DoesNotContain("dictionary_import_state", tables);
    }

    [Fact]
    public void ADatabaseFromANewerBuildIsRefusedUntouchedWithAnActionableMessage()
    {
        using var directory = new TestDirectory();
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        Execute(path, "PRAGMA user_version = 999;");
        var before = File.ReadAllBytes(path);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseFromNewerBuild, result.Outcome);
        Assert.Contains("Update Muesli", result.Message);
        Assert.False(result.CanRestoreBackup);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ADatabaseFromAPreReleaseBuildIsRefusedRatherThanGuessedAt(int preReleaseVersion)
    {
        // Development builds wrote schema versions 1 to 3 with no application id. Their numbers
        // overlap the released schema, so the id is what tells them apart.
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        Execute(path, $"CREATE TABLE dictations (id TEXT PRIMARY KEY, text TEXT); PRAGMA user_version = {preReleaseVersion};");
        var before = File.ReadAllBytes(path);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseFromPreReleaseBuild, result.Outcome);
        Assert.Contains("pre-release", result.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void JsonFilesLeftByPreReleaseBuildsAreIgnoredAndNeverTouched()
    {
        // Development builds kept history in windows-*.json. This build neither reads nor
        // converts them: the profile simply starts on an empty SQLite library.
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("windows-dictations.json"), "[{\"id\":\"old\",\"text\":\"old history\"}]");
        File.WriteAllText(directory.File("windows-meetings.json.corrupt-20260101-000000000-abc.json"), "{ broken");
        var before = Directory.EnumerateFiles(directory.Path).ToDictionary(path => path, File.ReadAllBytes);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.True(result.IsReady);
        Assert.True(result.CreatedDatabase);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Empty(adapter.LoadDictations());
        adapter.AppendDictation(Dictation);
        Assert.True(adapter.DeleteDictation(Dictation.Id));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    // ---- Backups ---------------------------------------------------------------------------

    [Fact]
    public void BackupsAreTakenAtMostOncePerIntervalAndTheOldestAreRemoved()
    {
        using var directory = new TestDirectory();
        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        store.Dictations.Upsert(new DictationRecord { Id = "d", Text = "kept" });
        var start = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        Assert.NotNull(LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path, now: start));
        Assert.Null(LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path, now: start.AddHours(23)));
        for (var day = 1; day <= 9; day++)
            Assert.NotNull(LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path, now: start.AddDays(day)));

        var backups = LibraryDatabaseBackup.List(directory.Path);
        Assert.Equal(LibraryDatabaseBackup.DefaultRetainedCount, backups.Count);
        Assert.Equal(start.AddDays(9), backups[0].CreatedAtUtc);
        Assert.Equal(start.AddDays(3), backups[^1].CreatedAtUtc);
        // A backup is a complete, independent database.
        using var copy = MuesliPersistenceStore.Open(backups[0].Path);
        Assert.Equal("kept", copy.Dictations.Find("d")!.Text);
    }

    [Fact]
    public void AnInterruptedBackupIsNeverListedOrRestored()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(LibraryDatabaseBackup.DirectoryFor(directory.Path));
        File.WriteAllText(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "muesli-20260101-090000000.db.partial"), "half");
        File.WriteAllText(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "notes.txt"), "user file");

        Assert.Empty(LibraryDatabaseBackup.List(directory.Path));
        Assert.Null(LibraryDatabaseBackup.RestoreLatest(directory.Path));
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
    }

    [Fact]
    public void ADamagedDatabaseIsReportedAndCanBeRestoredFromTheNewestGoodBackup()
    {
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        using (var store = MuesliPersistenceStore.Open(path))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d", Text = "before the damage" });
            LibraryDatabaseBackup.Create(store.Database, directory.Path, now: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        }
        // A newer backup that is itself damaged must be skipped, not restored.
        File.WriteAllText(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "muesli-20260102-000000000.db"), "not a database");
        File.WriteAllText(path, "broken database");

        var damaged = LibraryDatabaseStartup.Prepare(directory.Path);
        Assert.Equal(LibraryStartupOutcome.DatabaseUnreadable, damaged.Outcome);
        Assert.True(damaged.CanRestoreBackup);
        Assert.Equal("broken database", File.ReadAllText(path));

        var restored = LibraryDatabaseBackup.RestoreLatest(directory.Path);
        Assert.NotNull(restored);
        Assert.EndsWith("muesli-20260101-000000000.db", restored!.Path);
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        using var reopened = MuesliPersistenceStore.Open(path);
        Assert.Equal("before the damage", reopened.Dictations.Find("d")!.Text);
        // The damaged file is kept under a new name rather than deleted.
        var replaced = Assert.Single(Directory.EnumerateFiles(directory.Path, "muesli.db.replaced-*"));
        Assert.Equal("broken database", File.ReadAllText(replaced));
    }

    [Fact]
    public void ADamagedDatabaseWithNoBackupIsReportedWithoutOfferingARestore()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(PersistencePaths.DatabasePathFor(directory.Path), "broken database");
        var result = LibraryDatabaseStartup.Prepare(directory.Path);
        Assert.Equal(LibraryStartupOutcome.DatabaseUnreadable, result.Outcome);
        Assert.False(result.CanRestoreBackup);
        Assert.Contains("No backup is available", result.Message);
    }

    [Fact]
    public void AMissingDatabaseWithBackupsIsNotSilentlyReplacedByAnEmptyLibrary()
    {
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        using (var store = MuesliPersistenceStore.Open(path))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d", Text = "still in the backup" });
            LibraryDatabaseBackup.Create(store.Database, directory.Path);
        }
        File.Delete(path);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseMissing, result.Outcome);
        Assert.True(result.CanRestoreBackup);
        Assert.False(File.Exists(path));
        Assert.NotNull(LibraryDatabaseBackup.RestoreLatest(directory.Path));
        using var reopened = MuesliPersistenceStore.Open(path);
        Assert.Equal("still in the backup", reopened.Dictations.Find("d")!.Text);
    }

    [Fact]
    public void TheAppBacksUpOnLaunchAndADeletionLeavesNoBackupHoldingTheDeletedText()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);
        using (var context = new WinUiLibraryContext(profile))
        {
            context.History.AppendDictation(Dictation);
            context.History.AppendDictation(Dictation with { Id = "keep", Text = "keep this one" });
        }
        using (var context = new WinUiLibraryContext(profile))
        {
            // Force a backup that holds both rows, as an earlier day's launch would have.
            var adapter = Assert.IsType<SqliteLibraryHistoryAdapter>(context.History);
            LibraryDatabaseBackup.Create(adapter.Store.Database, profile.DataDirectory,
                now: DateTimeOffset.UtcNow.AddDays(-1));
            Assert.True(context.DeleteDictation("d"));
        }

        var backups = LibraryDatabaseBackup.List(profile.DataDirectory);
        Assert.NotEmpty(backups);
        foreach (var backup in backups)
        {
            using var copy = MuesliPersistenceStore.Open(backup.Path);
            Assert.Null(copy.Dictations.Find("d"));
            Assert.NotNull(copy.Dictations.Find("keep"));
            Assert.Empty(copy.Search.Search(new SearchQuery { Text = "lee" }));
        }
    }

    [Theory]
    [InlineData("muesli.db-wal", false)]
    [InlineData("muesli.db-shm", false)]
    [InlineData("muesli.db-wal", true)]
    public void AnOrphanedWriteAheadLogIsNeverOverwrittenByANewDatabase(string orphan, bool withBackup)
    {
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        if (withBackup)
        {
            using (var store = MuesliPersistenceStore.Open(path))
                LibraryDatabaseBackup.Create(store.Database, directory.Path);
            File.Delete(path);
        }
        File.WriteAllText(directory.File(orphan), "recent history that only the log holds");

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseMissing, result.Outcome);
        Assert.Contains(orphan, result.Message);
        Assert.Equal(withBackup, result.CanRestoreBackup);
        Assert.False(File.Exists(path));
        Assert.Equal("recent history that only the log holds", File.ReadAllText(directory.File(orphan)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEmptyDatabaseFileIsNotStampedAsANewLibrary(bool withBackup)
    {
        // An interrupted copy can leave a zero-byte muesli.db. SQLite opens it happily, so without
        // this check it would be given a schema and shown as an empty, healthy library.
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        if (withBackup)
        {
            using (var store = MuesliPersistenceStore.Open(path))
            {
                store.Dictations.Upsert(new DictationRecord { Id = "d", Text = "only in the backup" });
                LibraryDatabaseBackup.Create(store.Database, directory.Path);
            }
            File.Delete(path);
        }
        File.WriteAllBytes(path, []);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseUnreadable, result.Outcome);
        Assert.Equal(withBackup, result.CanRestoreBackup);
        Assert.Equal(0, new FileInfo(path).Length);
        Assert.Throws<PersistenceException>(() => SqliteLibraryHistoryAdapter.Open(directory.Path));
        if (!withBackup) return;
        Assert.NotNull(LibraryDatabaseBackup.RestoreLatest(directory.Path));
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        using var reopened = MuesliPersistenceStore.Open(path);
        Assert.Equal("only in the backup", reopened.Dictations.Find("d")!.Text);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "muesli.db.restoring-*"));
    }

    [Fact]
    public void ADeletionPurgeIsRecordedAtOnceAndFinishedOnTheNextLaunchIfTheAppWasClosed()
    {
        using var directory = new TestDirectory();
        var start = DateTimeOffset.UtcNow;
        using (var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path)))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d", Text = "sensitive words" });
            store.Dictations.Upsert(new DictationRecord { Id = "keep", Text = "ordinary words" });
            LibraryDatabaseBackup.Create(store.Database, directory.Path, now: start.AddDays(-1));
            store.Dictations.Delete("d");

            // What the app does before the deletion returns: no backup survives this call.
            LibraryDatabaseBackup.RecordDeletionAndPurge(directory.Path);
            Assert.Empty(LibraryDatabaseBackup.List(directory.Path));
            Assert.NotNull(LibraryDatabaseBackup.PurgePendingSince(directory.Path));

            // A copy that began before the deletion lands late; it must not be kept.
            var purgeMarker = Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "purge-pending");
            var recordedAt = File.GetLastWriteTimeUtc(purgeMarker);
            File.SetLastWriteTimeUtc(purgeMarker, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(LibraryDatabaseBackup.Create(store.Database, directory.Path));
            Assert.Empty(LibraryDatabaseBackup.List(directory.Path));
            Assert.Empty(Directory.EnumerateFiles(LibraryDatabaseBackup.DirectoryFor(directory.Path), "*.partial"));
            File.SetLastWriteTimeUtc(purgeMarker, recordedAt);
        }

        // The app was closed before it wrote the replacement. Put a stale copy back to stand for
        // one that slipped through, then launch again.
        File.WriteAllText(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "muesli-20260101-000000000.db"), "stale copy with sensitive words");
        using (var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path)))
        {
            Assert.NotNull(LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path));
        }

        var backup = Assert.Single(LibraryDatabaseBackup.List(directory.Path));
        Assert.Null(LibraryDatabaseBackup.PurgePendingSince(directory.Path));
        using var copy = MuesliPersistenceStore.Open(backup.Path);
        Assert.Null(copy.Dictations.Find("d"));
        Assert.NotNull(copy.Dictations.Find("keep"));
    }

    [Fact]
    public void APendingPurgeIsHonouredEvenWhenABackupIsNotDue()
    {
        using var directory = new TestDirectory();
        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        var first = LibraryDatabaseBackup.Create(store.Database, directory.Path, now: DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.NotNull(first);
        Assert.Null(LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path));

        Directory.CreateDirectory(LibraryDatabaseBackup.DirectoryFor(directory.Path));
        File.WriteAllText(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "purge-pending"), "1");
        File.SetLastWriteTimeUtc(Path.Combine(LibraryDatabaseBackup.DirectoryFor(directory.Path), "purge-pending"), DateTime.UtcNow.AddMinutes(-1));

        var replacement = LibraryDatabaseBackup.CreateIfDue(store.Database, directory.Path);
        Assert.NotNull(replacement);
        Assert.NotEqual(first!.Path, replacement!.Path);
        Assert.False(File.Exists(first.Path));
        Assert.Null(LibraryDatabaseBackup.PurgePendingSince(directory.Path));
    }

    // ---- Corrections commit as one unit ------------------------------------------------------

    [Fact]
    public void EditAndRememberCommitTogetherPreserveMetadataAndPersistThroughRestartAndExchange()
    {
        using var directory = new TestDirectory();
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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

    // ---- Remembered rules -------------------------------------------------------------------

    [Theory]
    [InlineData("Iphone", "iPhone", "My Iphone rings", "My iPhone rings")]
    [InlineData("github", "GitHub", "Github and github", "GitHub and GitHub")]
    [InlineData("there", "their", "There it is", "Their it is")]
    [InlineData("muesli", "muesli", "Muesli is ready", "Muesli is ready")]
    public void AReplacementWithItsOwnCapitalsIsWrittenAsTyped(string phrase, string replacement, string text, string expected)
    {
        var rule = new DictionaryEntryRecord { Id = "r", Phrase = phrase, Replacement = replacement, MatchingThreshold = 0.98 };
        Assert.Equal(expected, DictionaryCorrectionService.Apply(text, [rule]));
    }

    [Fact]
    public void APhraseJoinedByANonBreakingSpaceIsNotRememberedAsARuleThatCouldNeverMatch()
    {
        const string text = "mues lee helps";
        Assert.False(DictionaryCorrectionService.CanRememberSelection(text, 0, 8));
        Assert.True(DictionaryCorrectionService.CanRememberSelection("mues lee helps", 0, 8));

        using var directory = new TestDirectory();
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        adapter.AppendDictation(Dictation with { Text = text });
        var service = new DictationCorrectionService(adapter);
        var selection = new DictationCorrectionSelection("d", text, 0, 8);
        Assert.Equal(DictationCorrectionOutcome.Invalid, service.Save(selection, "Muesli", remember: true).Outcome);
        Assert.Empty(adapter.LoadDictionary());
        // The one-off edit of the same selection is still available.
        Assert.True(service.Save(selection, "Muesli", remember: false).Succeeded);
        Assert.Equal("Muesli helps", adapter.Store.Dictations.Find("d")!.Text);
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        PersistenceSchema.Execute(connection, sql);
    }
}
