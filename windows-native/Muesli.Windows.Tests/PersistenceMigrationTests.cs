using Microsoft.Data.Sqlite;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class PersistenceSchemaTests
{
    [Fact]
    public void AFreshDatabaseIsBroughtToTheCurrentSchemaAndRecordsHowItGotThere()
    {
        using var directory = new TestDirectory();

        using var database = MuesliDatabase.Open(directory.File("history.db"));

        Assert.Equal(PersistenceSchema.CurrentVersion, database.SchemaVersion);
        var history = database.Read(SchemaMigrator.ReadHistory);
        Assert.Equal(
            PersistenceSchema.Migrations.Select(migration => migration.Version),
            history.Select(entry => entry.Version));
    }

    [Fact]
    public void ReopeningAnUpToDateDatabaseAppliesNothing()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.db");
        DateTimeOffset applied;

        using (var database = MuesliDatabase.Open(path))
        {
            applied = database.Read(SchemaMigrator.ReadHistory)[0].AppliedAtUtc;
        }

        using (var database = MuesliDatabase.Open(path))
        {
            Assert.Equal(applied, database.Read(SchemaMigrator.ReadHistory)[0].AppliedAtUtc);
        }
    }

    [Fact]
    public void ADatabaseWrittenByANewerBuildIsRefusedRatherThanDowngraded()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.db");
        using (var store = MuesliPersistenceStore.Open(path))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "written by a newer build" });
            store.Database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {PersistenceSchema.CurrentVersion + 5};";
                command.ExecuteNonQuery();
            });
        }

        var exception = Assert.Throws<PersistenceSchemaException>(() => MuesliDatabase.Open(path));

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationsRunInOrderAndOnlyOnce()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var applied = new List<int>();
        var migrations = new List<SchemaMigration>
        {
            new(2, "second", _ => applied.Add(2)),
            new(1, "first", migrationConnection => PersistenceSchema.Execute(
                migrationConnection,
                "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY NOT NULL, description TEXT NOT NULL, applied_at_utc INTEGER NOT NULL) STRICT;")),
            new(3, "third", _ => applied.Add(3))
        };

        Assert.Equal(3, SchemaMigrator.Migrate(connection, migrations));
        Assert.Equal([2, 3], applied);

        Assert.Equal(3, SchemaMigrator.Migrate(connection, migrations));
        Assert.Equal([2, 3], applied);
        Assert.Equal([1, 2, 3], SchemaMigrator.ReadHistory(connection).Select(entry => entry.Version));
    }

    [Fact]
    public void AFailingMigrationLeavesTheSchemaVersionWhereItWas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var migrations = new List<SchemaMigration>
        {
            new(1, "first", migrationConnection => PersistenceSchema.Execute(
                migrationConnection,
                "CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY NOT NULL, description TEXT NOT NULL, applied_at_utc INTEGER NOT NULL) STRICT;" +
                "CREATE TABLE kept (id TEXT PRIMARY KEY NOT NULL) STRICT;")),
            new(2, "explodes", _ => throw new InvalidOperationException("simulated"))
        };

        Assert.Throws<InvalidOperationException>(() => SchemaMigrator.Migrate(connection, migrations));

        Assert.Equal(1, SchemaMigrator.ReadVersion(connection));
        Assert.Equal([1], SchemaMigrator.ReadHistory(connection).Select(entry => entry.Version));
    }

    [Fact]
    public void DuplicateMigrationVersionsAreRejected()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        Assert.Throws<PersistenceSchemaException>(() => SchemaMigrator.Migrate(
            connection,
            [new SchemaMigration(1, "a", _ => { }), new SchemaMigration(1, "b", _ => { })]));
    }

    [Fact]
    public void DefaultPathsAreCalculatedWithoutTouchingTheUserProfile()
    {
        var dataDirectory = PersistencePaths.DefaultDataDirectory;
        var existed = Directory.Exists(dataDirectory);

        var databasePath = PersistencePaths.DefaultDatabasePath;

        Assert.Equal(existed, Directory.Exists(dataDirectory));
        Assert.Equal(Path.Combine(dataDirectory, PersistencePaths.DatabaseFileName), databasePath);
        Assert.False(File.Exists(databasePath) && !existed);
    }
}

