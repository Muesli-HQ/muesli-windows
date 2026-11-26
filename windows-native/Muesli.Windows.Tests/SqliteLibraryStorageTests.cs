using Microsoft.Data.Sqlite;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Services.Persistence;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// SQLite is the only history store: what a launch does with each state a profile can be in.
/// </summary>
public sealed class SqliteLibraryStorageTests
{
    private static readonly DictionaryEntryRecord Rule = new()
    {
        Id = "rule-α", Phrase = "mues lee", Replacement = "Muesli", MatchingThreshold = 0.9123456789
    };
    // Local time, as the app creates and reads records; storage converts to UTC and back.
    private static readonly PersistedDictation Dictation = new("d", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime(),
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

        // The library is the database alone: no folders, markers or copies beside it.
        Assert.Empty(Directory.EnumerateDirectories(profile.DataDirectory));
        using var restarted = new WinUiLibraryContext(profile);
        Assert.Equal(Dictation, Assert.Single(restarted.History.LoadDictations()));
    }

    [Fact]
    public void TheLibraryRevisionMovesOnlyWhenSomethingIsWritten()
    {
        using var directory = new TestDirectory();
        using var context = new WinUiLibraryContext(MuesliProfilePaths.Isolated(directory.Path));

        var empty = context.Revision;
        context.ReadSnapshot();
        Assert.Equal(empty, context.Revision);

        context.History.AppendDictation(Dictation);
        var afterDictation = context.Revision;
        Assert.NotEqual(empty, afterDictation);

        // A page that reloads on this value must not reload for a read.
        context.ReadSnapshot();
        Assert.Equal(afterDictation, context.Revision);

        // A meeting's title arriving later changes no count, but it is a write.
        context.SaveMeeting(new PersistedMeeting { Id = "m", Title = "", CreatedAt = DateTime.Now });
        var afterMeeting = context.Revision;
        context.SaveMeeting(new PersistedMeeting { Id = "m", Title = "Planning", CreatedAt = DateTime.Now });
        Assert.NotEqual(afterDictation, afterMeeting);
        Assert.NotEqual(afterMeeting, context.Revision);
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

    // ---- Damaged or partial databases ---------------------------------------------------------

    [Theory]
    [InlineData("muesli.db-wal")]
    [InlineData("muesli.db-shm")]
    public void AnOrphanedWriteAheadLogIsNeverOverwrittenByANewDatabase(string orphan)
    {
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        File.WriteAllText(directory.File(orphan), "recent history that only the log holds");

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseMissing, result.Outcome);
        Assert.Contains(orphan, result.Message);
        Assert.False(File.Exists(path));
        Assert.Equal("recent history that only the log holds", File.ReadAllText(directory.File(orphan)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken database")]
    public void AnEmptyOrDamagedDatabaseFileIsReportedAndLeftAsItWas(string content)
    {
        // SQLite opens a zero-byte file happily, so without the check it would be given a schema
        // and shown as an empty, healthy library.
        using var directory = new TestDirectory();
        var path = PersistencePaths.DatabasePathFor(directory.Path);
        File.WriteAllText(path, content);

        var result = LibraryDatabaseStartup.Prepare(directory.Path);

        Assert.Equal(LibraryStartupOutcome.DatabaseUnreadable, result.Outcome);
        Assert.Contains("move muesli.db out of that folder", result.Message);
        Assert.Equal(content, File.ReadAllText(path));

        // The app refuses the same way, and releases the profile so a later launch is not locked out.
        var profile = MuesliProfilePaths.Isolated(directory.File("profile"));
        Directory.CreateDirectory(profile.DataDirectory);
        var profileDatabase = PersistencePaths.DatabasePathFor(profile.DataDirectory);
        File.WriteAllText(profileDatabase, content);
        var thrown = Assert.Throws<LibraryUnavailableException>(() => new WinUiLibraryContext(profile));
        Assert.Equal(LibraryStartupOutcome.DatabaseUnreadable, thrown.Result.Outcome);
        File.Delete(profileDatabase);
        using var opened = new WinUiLibraryContext(profile);
    }

    [Fact]
    public void HistoryIsHandedToTheAppInLocalTimeAndStoredAsTheSameInstant()
    {
        // Pages group by calendar day and compare with local cut-offs. A UTC value would put an
        // evening or early-morning recording on the wrong day for anyone not in UTC.
        using var directory = new TestDirectory();
        using var library = TestLibrary.Open(directory.Path);
        var localEvening = new DateTime(2026, 3, 9, 23, 30, 0, DateTimeKind.Local);
        library.AppendDictation(new PersistedDictation("d", localEvening, "late note", 1000, "model"));
        library.SaveMeetings([new PersistedMeeting { Id = "m", Title = "Late call", CreatedAt = localEvening }]);

        var dictation = Assert.Single(library.LoadDictations());
        var meeting = Assert.Single(library.LoadMeetings());

        Assert.Equal(DateTimeKind.Local, dictation.Timestamp.Kind);
        Assert.Equal(localEvening, dictation.Timestamp);
        Assert.Equal(new DateTime(2026, 3, 9), dictation.Timestamp.Date);
        Assert.Equal(DateTimeKind.Local, meeting.CreatedAt.Kind);
        Assert.Equal(localEvening, meeting.CreatedAt);
        // Storage itself stays an absolute UTC instant.
        Assert.Equal(new DateTimeOffset(localEvening).UtcTicks, library.Store.Dictations.Find("d")!.CreatedAtUtc.UtcTicks);
    }

    // ---- Meetings ---------------------------------------------------------------------------

    [Fact]
    public void TheFirstTranscriptOfADraftMeetingIsKeptAsTheRecognizersOutput()
    {
        // Typing notes while recording saves the meeting before any speech is recognized. As on
        // macOS, finalization then fills in that row's transcript; a later cleanup or user edit
        // must not be the only copy.
        using var directory = new TestDirectory();
        using var library = TestLibrary.Open(directory.Path);
        var draft = new PersistedMeeting { Id = "m", Title = "Planning", ManualNotes = "typed while recording", CreatedAt = DateTime.UtcNow };
        library.SaveMeetings([draft]);

        library.SaveMeetings([draft with { Transcript = "what the recognizer heard" }]);

        var stored = library.Store.Meetings.FindDetail("m")!;
        Assert.Equal("what the recognizer heard", stored.Transcript(MeetingTranscriptKind.Raw));
        Assert.Null(stored.Transcript(MeetingTranscriptKind.Edited));

        library.SaveMeetings([draft with { Transcript = "What the recognizer heard, cleaned up." }]);

        stored = library.Store.Meetings.FindDetail("m")!;
        Assert.Equal("what the recognizer heard", stored.Transcript(MeetingTranscriptKind.Raw));
        Assert.Equal("What the recognizer heard, cleaned up.", stored.Transcript(MeetingTranscriptKind.Edited));
        var shown = library.FindMeeting("m")!;
        Assert.Equal("What the recognizer heard, cleaned up.", shown.Transcript);
        Assert.Equal("typed while recording", shown.ManualNotes);
        Assert.Single(library.Search(new SearchQuery { Text = "cleaned" }));
    }

    [Fact]
    public void ANewAutomationRunReplacesTheStoredResultAndOtherSavesKeepIt()
    {
        using var directory = new TestDirectory();
        using var library = TestLibrary.Open(directory.Path);
        var first = AutomationResult(PostMeetingAutomationStatus.Failed, directory.File("first.md"));
        var second = AutomationResult(PostMeetingAutomationStatus.Succeeded, directory.File("second.md"));
        var meeting = new PersistedMeeting { Id = "m", Title = "Planning", Transcript = "spoken", CreatedAt = DateTime.UtcNow };
        library.SaveMeetings([meeting with { AutomationResult = first }]);

        // A save that carries no result, such as a notes edit, keeps the stored one.
        library.SaveMeetings([meeting with { ManualNotes = "added later" }]);
        Assert.Equal(first, library.FindMeeting("m")!.AutomationResult);

        // The meeting was resumed and the hook ran again: the details must show this run.
        library.SaveMeetings([meeting with { AutomationResult = second }]);
        Assert.Equal(second, library.FindMeeting("m")!.AutomationResult);
    }

    private static PostMeetingAutomationResult AutomationResult(PostMeetingAutomationStatus status, string exportPath) =>
        new(Guid.NewGuid(), status, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, 1,
            status == PostMeetingAutomationStatus.Succeeded ? 0 : 17, "out", "err", false, false,
            status == PostMeetingAutomationStatus.Succeeded ? null : "Hook exited with code 17.",
            new PostMeetingExportDiagnostic(true, true, exportPath,
                AutomationDestinationOwnership.UserSelectedDestination, null, 1));

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
