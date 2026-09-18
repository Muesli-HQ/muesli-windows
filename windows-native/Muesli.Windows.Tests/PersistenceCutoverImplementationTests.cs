using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

/// <summary>
/// L27 cutover implementation tests. These call <see cref="PersistenceCutover"/> and
/// <see cref="SqliteLibraryHistoryAdapter"/> directly. They do not flip
/// <see cref="PersistenceCutoverGate"/> or touch FeatureRuntime.
/// </summary>
public sealed class PersistenceCutoverImplementationTests
{
    private static readonly DateTime Created =
        new DateTime(2026, 3, 14, 9, 30, 0, DateTimeKind.Utc).AddTicks(1234567);

    private const string TitleToken = "title-secret";
    private const string TranscriptToken = "transcript-secret";
    private const string AudioPathToken = @"C:\captures\secret-microphone.wav";

    [Fact]
    public void FeatureFlagDefaultsOffAndIsNotProductionWiring()
    {
        Assert.Equal("L27SqliteHistoryCutover", PersistenceCutoverGate.FeatureFlagName);
        Assert.False(PersistenceCutoverGate.DefaultEnabled);
        var previous = Environment.GetEnvironmentVariable(PersistenceCutoverGate.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(PersistenceCutoverGate.EnvironmentVariableName, null);
            Assert.False(PersistenceCutoverGate.IsEnabled);
            Environment.SetEnvironmentVariable(PersistenceCutoverGate.EnvironmentVariableName, "1");
            Assert.True(PersistenceCutoverGate.IsEnabled);
            Environment.SetEnvironmentVariable(PersistenceCutoverGate.EnvironmentVariableName, "true");
            Assert.False(PersistenceCutoverGate.IsEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PersistenceCutoverGate.EnvironmentVariableName, previous);
        }
    }

