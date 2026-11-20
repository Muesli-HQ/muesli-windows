using Microsoft.Data.Sqlite;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// SQLite is the only history store: what a launch does with each state a profile can be in, the
/// rolling backups, and the one-time developer import of JSON history.
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

        // Nothing JSON-shaped is written for history, and no marker or snapshot is left behind.
        Assert.Empty(LibraryDatabaseStartup.FindLegacyHistoryFiles(profile.DataDirectory));
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

    [Theory]
    [InlineData("windows-dictations.json")]
    [InlineData("windows-meetings.json.bak")]
    [InlineData("windows-dictionary.json")]
    [InlineData("windows-meeting-templates.json.corrupt-20260101-000000000-abc.json")]
    public void LegacyJsonHistoryWithoutADatabaseIsReportedAndNeverOpenedAsAnEmptyLibrary(string fileName)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File(fileName), "[]");
        var before = File.ReadAllBytes(directory.File(fileName));

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.LegacyHistoryNeedsImport, result.Outcome);
        Assert.Contains("import-legacy-history.ps1", result.Message);
        Assert.Contains(fileName, result.Message);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.Equal(before, File.ReadAllBytes(directory.File(fileName)));
        Assert.Equal(new[] { fileName }, Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName));

        var profile = MuesliProfilePaths.Isolated(directory.File("profile"));
        Directory.CreateDirectory(profile.DataDirectory);
        File.WriteAllText(Path.Combine(profile.DataDirectory, fileName), "[]");
        var thrown = Assert.Throws<LibraryUnavailableException>(() => new WinUiLibraryContext(profile));
        Assert.Equal(LibraryStartupOutcome.LegacyHistoryNeedsImport, thrown.Result.Outcome);
        // The refusal released the profile, so a later launch is not locked out.
        File.Delete(Path.Combine(profile.DataDirectory, fileName));
        using var opened = new WinUiLibraryContext(profile);
    }

    [Fact]
    public void SettingsAndRecordingJournalsAreNotMistakenForLegacyHistory()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("windows-settings.json"), "{}");
        File.WriteAllText(directory.File("meeting-session-journal.json"), "{}");
        File.WriteAllText(directory.File("my-transcript-export.json"), "user-owned");
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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

    // ---- One-time developer import ----------------------------------------------------------

    [Fact]
    public void TheImporterBringsHistoryAndDictionaryAcrossAndLeavesTheJsonUntouched()
    {
        using var directory = new TestDirectory();
        var legacy = new AppDataStore(directory.Path);
        legacy.SaveDictations([Dictation]);
        legacy.SaveDictionary([Rule]);
        legacy.SaveMeetings([new PersistedMeeting { Id = "m", Title = "Planning", Transcript = "raw words", CreatedAt = DateTime.UtcNow }]);
        var before = Directory.EnumerateFiles(directory.Path).ToDictionary(path => path, File.ReadAllBytes);

        var result = LegacyJsonHistoryImporter.Import(directory.Path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.Counts.Dictations);
        Assert.Equal(1, result.Counts.Meetings);
        Assert.Equal(1, result.DictionaryEntries);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(
            before.Keys.Append(result.DatabasePath).Order(),
            Directory.EnumerateFiles(directory.Path).Order());
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal(Dictation, Assert.Single(adapter.LoadDictations()));
        Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
        Assert.Equal("Planning", Assert.Single(adapter.LoadMeetings()).Title);
        Assert.Single(adapter.Search(new SearchQuery { Text = "lee" }));
    }

    [Fact]
    public void TheImporterNeverTouchesAnExistingLibrary()
    {
        using var directory = new TestDirectory();
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
        using (var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path))
            adapter.AppendDictation(Dictation with { Id = "live", Text = "live library" });
        new AppDataStore(directory.Path).SaveDictations([Dictation]);

        var result = LegacyJsonHistoryImporter.Import(directory.Path);

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Message);
        using var reopened = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal("live", Assert.Single(reopened.LoadDictations()).Id);
    }

    [Theory]
    [InlineData("windows-dictations.json", "{ broken")]
    [InlineData("windows-meetings.json", "{\"schemaVersion\":999,\"data\":[]}")]
    [InlineData("windows-dictionary.json", "[{\"id\":\"same\",\"phrase\":\"a\"},{\"id\":\"same\",\"phrase\":\"b\"}]")]
    public void AnImportThatCannotBeCompleteCreatesNoLibraryAtAll(string fileName, string content)
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        File.WriteAllText(directory.File(fileName), content);
        if (fileName == "windows-dictations.json") File.Delete(directory.File(fileName + ".bak"));

        var result = LegacyJsonHistoryImporter.Import(directory.Path);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Message);
        Assert.Equal(content, File.ReadAllText(directory.File(fileName)));
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), path =>
            Path.GetFileName(path).StartsWith("muesli.db", StringComparison.Ordinal));
        // The profile is still in the state the app reports, not an empty library.
        Assert.Equal(LibraryStartupOutcome.LegacyHistoryNeedsImport, LibraryDatabaseStartup.Prepare(directory.Path).Outcome);
    }

    [Fact]
    public void TheImporterRecoversAMissingPrimaryFromItsBackupAndSaysSo()
    {
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation]);
        new AppDataStore(directory.Path).SaveDictionary([Rule]);
        foreach (var name in new[] { JsonHistorySnapshotReader.DictationsFileName, JsonHistorySnapshotReader.DictionaryFileName })
            File.Move(directory.File(name), directory.File(name + ".bak"));

        var result = LegacyJsonHistoryImporter.Import(directory.Path);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(result.Warnings, warning => warning.Contains("backup was used"));
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal(Dictation, Assert.Single(adapter.LoadDictations()));
        Assert.Equal(Rule, Assert.Single(adapter.LoadDictionary()));
    }

    [Fact]
    public void TheImporterHonoursAPendingFolderMutationWithoutRewritingIt()
    {
        using var directory = new TestDirectory();
        var meeting = new PersistedMeeting { Id = "m", Title = "preserved", FolderId = "new-folder", Transcript = "raw" };
        new AtomicJsonFile().Save(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName), new
        {
            Folders = new[] { new PersistedMeetingFolder("new-folder", "new") }, Meetings = new[] { meeting }
        });
        var original = File.ReadAllBytes(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName));

        var result = LegacyJsonHistoryImporter.Import(directory.Path);

        Assert.True(result.Succeeded, result.Message);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.Equal("new-folder", Assert.Single(adapter.LoadMeetings()).FolderId);
        Assert.Equal("new-folder", Assert.Single(adapter.LoadMeetingFolders()).Id);
        Assert.Equal(original, File.ReadAllBytes(directory.File(JsonHistorySnapshotReader.LibraryMutationFileName)));
    }

    [Fact]
    public void AProfileWithOnlyEmptyLegacyFilesImportsToAnEmptyUsableLibrary()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictionaryFileName), "[]");
        var result = LegacyJsonHistoryImporter.Import(directory.Path);
        Assert.True(result.Succeeded, result.Message);
        Assert.True(LibraryDatabaseStartup.Prepare(directory.Path).IsReady);
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

    [Fact]
    public void DeletingARecordOnlyTouchesTheDatabase()
    {
        // Development builds kept JSON history beside the database. A deletion must not parse,
        // rewrite or remove any of it: an unreadable leftover used to make deletion fail.
        using var directory = new TestDirectory();
        new AppDataStore(directory.Path).SaveDictations([Dictation, Dictation with { Id = "keep", Text = "keep" }]);
        Assert.True(LegacyJsonHistoryImporter.Import(directory.Path).Succeeded);
        File.WriteAllText(directory.File("windows-dictations.json.corrupt-20260101-000000000-abc.json"), "{ broken");
        var before = Directory.EnumerateFiles(directory.Path, "windows-*").ToDictionary(path => path, File.ReadAllBytes);

        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        Assert.True(adapter.DeleteDictation("d"));

        Assert.Equal("keep", Assert.Single(adapter.LoadDictations()).Id);
        Assert.Empty(adapter.Search(new SearchQuery { Text = "lee" }));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
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