public sealed class JsonToSqliteMigrationTests
{
    private static readonly DateTime Created =
        new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc).AddTicks(1234567);

    [Fact]
    public void TheWholeHistoryArrivesIntact()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(new MigrationCounts(2, 2, 1, 1), result.Counts);
        Assert.True(result.CreatedDatabase);

        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        var detail = store.Meetings.FindDetail("m1")!;

        Assert.Equal("Northwind quarterly review", detail.Meeting.Title);
        Assert.True(detail.Meeting.TitleIsManual);
        Assert.Equal("Ask Dana about the storage overage.", detail.Note(MeetingNoteKind.Manual));
        Assert.Equal("## Summary\nRenewal on track.", detail.Note(MeetingNoteKind.Generated));
        Assert.Equal("Speaker 1: welcome to the renewal call.", detail.Transcript(MeetingTranscriptKind.Raw));
        Assert.Equal("Dana Whitfield", Assert.Single(detail.SpeakerAliases).Alias);
        Assert.Equal("folder-1", detail.Meeting.FolderId);
        Assert.Equal("Client review", detail.Meeting.TemplateName);
        Assert.Equal(["microphone dropped for 3s"], detail.Meeting.HealthWarnings);
        Assert.Equal(Created.AddHours(-2).Ticks, detail.Meeting.CreatedAtUtc.UtcTicks);

        Assert.Equal(@"C:\captures\m1-microphone.wav", detail.Meeting.Audio.MicrophoneAudioPath);
        Assert.Equal(@"C:\captures\m1-system.wav", detail.Meeting.Audio.SystemAudioPath);
        Assert.Equal("process-loopback", detail.Meeting.Audio.SystemCaptureMode);
        Assert.Equal("whisper-large", detail.Meeting.Audio.FinalTranscriptOwnerModelId);

        var dictation = store.Dictations.Find("d1")!;
        Assert.Equal("Send the pricing deck to procurement.", dictation.Text);
        Assert.Equal(Created.Ticks, dictation.CreatedAtUtc.UtcTicks);
        Assert.Equal(4200, dictation.DurationMs);
        Assert.Equal("parakeet", dictation.ModelProfile);

        Assert.Equal("Clients", Assert.Single(store.Folders.List()).Name);
        var template = Assert.Single(store.Templates.List());
        Assert.Equal("Client review", template.Name);
        Assert.Equal("Summarise renewal risk", template.Prompt);

        Assert.Single(store.Search.Search(new SearchQuery { Text = "procurement" }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "renewal", Kinds = SearchRecordKinds.Meeting }));
    }

    [Fact]
    public void NestedFoldersPreserveParentsAndMalformedParentsAreSanitized()
    {
        using var directory = new TestDirectory();
        var json = new AppDataStore(directory.Path);
        json.SaveMeetingFolders(
        [
            new PersistedMeetingFolder("child", "Child", "root"),
            new PersistedMeetingFolder("root", "Root"),
            new PersistedMeetingFolder("missing", "Missing", "gone"),
            new PersistedMeetingFolder("cycle-a", "Cycle A", "cycle-b"),
            new PersistedMeetingFolder("cycle-b", "Cycle B", "cycle-a")
        ]);

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.NotEmpty(result.Warnings);
        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        Assert.Equal("root", store.Folders.Find("child")!.ParentId);
        Assert.Null(store.Folders.Find("missing")!.ParentId);
        Assert.Null(store.Folders.Find("cycle-a")!.ParentId);
        Assert.Null(store.Folders.Find("cycle-b")!.ParentId);

        var imported = store.Folders.List().Select(folder => folder.Id).ToList();
        Assert.True(imported.IndexOf("root") < imported.IndexOf("child"));
    }

    [Fact]
    public void LegacyMeetingsKeepTheirAudioOwnership()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);

        new JsonToSqliteMigrationService(directory.Path).Migrate();

        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        var legacy = store.Meetings.Find("m-legacy")!;

        Assert.Equal(@"C:\captures\legacy-microphone.wav", legacy.Audio.MicrophoneAudioPath);
        Assert.Equal("legacy-unknown", legacy.Audio.SystemCaptureMode);
        Assert.Equal(MeetingSessionState.Completed, legacy.SessionState);
        Assert.Equal(0, legacy.SourceSchemaVersion);
    }

    [Fact]
    public void TheSourceFilesAreNotTouched()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var before = Fingerprint(directory.Path);

        new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(before, Fingerprint(directory.Path));
    }

    [Fact]
    public void SettingsAndSessionJournalsAreLeftAsAtomicJson()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var settingsPath = Path.Combine(directory.Path, "windows-settings.json");
        var sessionDirectory = Path.Combine(directory.Path, "captures", "in-progress", "session-1");
        Directory.CreateDirectory(sessionDirectory);
        var sessionPath = Path.Combine(sessionDirectory, "session.json");
        File.WriteAllText(settingsPath, """{"schemaVersion":1,"data":{"theme":"dark"}}""");
        File.WriteAllText(sessionPath, """{"schemaVersion":3,"data":{"sessionId":"session-1"}}""");
        var settings = File.ReadAllBytes(settingsPath);
        var session = File.ReadAllBytes(sessionPath);

        new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(settings, File.ReadAllBytes(settingsPath));
        Assert.Equal(session, File.ReadAllBytes(sessionPath));

        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        var tables = store.Database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        });
        Assert.DoesNotContain(tables, name => name.Contains("setting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, name => name.Contains("session", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunningItTwiceOverTheSameHistoryChangesNothingTheSecondTime()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var service = new JsonToSqliteMigrationService(directory.Path);
        var first = service.Migrate();

        var second = service.Migrate();

        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, second.Outcome);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Null(second.BackupPath);

        using var store = MuesliPersistenceStore.Open(service.DatabasePath);
        Assert.Equal(2, store.Dictations.Count());
        Assert.Equal(2, store.Meetings.Count());
    }

    [Fact]
    public void AnInterruptedImportLeavesNoPartialDataAndIsRetried()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var service = new JsonToSqliteMigrationService(directory.Path);

        // Simulate a crash between starting a run and committing its import.
        using (var store = MuesliPersistenceStore.Open(service.DatabasePath))
        {
            store.Database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT INTO migration_runs (id, source_fingerprint, state, started_at_utc) " +
                    "VALUES ('crashed', 'partial-fingerprint', 'started', 0);";
                command.ExecuteNonQuery();
            });

            using var abandoned = store.BeginTransaction();
            store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "half-written" });
            store.Dictations.Upsert(new DictationRecord { Id = "partial", Text = "half-written" });
        }

        var result = service.Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.True(result.ResumedAfterInterruption);

        using var reopened = MuesliPersistenceStore.Open(service.DatabasePath);
        Assert.Null(reopened.Dictations.Find("partial"));
        Assert.Equal(2, reopened.Dictations.Count());
        Assert.Equal("Send the pricing deck to procurement.", reopened.Dictations.Find("d1")!.Text);
    }

    [Fact]
    public void DuplicateAndBlankIdsAreImportedOnceAndReported()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations(
        [
            new PersistedDictation("d1", Created, "first wins", 100, "parakeet"),
            new PersistedDictation("d1", Created.AddMinutes(1), "duplicate loses", 200, "parakeet"),
            new PersistedDictation("", Created.AddMinutes(2), "no id", 300, "parakeet")
        ]);

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(1, result.Counts.Dictations);
        Assert.Contains(result.Warnings, warning => warning.Contains("dictations", StringComparison.Ordinal));

        using var migrated = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        Assert.Equal("first wins", migrated.Dictations.Find("d1")!.Text);
    }

    [Fact]
    public void AMeetingPointingAtAMissingFolderIsKeptAndUnfiled()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings(
        [
            new PersistedMeeting
            {
                SchemaVersion = 5,
                Id = "m1",
                Title = "Orphan",
                CreatedAt = Created,
                FolderId = "folder-that-was-deleted"
            }
        ]);

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Contains(result.Warnings, warning => warning.Contains("folder", StringComparison.OrdinalIgnoreCase));

        using var migrated = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        Assert.Null(migrated.Meetings.Find("m1")!.FolderId);
    }

    [Fact]
    public void UnreadableSourceStopsTheImportAndLeavesTheDatabaseAlone()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var service = new JsonToSqliteMigrationService(directory.Path);
        service.Migrate();
        File.WriteAllText(Path.Combine(directory.Path, "windows-dictations.json"), "{ not json at all");
        File.Delete(Path.Combine(directory.Path, "windows-dictations.json.bak"));

        var result = service.Migrate();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("could not be read", result.Failure);

        using var store = MuesliPersistenceStore.Open(service.DatabasePath);
        Assert.Equal(2, store.Dictations.Count());
    }

    [Fact]
    public void AnUnreadableSourceWithAReadableBackupIsRecoveredAndReported()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations([new PersistedDictation("d1", Created, "backed up text", 100, "parakeet")]);
        store.SaveDictations([new PersistedDictation("d1", Created, "current text", 100, "parakeet")]);
        File.WriteAllText(Path.Combine(directory.Path, "windows-dictations.json"), "{ not json at all");

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Contains(result.Warnings, warning => warning.Contains("backup", StringComparison.OrdinalIgnoreCase));

        using var migrated = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        Assert.Equal("backed up text", migrated.Dictations.Find("d1")!.Text);
    }

    [Fact]
    public void RollbackRestoresThePreMigrationDatabase()
    {
        using var directory = new TestDirectory();
        var appData = new AppDataStore(directory.Path);
        appData.SaveDictations([new PersistedDictation("d1", Created, "original", 100, "parakeet")]);
        var service = new JsonToSqliteMigrationService(directory.Path);
        service.Migrate();

        appData.SaveDictations(
        [
            new PersistedDictation("d1", Created, "original", 100, "parakeet"),
            new PersistedDictation("d2", Created.AddMinutes(1), "added later", 100, "parakeet")
        ]);
        var second = service.Migrate();
        Assert.Equal(JsonMigrationOutcome.Migrated, second.Outcome);
        Assert.NotNull(second.BackupPath);

        service.Rollback(second);

        using var store = MuesliPersistenceStore.Open(service.DatabasePath);
        Assert.Equal(1, store.Dictations.Count());
        Assert.Null(store.Dictations.Find("d2"));
    }

    [Fact]
    public void RollingBackAMigrationThatCreatedTheDatabaseRemovesIt()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var service = new JsonToSqliteMigrationService(directory.Path);
        var result = service.Migrate();
        Assert.True(File.Exists(service.DatabasePath));

        service.Rollback(result);

        Assert.False(File.Exists(service.DatabasePath));
    }

    [Fact]
    public void WithoutARetainedBackupRollbackRefusesRatherThanGuessing()
    {
        using var directory = new TestDirectory();
        var appData = new AppDataStore(directory.Path);
        appData.SaveDictations([new PersistedDictation("d1", Created, "original", 100, "parakeet")]);
        var service = new JsonToSqliteMigrationService(directory.Path);
        service.Migrate();
        appData.SaveDictations([new PersistedDictation("d2", Created, "second", 100, "parakeet")]);

        var result = service.Migrate(new JsonMigrationOptions { RetainBackup = false });

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Null(result.BackupPath);
        Assert.Throws<PersistenceException>(() => service.Rollback(result));
    }

    [Fact]
    public void AnEmptyDataDirectoryIsNotAMigration()
    {
        using var directory = new TestDirectory();

        var result = new JsonToSqliteMigrationService(directory.Path).Migrate();

        Assert.Equal(JsonMigrationOutcome.NothingToMigrate, result.Outcome);
        Assert.Equal(0, result.Counts.Total);
    }

    [Fact]
    public void MigratingIntoADatabaseThatAlreadyHasHistoryKeepsBoth()
    {
        using var directory = new TestDirectory();
        WriteHistory(directory.Path);
        var service = new JsonToSqliteMigrationService(directory.Path);
        using (var store = MuesliPersistenceStore.Open(service.DatabasePath))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "native", Text = "written natively" });
        }

        var result = service.Migrate();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.NotNull(result.BackupPath);

        using var reopened = MuesliPersistenceStore.Open(service.DatabasePath);
        Assert.Equal(3, reopened.Dictations.Count());
        Assert.NotNull(reopened.Dictations.Find("native"));
    }

    [Theory]
    [InlineData("manual note")]
    [InlineData("title")]
    [InlineData("timestamp")]
    [InlineData("alias")]
    [InlineData("audio path")]
    [InlineData("transcript")]
    public void TheContentHashNoticesAFieldThatWentMissing(string change)
    {
        var original = PersistenceRepositoryTests.Meeting();
        var damaged = change switch
        {
            "manual note" => original with
            {
                Notes = original.Notes.Where(note => note.Kind != MeetingNoteKind.Manual).ToList()
            },
            "title" => original with { Meeting = original.Meeting with { Title = "Renamed" } },
            "timestamp" => original with
            {
                Meeting = original.Meeting with { CreatedAtUtc = original.Meeting.CreatedAtUtc.AddTicks(1) }
            },
            "alias" => original with { SpeakerAliases = [] },
            "audio path" => original with
            {
                Meeting = original.Meeting with
                {
                    Audio = original.Meeting.Audio with { MicrophoneAudioPath = null }
                }
            },
            _ => original with
            {
                Transcripts = original.Transcripts
                    .Select(transcript => transcript.Kind == MeetingTranscriptKind.Raw
                        ? transcript with { Content = transcript.Content[..5] }
                        : transcript)
                    .ToList()
            }
        };

        Assert.NotEqual(
            PersistenceDigest.OfMeetings([original]),
            PersistenceDigest.OfMeetings([damaged]));
    }

    [Fact]
    public void TheContentHashIgnoresTheOrderRecordsArriveIn()
    {
        var first = PersistenceRepositoryTests.Meeting();
        var second = first with { Meeting = first.Meeting with { Id = "m2" } };

        Assert.Equal(
            PersistenceDigest.OfMeetings([first, second]),
            PersistenceDigest.OfMeetings([second, first]));
    }

    private static void WriteHistory(string dataDirectory)
    {
        var store = new AppDataStore(dataDirectory);
        store.SaveMeetingFolders([new PersistedMeetingFolder("folder-1", "Clients")]);
        store.SaveMeetingTemplates(
        [
            new PersistedMeetingTemplate
            {
                Id = "tmpl-1",
                Name = "Client review",
                Prompt = "Summarise renewal risk",
                Icon = "doc"
            }
        ]);
        store.SaveDictations(
        [
            new PersistedDictation("d1", Created, "Send the pricing deck to procurement.", 4200, "parakeet"),
            new PersistedDictation("d2", Created.AddMinutes(5), "Second dictation.", 1000, "parakeet")
        ]);
        store.SaveMeetings(
        [
            new PersistedMeeting
            {
                SchemaVersion = 5,
                Id = "m1",
                Title = "Northwind quarterly review",
                TitleIsManual = true,
                CreatedAt = Created.AddHours(-2),
                DurationMs = 3_600_000,
                Transcript = "Speaker 1: welcome to the renewal call.",
                Summary = "## Summary\nRenewal on track.",
                ManualNotes = "Ask Dana about the storage overage.",
                SourcePath = @"C:\captures\m1-microphone.wav;C:\captures\m1-system.wav",
                MicrophoneAudioPath = @"C:\captures\m1-microphone.wav",
                SystemAudioPath = @"C:\captures\m1-system.wav",
                SystemCaptureMode = "process-loopback",
                ModelProfile = "whisper-large",
                FolderId = "folder-1",
                WordCount = 5400,
                TemplateName = "Client review",
                SpeakerAliases = new Dictionary<string, string> { ["Speaker 1"] = "Dana Whitfield" },
                HealthWarnings = ["microphone dropped for 3s"],
                FinalTranscriptOwnerModelId = "whisper-large"
            },
            new PersistedMeeting
            {
                SchemaVersion = 0,
                Id = "m-legacy",
                Title = "Old standup",
                CreatedAt = Created.AddDays(-30),
                Transcript = "Legacy transcript.",
                SourcePath = @"C:\captures\legacy-microphone.wav",
                ModelProfile = "parakeet"
            }
        ]);
    }

    private static List<string> Fingerprint(string directory) =>
        Directory.EnumerateFiles(directory, "windows-*.json*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:" +
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
            .ToList();
}