    [Fact]
    public void CaptureUsesHiddenProfileStagingAndScavengesStalePlaintextAtStartup()
    {
        using var directory = new TestDirectory();
        var stagingRoot = Path.Combine(directory.Path, PersistenceCutover.JsonCaptureStagingDirectoryName);
        var staleDirectory = Path.Combine(
            stagingRoot,
            PersistenceCutover.JsonCaptureStagingDirectoryPrefix + "stale");
        Directory.CreateDirectory(staleDirectory);
        File.WriteAllText(Path.Combine(staleDirectory, ".capture.lock"), TranscriptToken);
        File.WriteAllText(Path.Combine(staleDirectory, "stale.json"), TranscriptToken);
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName), "[]");

        string? createdCapture = null;
        var createdCaptureWasHidden = false;
        var result = new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions
            {
                FailAfterJsonSnapshot = true,
                CaptureDirectoryCreated = path =>
                {
                    createdCapture = path;
                    createdCaptureWasHidden = File.GetAttributes(path).HasFlag(FileAttributes.Hidden);
                }
            }).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.False(Directory.Exists(staleDirectory));
        Assert.NotNull(createdCapture);
        Assert.StartsWith(stagingRoot, createdCapture!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.Combine(Path.GetFullPath(Path.GetTempPath()), "muesli-json-cutover-"),
            createdCapture!,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(createdCaptureWasHidden);
        Assert.StartsWith(directory.Path, result.JsonSnapshotDirectory!, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(staleDirectory, "stale.json")));
    }

    [Fact]
    public void LiveCaptureMarkerPreventsScavengingAndLeavesLockedStageUntouched()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName), "[]");
        var captureDirectory = Path.Combine(
            directory.Path,
            PersistenceCutover.JsonCaptureStagingDirectoryName,
            PersistenceCutover.JsonCaptureStagingDirectoryPrefix + "live");
        Directory.CreateDirectory(captureDirectory);
        var markerPath = Path.Combine(captureDirectory, ".capture.lock");
        using var marker = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            1,
            FileOptions.WriteThrough);
        File.WriteAllText(Path.Combine(captureDirectory, "sentinel.json"), TranscriptToken);

        var result = new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions { FailAfterJsonSnapshot = true }).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.True(Directory.Exists(captureDirectory));
        Assert.True(File.Exists(Path.Combine(captureDirectory, "sentinel.json")));
    }

    [Fact]
    public async Task ProfileLockCoversImportAndAuthorityCommitAcrossTwoInstances()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        using var authorityCommitStarted = new ManualResetEventSlim();
        using var releaseAuthorityCommit = new ManualResetEventSlim();

        var firstTask = Task.Run(() => new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions
            {
                BeforeAuthorityCommit = () =>
                {
                    authorityCommitStarted.Set();
                    releaseAuthorityCommit.Wait();
                }
            }).EnsureMigrated());

        try
        {
            Assert.True(
                authorityCommitStarted.Wait(TimeSpan.FromSeconds(10)),
                "The first cutover did not reach the authority transaction in time.");

            var second = new PersistenceCutover(directory.Path).EnsureMigrated();
            Assert.Equal(JsonMigrationOutcome.Failed, second.Outcome);
            Assert.Contains("capture area", second.Failure, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            releaseAuthorityCommit.Set();
        }

        var first = await firstTask;
        Assert.Equal(JsonMigrationOutcome.Migrated, first.Outcome);
        Assert.Equal(
            JsonMigrationOutcome.AlreadyCurrent,
            new PersistenceCutover(directory.Path).EnsureMigrated().Outcome);
    }

    [Fact]
    public void ReparseCaptureDirectoryFailsClosedWithoutDeletingJunctionTarget()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        File.WriteAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName), "[]");
        File.WriteAllText(outside.File("sentinel.json"), TranscriptToken);
        var root = Path.Combine(directory.Path, PersistenceCutover.JsonCaptureStagingDirectoryName);
        var junction = Path.Combine(
            root,
            PersistenceCutover.JsonCaptureStagingDirectoryPrefix + "junction");
        Directory.CreateDirectory(root);

        CreateJunction(junction, outside.Path);

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.True(File.Exists(outside.File("sentinel.json")));
        Assert.Equal(TranscriptToken, File.ReadAllText(outside.File("sentinel.json")));
    }

    [Fact]
    public void ReparseSourceInputFailsClosedWithoutReadingOutsideProfile()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        File.WriteAllText(outside.File("source.json"), "[]");
        var source = directory.File(JsonHistorySnapshotReader.DictationsFileName);

        // A directory junction is sufficient here: the source path itself is a reparse point,
        // so capture must reject it before attempting to interpret the target as JSON.
        CreateJunction(source, outside.Path);

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.Equal("[]", File.ReadAllText(outside.File("source.json")));
    }

    [Fact]
    public void ReparseDatabasePathFailsClosedWithoutTouchingJunctionTarget()
    {
        using var directory = new TestDirectory();
        using var outside = new TestDirectory();
        File.WriteAllText(outside.File("sentinel.json"), TranscriptToken);
        var databasePath = PersistencePaths.DatabasePathFor(directory.Path);

        CreateJunction(databasePath, outside.Path);

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Equal(TranscriptToken, File.ReadAllText(outside.File("sentinel.json")));
    }

    [Fact]
    public void MutationDuringMultiFileCaptureRetriesAndImportsOneCoherentGeneration()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var mutationCount = 0;
        var changed = false;

        var result = new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions
            {
                AfterCaptureFile = name =>
                {
                    if (!name.Equals(JsonHistorySnapshotReader.DictationsFileName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    mutationCount++;
                    if (!changed)
                    {
                        changed = true;
                        WriteCaptureGeneration(directory.Path, "new-generation");
                    }
                }
            }).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.True(mutationCount >= 2);
        using var store = MuesliPersistenceStore.Open(result.DatabasePath);
        Assert.Equal(
            "id-d-new-generation",
            Assert.Single(store.Dictations.List(UnboundedHistoryQuery.Dictations)).Id);
        Assert.Equal(
            "id-m-new-generation",
            Assert.Single(store.Meetings.List(new MeetingQuery())).Id);
    }

    [Fact]
    public void RepeatedMutationDuringCaptureFailsClosedWithoutCreatingDatabase()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var mutationCount = 0;

        var result = new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions
            {
                CaptureAttemptCount = 2,
                AfterCaptureFile = name =>
                {
                    if (name.Equals(JsonHistorySnapshotReader.DictationsFileName, StringComparison.Ordinal))
                    {
                        mutationCount++;
                        WriteCaptureGeneration(directory.Path, $"generation-{mutationCount}");
                    }
                }
            }).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.Equal(2, mutationCount);
        Assert.Contains("captured", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "id-d-generation-2",
            File.ReadAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClonedProfileMigratesWithEqualCountsAndDigestsAndLeavesJsonInPlace()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var reports = new List<string>();
        var snapshot = JsonHistorySnapshotReader.Read(directory.Path);
        var plan = MigrationPlan.Build(snapshot, []);
        var jsonBefore = FingerprintJson(directory.Path);

        var result = new PersistenceCutover(directory.Path, report: reports.Add).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(plan.Counts, result.Counts);
        Assert.Equal(plan.Fingerprint, result.SourceFingerprint);
        Assert.True(Directory.Exists(result.JsonSnapshotDirectory));
        Assert.Equal(jsonBefore, FingerprintJson(directory.Path));
        Assert.True(File.Exists(Path.Combine(directory.Path, "windows-dictations.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, "windows-meetings.json")));
        Assert.True(File.Exists(Path.Combine(directory.Path, PersistenceCutover.DictionaryFileName)));

        using var store = MuesliPersistenceStore.Open(result.DatabasePath);
        Assert.Equal(plan.Counts.Dictations, store.Dictations.Count());
        Assert.Equal(plan.Counts.Meetings, store.Meetings.Count());
        Assert.Equal(plan.Counts.Folders, store.Folders.Count());
        Assert.Equal(plan.Counts.Templates, store.Templates.Count());
        Assert.Equal(
            PersistenceDigest.OfDictations(plan.Dictations),
            PersistenceDigest.OfDictations(store.Dictations.List(UnboundedHistoryQuery.Dictations)));
        Assert.Equal(
            PersistenceDigest.OfFolders(plan.Folders),
            PersistenceDigest.OfFolders(store.Folders.List()));
        Assert.Equal(
            PersistenceDigest.OfTemplates(plan.Templates),
            PersistenceDigest.OfTemplates(store.Templates.List()));
        var imported = plan.Meetings
            .Select(meeting => store.Meetings.FindDetail(meeting.Meeting.Id)!)
            .ToList();
        Assert.Equal(PersistenceDigest.OfMeetings(plan.Meetings), PersistenceDigest.OfMeetings(imported));
        AssertNoSensitivePayload(reports, result.Failure);
        using (var database = MuesliDatabase.Open(result.DatabasePath))
        {
            var state = PersistenceCutoverState.Read(database);
            Assert.Equal(PersistenceAuthority.Sqlite, state.Authority);
            Assert.Equal(result.SourceFingerprint, state.SourceFingerprint);
            Assert.NotNull(state.ActivatedAtUtc);
        }
        Assert.Equal(
            """{"runId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","extra":"kept"}""",
            store.Meetings.Find("id-m-keep")!.AutomationResultJson);
    }

    [Fact]
    public void SecondEnsureMigratedIsAlreadyCurrentAndDoesNotRewriteSqlite()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var cutover = new PersistenceCutover(directory.Path);
        var first = cutover.EnsureMigrated();
        var jsonBefore = FingerprintJson(directory.Path);

        var second = cutover.EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, second.Outcome);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal(jsonBefore, FingerprintJson(directory.Path));
        Assert.NotNull(second.JsonSnapshotDirectory);
        Assert.True(Directory.Exists(second.JsonSnapshotDirectory));
        using var store = MuesliPersistenceStore.Open(first.DatabasePath);
        Assert.Equal(first.Counts.Dictations, store.Dictations.Count());
        Assert.Equal(first.Counts.Meetings, store.Meetings.Count());
    }

    [Fact]
    public void MutationAfterCapturedJsonSnapshotDoesNotChangeImportFingerprintOrRetainedSnapshot()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var capturedSnapshot = JsonHistorySnapshotReader.Read(directory.Path);
        var capturedPlan = MigrationPlan.Build(capturedSnapshot, []);
        var capturedDictations = File.ReadAllBytes(directory.File(JsonHistorySnapshotReader.DictationsFileName));
        var reports = new List<string>();
        var mutated = false;

        var result = new PersistenceCutover(
            directory.Path,
            report: message =>
            {
                reports.Add(message);
                if (!mutated && message.StartsWith("Kept a JSON history snapshot", StringComparison.Ordinal))
                {
                    mutated = true;
                    new AppDataStore(directory.Path).SaveDictations([
                        new PersistedDictation(
                            "id-d-after-capture",
                            Created.AddDays(1),
                            "body-after-capture",
                            999,
                            "model-after-capture")
                    ]);
                }
            }).EnsureMigrated();

        Assert.True(mutated);
        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(capturedPlan.Fingerprint, result.SourceFingerprint);
        Assert.Equal(capturedPlan.Counts, result.Counts);
        Assert.DoesNotContain(reports, report => report.Contains("id-d-after-capture", StringComparison.Ordinal));

        using (var store = MuesliPersistenceStore.Open(result.DatabasePath))
        {
            Assert.Null(store.Dictations.Find("id-d-after-capture"));
            Assert.Equal(
                PersistenceDigest.OfDictations(capturedPlan.Dictations),
                PersistenceDigest.OfDictations(store.Dictations.List(UnboundedHistoryQuery.Dictations)));
        }

        Assert.NotNull(result.JsonSnapshotDirectory);
        Assert.Equal(
            capturedDictations,
            File.ReadAllBytes(Path.Combine(result.JsonSnapshotDirectory!, JsonHistorySnapshotReader.DictationsFileName)));
        Assert.Contains(
            "id-d-after-capture",
            File.ReadAllText(directory.File(JsonHistorySnapshotReader.DictationsFileName)),
            StringComparison.Ordinal);

        using var database = MuesliDatabase.Open(result.DatabasePath);
        Assert.Equal(result.SourceFingerprint, PersistenceCutoverState.Read(database).SourceFingerprint);
    }

    [Fact]
    public void ForcedFailureAfterSnapshotRetainsOriginalJson()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var before = FingerprintJson(directory.Path);

        var result = new PersistenceCutover(
            directory.Path,
            options: new PersistenceCutoverOptions { FailAfterJsonSnapshot = true }).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(before, FingerprintJson(directory.Path));
        Assert.True(Directory.Exists(result.JsonSnapshotDirectory));
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        new PersistenceCutover(directory.Path).Rollback(result);
        Assert.Equal(before, FingerprintJson(directory.Path));
    }

    [Fact]
    public void MigrationFailureLeavesExistingDatabaseRowsIntact()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var databasePath = PersistencePaths.DatabasePathFor(directory.Path);
        using (var store = MuesliPersistenceStore.Open(databasePath))
        {
            store.Dictations.Upsert(new DictationRecord
            {
                Id = "id-existing",
                Text = "existing-token",
                CreatedAtUtc = new DateTimeOffset(Created, TimeSpan.Zero),
                UpdatedAtUtc = new DateTimeOffset(Created, TimeSpan.Zero)
            });
        }

        // A pre-existing backup path fails after the migration has opened the database but before
        // import. The cutover must fail closed and retain the pre-existing row.
        var invalidBackupPath = Path.Combine(directory.Path, "already-present-backup.db");
        File.WriteAllText(invalidBackupPath, "reserved");
        var result = new JsonToSqliteMigrationService(directory.Path, databasePath)
            .Migrate(new JsonMigrationOptions { BackupPath = invalidBackupPath });

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        using var reopened = MuesliPersistenceStore.Open(databasePath);
        Assert.NotNull(reopened.Dictations.Find("id-existing"));
        Assert.Equal(1, reopened.Dictations.Count());
    }

    [Fact]
    public void EmptyProfileCreatesUsableAuthoritativeDatabase()
    {
        using var directory = new TestDirectory();

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.True(result.CreatedDatabase);
        Assert.True(File.Exists(result.DatabasePath));
        using var database = MuesliDatabase.Open(result.DatabasePath);
        var state = PersistenceCutoverState.Read(database);
        Assert.Equal(PersistenceAuthority.Sqlite, state.Authority);
        Assert.Equal(0, result.Counts.Total);
    }

    [Fact]
    public void FirstAdapterWriteIsDurableAndRefusesJsonRollback()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var cutover = new PersistenceCutover(directory.Path);
        var result = cutover.EnsureMigrated();
        using (var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path))
        {
            adapter.SaveDictations(adapter.LoadDictations());
        }

        using (var database = MuesliDatabase.Open(result.DatabasePath))
        {
            var state = PersistenceCutoverState.Read(database);
            Assert.NotNull(state.FirstPostCutoverWriteAtUtc);
            Assert.Equal("dictations", state.FirstPostCutoverWrite);
        }

        var exception = Assert.Throws<PersistenceException>(() => cutover.Rollback(result));
        Assert.Contains("explicit SQLite backup", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(result.DatabasePath));
    }

    [Fact]
    public void CorruptJsonFailsClosedWithoutQuarantineOrSuccess()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("windows-dictations.json"), "{not-json");
        var before = File.ReadAllBytes(directory.File("windows-dictations.json"));

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("could not be read", result.Failure, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(directory.File("windows-dictations.json")));
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.DoesNotContain("not-json", string.Join('\n', result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardJsonEnvelopeFailsClosedAndIsNotMigratedSuccess()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.File("windows-meetings.json"),
            """{"schemaVersion":2,"data":[{"id":"id-future","title":"title-future"}]}""");

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("unsupported schema", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(directory.File("windows-meetings.json")));
        Assert.DoesNotContain("title-future", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("id-future", result.Failure, StringComparison.Ordinal);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
    }

    [Fact]
    public void ForwardMeetingSchemaFailsClosedAndKeepsJson()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion + 1,
                Id = "id-future-record",
                Title = TitleToken,
                CreatedAt = Created,
                Transcript = TranscriptToken
            }
        ]);

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("unsupported schema", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id-future-record", result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(TitleToken, result.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptToken, result.Failure, StringComparison.Ordinal);
        Assert.True(File.Exists(directory.File("windows-meetings.json")));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void NewerSqliteUserVersionFailsClosedAndLeavesJson()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var jsonBefore = FingerprintJson(directory.Path);
        using (var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path)))
        {
            store.Dictations.Upsert(new DictationRecord { Id = "native", Text = "written by a newer build" });
            store.Database.Write(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA user_version = {PersistenceSchema.CurrentVersion + 5};";
                command.ExecuteNonQuery();
            });
        }

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("newer", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(jsonBefore, FingerprintJson(directory.Path));
        Assert.DoesNotContain(TitleToken, result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedJsonAfterCompletedImportIsIgnoredAfterSqliteAuthority()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var cutover = new PersistenceCutover(directory.Path);
        cutover.EnsureMigrated();
        new AppDataStore(directory.Path).SaveDictations([
            new PersistedDictation("id-d-extra", Created, "body-extra", 100, "model-token")
        ]);

        var result = cutover.EnsureMigrated();

        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, result.Outcome);
        Assert.Null(result.Failure);
        using var store = MuesliPersistenceStore.Open(PersistencePaths.DatabasePathFor(directory.Path));
        Assert.Null(store.Dictations.Find("id-d-extra"));
        Assert.Equal(2, store.Dictations.Count());
    }

    [Fact]
    public void AdapterLoadDoesNotUseDefaultLimitOf100()
    {
        using var directory = new TestDirectory();
        var json = new AppDataStore(directory.Path);
        json.SaveDictations(Enumerable.Range(0, 101)
            .Select(index => new PersistedDictation(
                $"id-d-{index:D3}",
                Created.AddMinutes(index),
                $"body-{index:D3}",
                100,
                "model-token"))
            .ToList());

        var migrated = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.Equal(JsonMigrationOutcome.Migrated, migrated.Outcome);
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);

        Assert.Equal(101, adapter.LoadDictations().Count);
        Assert.Equal(100, adapter.Store.Dictations.List(new DictationQuery()).Count);
        Assert.Equal(101, adapter.Store.Dictations.List(UnboundedHistoryQuery.Dictations).Count);
    }

    [Fact]
    public void FolderSaveRoundTripsParentIdAndDoesNotFlattenNestedFolders()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        var anchor = new DateTimeOffset(Created, TimeSpan.Zero);
        adapter.Store.Folders.Upsert(new FolderRecord
        {
            Id = "id-child",
            Name = "child-token",
            ParentId = "id-f-keep",
            SortOrder = 1,
            CreatedAtUtc = anchor,
            UpdatedAtUtc = anchor
        });

        adapter.SaveMeetingFolders(
        [
            new PersistedMeetingFolder("id-f-keep", "folder-token"),
            new PersistedMeetingFolder("id-child", "child-renamed", "id-f-keep")
        ]);

        Assert.Equal("id-f-keep", adapter.Store.Folders.Find("id-child")!.ParentId);
        Assert.Equal("child-renamed", adapter.Store.Folders.Find("id-child")!.Name);
        Assert.Null(adapter.Store.Folders.Find("id-f-keep")!.ParentId);
        adapter.SaveMeetingFolders([new PersistedMeetingFolder("id-f-keep", "folder-token")]);
        Assert.Equal("id-f-keep", adapter.Store.Folders.Find("id-child")!.ParentId);
    }

    [Fact]
    public void ExplicitFolderDeletionRemovesMissingRowsWhilePreservingNestedParentBeforeDelete()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        var anchor = new DateTimeOffset(Created, TimeSpan.Zero);
        adapter.Store.Folders.Upsert(new FolderRecord
        {
            Id = "id-child",
            Name = "child-token",
            ParentId = "id-f-keep",
            SortOrder = 1,
            CreatedAtUtc = anchor,
            UpdatedAtUtc = anchor
        });

        adapter.SaveMeetingFolders(
            [
                new PersistedMeetingFolder("id-f-keep", "folder-token"),
            new PersistedMeetingFolder("id-child", "child-renamed", "id-f-keep")
            ]);
        Assert.Equal("id-f-keep", adapter.Store.Folders.Find("id-child")!.ParentId);

        adapter.SaveMeetingFolders(
            [new PersistedMeetingFolder("id-f-keep", "folder-token")],
            afterExplicitDeletion: true);

        Assert.Null(adapter.Store.Folders.Find("id-child"));
    }

    [Fact]
    public void ClearedNotesEditedTranscriptAndAliasesRemoveChildRows()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        adapter.Store.Meetings.SetTranscript("id-m-keep", MeetingTranscriptKind.Edited, "edited-token");
        adapter.Store.Meetings.ReplaceSpeakerAliases(
            "id-m-keep",
            new Dictionary<string, string> { ["Speaker 2"] = "alias-b" });

        var loaded = Assert.Single(adapter.LoadMeetings());
        adapter.SaveMeetings(
        [
            loaded with
            {
                Summary = "",
                ManualNotes = "",
                Transcript = "",
                SpeakerAliases = []
            }
        ]);

        var detail = adapter.Store.Meetings.FindDetail("id-m-keep")!;
        Assert.Null(detail.Note(MeetingNoteKind.Generated));
        Assert.Null(detail.Note(MeetingNoteKind.Manual));
        Assert.Null(detail.Transcript(MeetingTranscriptKind.Edited));
        Assert.Empty(detail.SpeakerAliases);
        Assert.Equal(TranscriptToken, detail.Transcript(MeetingTranscriptKind.Raw));
    }

    [Fact]
    public void DictionaryStaysOnJsonAndHasNoSqliteTable()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);

        var entry = Assert.Single(adapter.LoadDictionary());
        Assert.Equal("phrase-token", entry.Phrase);
        Assert.True(File.Exists(directory.File(PersistenceCutover.DictionaryFileName)));
        adapter.SaveDictionary(
        [
            entry,
            new DictionaryEntryRecord { Id = "id-dict-new", Phrase = "phrase-new", Replacement = "repl-new" }
        ]);
        Assert.Equal(2, adapter.LoadDictionary().Count);
        Assert.True(File.Exists(directory.File(PersistenceCutover.DictionaryFileName)));

        var tables = adapter.Store.Database.Read(connection =>
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
        Assert.DoesNotContain(tables, name => name.Contains("dictionary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncompleteMeetingSaveDoesNotDestroyEditedTranscriptOrFollowUps()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        new PersistenceCutover(directory.Path).EnsureMigrated();
        using var adapter = SqliteLibraryHistoryAdapter.Open(directory.Path);
        adapter.Store.Meetings.SetTranscript("id-m-keep", MeetingTranscriptKind.Edited, "edited-token");
        adapter.Store.Meetings.UpsertFollowUp(new FollowUpRecord
        {
            Id = "id-follow",
            MeetingId = "id-m-keep",
            Text = "follow-token",
            Status = FollowUpStatus.Open
        });

        var loaded = Assert.Single(adapter.LoadMeetings());
        Assert.Equal("edited-token", loaded.Transcript);
        adapter.SaveMeetings([
            loaded with
            {
                Transcript = "edited-token",
                Summary = "summary-token",
                ManualNotes = "manual-token",
                SpeakerAliases = []
            }
        ]);

        var detail = adapter.Store.Meetings.FindDetail("id-m-keep")!;
        Assert.Equal("edited-token", detail.Transcript(MeetingTranscriptKind.Edited));
        Assert.Equal(TranscriptToken, detail.Transcript(MeetingTranscriptKind.Raw));
        Assert.Equal("follow-token", Assert.Single(detail.FollowUps).Text);
        Assert.Equal("manual-token", detail.Note(MeetingNoteKind.Manual));
        Assert.Equal(
            """{"runId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","extra":"kept"}""",
            detail.Meeting.AutomationResultJson);
    }

    [Fact]
    public void RetranscriptionSidecarAndSelectionAreNotInventedOrDeleted()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var sidecar = directory.File("retranscription-id-m-keep.json");
        File.WriteAllText(sidecar, """{"schemaVersion":1,"data":{"meetingId":"id-m-keep","status":"Ready"}}""");

        var result = new PersistenceCutover(directory.Path).EnsureMigrated();
        Assert.Equal(JsonMigrationOutcome.Migrated, result.Outcome);
        Assert.True(File.Exists(sidecar));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory.Path).Select(Path.GetFileName),
            name => name is not null && name.Contains("selected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RollbackOfSuccessfulFirstImportRemovesDbAndKeepsJson()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var before = FingerprintJson(directory.Path);
        var cutover = new PersistenceCutover(directory.Path);
        var result = cutover.EnsureMigrated();
        Assert.True(File.Exists(result.DatabasePath));

        cutover.Rollback(result);

        Assert.False(File.Exists(result.DatabasePath));
        Assert.Equal(before, FingerprintJson(directory.Path));
        Assert.True(Directory.Exists(result.JsonSnapshotDirectory));
    }

    [Fact]
    public void RollbackRemovesDatabaseSidecarsBeforeDeletingNewDatabase()
    {
        using var directory = new TestDirectory();
        WriteClonedProfile(directory.Path);
        var cutover = new PersistenceCutover(directory.Path);
        var result = cutover.EnsureMigrated();
        File.WriteAllText(result.DatabasePath + "-wal", "sidecar");
        File.WriteAllText(result.DatabasePath + "-shm", "sidecar");

        cutover.Rollback(result);

        Assert.False(File.Exists(result.DatabasePath));
        Assert.False(File.Exists(result.DatabasePath + "-wal"));
        Assert.False(File.Exists(result.DatabasePath + "-shm"));
    }

    [QualificationFact("MUESLI_CLONED_PROFILE_DIR")]
    public void OperatorClonedProfileMigratesWithMatchingCountsRollbackAndSecondLaunchIdempotence()
    {
        var source = Environment.GetEnvironmentVariable("MUESLI_CLONED_PROFILE_DIR")!;

        using var rollbackDirectory = new TestDirectory();
        CopyHistoryFiles(source, rollbackDirectory.Path);
        var rollbackSnapshot = JsonHistorySnapshotReader.Read(rollbackDirectory.Path);
        var rollback = new PersistenceCutover(
            rollbackDirectory.Path,
            options: new PersistenceCutoverOptions { FailAfterJsonSnapshot = true }).EnsureMigrated();
        Assert.Equal(JsonMigrationOutcome.Failed, rollback.Outcome);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(rollbackDirectory.Path)));
        Assert.Equal(rollbackSnapshot.RecordCount, JsonHistorySnapshotReader.Read(rollbackDirectory.Path).RecordCount);

        using var migrateDirectory = new TestDirectory();
        CopyHistoryFiles(source, migrateDirectory.Path);
        var expected = JsonHistorySnapshotReader.Read(migrateDirectory.Path);
        var cutover = new PersistenceCutover(migrateDirectory.Path);
        var first = cutover.EnsureMigrated();
        Assert.Equal(JsonMigrationOutcome.Migrated, first.Outcome);
        using (var store = MuesliPersistenceStore.Open(first.DatabasePath))
        {
            Assert.Equal(expected.Dictations.Count, store.Dictations.Count());
            Assert.Equal(expected.Meetings.Count, store.Meetings.Count());
            Assert.Equal(first.Counts.Dictations, store.Dictations.Count());
            Assert.Equal(first.Counts.Meetings, store.Meetings.Count());
        }

        var jsonBefore = FingerprintJson(migrateDirectory.Path);
        var second = cutover.EnsureMigrated();
        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, second.Outcome);
        Assert.Equal(first.SourceFingerprint, second.SourceFingerprint);
        Assert.Equal(jsonBefore, FingerprintJson(migrateDirectory.Path));
    }

    private static void CopyHistoryFiles(string sourceDirectory, string destinationDirectory)
    {
        foreach (var name in new[]
                 {
                     JsonHistorySnapshotReader.DictationsFileName,
                     JsonHistorySnapshotReader.MeetingsFileName,
                     JsonHistorySnapshotReader.FoldersFileName,
                     JsonHistorySnapshotReader.TemplatesFileName,
                     PersistenceCutover.DictionaryFileName
                 })
        {
            var source = Path.Combine(sourceDirectory, name);
            if (File.Exists(source))
            {
                File.Copy(source, Path.Combine(destinationDirectory, name), overwrite: true);
            }
        }
    }

    private static void WriteClonedProfile(string dataDirectory)
    {
        var store = new AppDataStore(dataDirectory);
        store.SaveMeetingFolders([new PersistedMeetingFolder("id-f-keep", "folder-token")]);
        store.SaveMeetingTemplates(
        [
            new PersistedMeetingTemplate
            {
                Id = "id-t-keep",
                Name = "template-token",
                Prompt = "prompt-token",
                Icon = "icon-token"
            }
        ]);
        store.SaveDictations(
        [
            new PersistedDictation("id-d-old", Created, "body-old", 100, "model-token"),
            new PersistedDictation("id-d-new", Created.AddMinutes(5), "body-new", 200, "model-token")
        ]);
        store.SaveMeetings(
        [
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Id = "id-m-keep",
                Title = TitleToken,
                TitleIsManual = true,
                CreatedAt = Created.AddHours(-2),
                Transcript = TranscriptToken,
                Summary = "summary-token",
                ManualNotes = "manual-token",
                SourcePath = AudioPathToken,
                MicrophoneAudioPath = AudioPathToken,
                SystemCaptureMode = "process-loopback",
                ModelProfile = "model-token",
                FolderId = "id-f-keep",
                TemplateName = "template-token",
                SpeakerAliases = new Dictionary<string, string> { ["Speaker 1"] = "alias-a" },
                AutomationResult = null
            }
        ]);
        var meetingsPath = Path.Combine(dataDirectory, "windows-meetings.json");
        var json = File.ReadAllText(meetingsPath);
        var injected = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"automationResult\"\\s*:\\s*null",
            "\"automationResult\": {\"runId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"extra\":\"kept\"}",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (string.Equals(json, injected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Could not inject verbatim automation JSON into the cloned profile.");
        }

        File.WriteAllText(meetingsPath, injected);
        store.SaveDictionary(
        [
            new DictionaryEntryRecord
            {
                Id = "id-dict-keep",
                Phrase = "phrase-token",
                Replacement = "repl-token",
                MatchingThreshold = 0.91
            }
        ]);
    }

    private static void WriteCaptureGeneration(string dataDirectory, string generation)
    {
        var store = new AppDataStore(dataDirectory);
        store.SaveDictations([
            new PersistedDictation(
                $"id-d-{generation}",
                Created.AddMinutes(10),
                $"body-{generation}",
                100,
                "model-token")
        ]);
        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Id = $"id-m-{generation}",
                Title = $"title-{generation}",
                CreatedAt = Created.AddMinutes(10),
                Transcript = $"transcript-{generation}"
            }
        ]);
    }

    private static List<string> FingerprintJson(string directory) =>
        Directory.EnumerateFiles(directory, "windows-*.json*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:" +
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
            .ToList();

    private static void AssertNoSensitivePayload(IEnumerable<string> reports, string? failure)
    {
        var text = string.Join('\n', reports.Append(failure ?? ""));
        Assert.DoesNotContain(TitleToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AudioPathToken, text, StringComparison.Ordinal);
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(junctionPath);
        process.StartInfo.ArgumentList.Add(targetPath);
        Assert.True(process.Start(), "Could not launch the Windows junction fixture helper.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create the deterministic Windows junction fixture: {process.StandardError.ReadToEnd()}" );
        }
    }
}
