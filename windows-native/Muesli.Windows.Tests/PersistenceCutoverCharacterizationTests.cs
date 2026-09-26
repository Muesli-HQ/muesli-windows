using System.IO;
using System.Text.Json;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

/// <summary>
/// Golden tests of CURRENT production history behavior before L27 cutover. These lock awkward
/// JSON AppDataStore semantics and the unused SQLite repository gaps. They do not run migration
/// as a success path and they do not invent a selected-item field that the product does not store.
/// </summary>
public sealed class PersistenceCutoverCharacterizationTests
{
    private static readonly DateTime Early = new(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Late = new(2026, 1, 3, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Anchor = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void MissingHistoryFilesLoadAsEmptyListsWithoutAWarning()
    {
        using var directory = new TestDirectory();
        ILibraryHistoryAdapter store = new JsonLibraryHistoryAdapter(new AppDataStore(directory.Path));

        Assert.Empty(store.LoadDictations());
        Assert.Empty(store.LoadMeetings());
        Assert.Empty(store.LoadMeetingFolders());
        Assert.Empty(store.LoadDictionary());
        Assert.Empty(store.LoadMeetingTemplates());
        Assert.Null(store.LastWarning);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public void AppDataStoreReturnsSaveOrderAndDoesNotSort()
    {
        using var directory = new TestDirectory();
        ILibraryHistoryAdapter store = new JsonLibraryHistoryAdapter(new AppDataStore(directory.Path));
        store.SaveDictations([
            Dictation("id-d-old", Early, "body-old"),
            Dictation("id-d-new", Late, "body-new")
        ]);
        store.SaveMeetings([
            Meeting("id-m-old", Early, "title-old"),
            Meeting("id-m-new", Late, "title-new")
        ]);

        Assert.Equal(["id-d-old", "id-d-new"], store.LoadDictations().Select(item => item.Id));
        Assert.Equal(["id-m-old", "id-m-new"], store.LoadMeetings().Select(item => item.Id));
    }

    [Fact]
    public void FeatureRuntimeDisplayOrderIsNewestTimestampFirstRegardlessOfFileOrder()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations([
            Dictation("id-d-old", Early, "body-old"),
            Dictation("id-d-new", Late, "body-new")
        ]);
        store.SaveMeetings([
            Meeting("id-m-old", Early, "title-old"),
            Meeting("id-m-new", Late, "title-new")
        ]);

        // Copied from FeatureRuntime.xaml.cs LoadPersistedData: OrderByDescending Timestamp/CreatedAt.
        var dictationOrder = store.LoadDictations().OrderByDescending(item => item.Timestamp).Select(item => item.Id);
        var meetingOrder = store.LoadMeetings().OrderByDescending(item => item.CreatedAt).Select(item => item.Id);

        Assert.Equal(["id-d-new", "id-d-old"], dictationOrder);
        Assert.Equal(["id-m-new", "id-m-old"], meetingOrder);
    }

    [Fact]
    public void HistoryJsonDoesNotPersistVisibleSelection()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations([Dictation("id-d-sel", Late, "body-sel")]);
        store.SaveMeetings([Meeting("id-m-sel", Late, "title-sel")]);
        store.SaveMeetingFolders([new PersistedMeetingFolder("id-f-sel", "folder-sel")]);

        Assert.DoesNotContain("selected", FileNames(directory.Path), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("selectedMeetingId", JsonPropertyNames(directory.File("windows-meetings.json")));
        Assert.DoesNotContain("selectedDictationId", JsonPropertyNames(directory.File("windows-dictations.json")));
        Assert.DoesNotContain("selectedFolderId", JsonPropertyNames(directory.File("windows-meeting-folders.json")));
        var navigation = new NavigationService();
        Assert.Null(navigation.State.SelectedMeetingId);
    }

    [Fact]
    public void JsonFoldersRoundTripParentId()
    {
        using var directory = new TestDirectory();
        var path = directory.File("windows-meeting-folders.json");
        File.WriteAllText(
            path,
            """
            {"schemaVersion":1,"data":[{"id":"id-child","name":"child-token","parentId":"id-root"}]}
            """);

        var loaded = new AppDataStore(directory.Path).LoadMeetingFolders();
        var folder = Assert.Single(loaded);
        Assert.Equal("id-child", folder.Id);
        Assert.Equal("child-token", folder.Name);
        Assert.Equal("id-root", folder.ParentId);
    }

    [Fact]
    public void SqliteFoldersRoundTripParentIdWhichJsonCannotStore()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord
        {
            Id = "id-root",
            Name = "root-token",
            ParentId = null,
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor
        });
        store.Folders.Upsert(new FolderRecord
        {
            Id = "id-child",
            Name = "child-token",
            ParentId = "id-root",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor
        });

        Assert.Equal("id-root", store.Folders.Find("id-child")!.ParentId);
        Assert.Equal("id-child", Assert.Single(store.Folders.ListChildren("id-root")).Id);
        Assert.Equal("id-root", Assert.Single(store.Folders.ListChildren(null)).Id);
    }

    [Fact]
    public void NotesAliasesTemplatesAndAutomationRoundTripThroughJson()
    {
        using var directory = new TestDirectory();
        ILibraryHistoryAdapter store = new JsonLibraryHistoryAdapter(new AppDataStore(directory.Path));
        var aliases = new Dictionary<string, string> { ["Speaker 1"] = "alias-a" };
        var automation = new PostMeetingAutomationResult(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            PostMeetingAutomationStatus.Succeeded,
            Anchor,
            Anchor.AddMinutes(1),
            1,
            0,
            "stdout-token",
            "",
            false,
            false,
            null,
            new PostMeetingExportDiagnostic(
                true,
                true,
                "dest-token",
                AutomationDestinationOwnership.UserSelectedDestination,
                null,
                1));

        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Id = "id-m-keep",
                Title = "title-token",
                CreatedAt = Late,
                Transcript = "transcript-token",
                Summary = "summary-token",
                ManualNotes = "manual-token",
                TitleIsManual = true,
                FolderId = "id-f-keep",
                TemplateName = "template-token",
                SpeakerAliases = aliases,
                AutomationResult = automation,
                SourcePath = "owned-audio-a",
                MicrophoneAudioPath = "owned-audio-mic",
                SystemAudioPath = "owned-audio-sys"
            }
        ]);
        store.SaveMeetingFolders([new PersistedMeetingFolder("id-f-keep", "folder-token")]);
        store.SaveMeetingTemplates([
            new PersistedMeetingTemplate
            {
                Id = "id-t-keep",
                Name = "template-token",
                Prompt = "prompt-token",
                Icon = "icon-token"
            }
        ]);
        store.SaveDictionary([
            new DictionaryEntryRecord
            {
                Id = "id-dict-keep",
                Phrase = "phrase-token",
                Replacement = "repl-token",
                MatchingThreshold = 0.91
            }
        ]);

        var meeting = Assert.Single(store.LoadMeetings());
        Assert.Equal("manual-token", meeting.ManualNotes);
        Assert.True(meeting.TitleIsManual);
        Assert.Equal("summary-token", meeting.Summary);
        Assert.Equal("alias-a", meeting.SpeakerAliases["Speaker 1"]);
        Assert.Equal("id-f-keep", meeting.FolderId);
        Assert.Equal("template-token", meeting.TemplateName);
        Assert.Equal(automation, meeting.AutomationResult);
        Assert.Equal("owned-audio-mic", meeting.MicrophoneAudioPath);

        var folder = Assert.Single(store.LoadMeetingFolders());
        Assert.Equal("id-f-keep", folder.Id);
        Assert.Equal("folder-token", folder.Name);

        var template = Assert.Single(store.LoadMeetingTemplates());
        Assert.Equal("prompt-token", template.Prompt);
        Assert.Equal("template-token", template.Name);

        var entry = Assert.Single(store.LoadDictionary());
        Assert.Equal("phrase-token", entry.Phrase);
        Assert.Equal(0.91, entry.MatchingThreshold);
    }

    [Fact]
    public void SettingsTemplateReferenceIsANameStringNotAHistoryFileField()
    {
        using var directory = new TestDirectory();
        var path = directory.File("windows-settings.json");
        var settings = new SettingsStore(path, new InMemorySecretStore());
        settings.Save(new MuesliSettings { MeetingSummaryTemplate = "template-token" });

        var loaded = settings.Load();
        Assert.Equal("template-token", loaded.MeetingSummaryTemplate);
        Assert.False(File.Exists(directory.File("windows-meetings.json")));
    }

    [Fact]
    public void ProductionInMemorySearchFindsManualNotesAndSqliteSearchStillFindsThem()
    {
        using var directory = new TestDirectory();
        var json = new AppDataStore(directory.Path);
        json.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Id = "id-m-notes",
                Title = "title-plain",
                CreatedAt = Late,
                Transcript = "transcript-plain",
                Summary = "summary-plain",
                ManualNotes = "needle-manual"
            }
        ]);
        var meeting = Assert.Single(json.LoadMeetings());
        Assert.True(ProductionInMemorySearchMatch.MeetingMatches(
            meeting.Title,
            meeting.Summary,
            meeting.Transcript,
            metadata: "",
            query: "needle-manual",
            notes: meeting.ManualNotes));
        Assert.True(ProductionInMemorySearchMatch.MeetingMatches(
            meeting.Title,
            meeting.Summary,
            meeting.Transcript,
            metadata: "",
            query: "title-plain"));

        using var sqlite = MuesliPersistenceStore.OpenInMemory();
        sqlite.Meetings.Save(new MeetingDetail
        {
            Meeting = new MeetingRecord
            {
                Id = "id-m-notes",
                Title = "title-plain",
                CreatedAtUtc = Anchor,
                UpdatedAtUtc = Anchor
            },
            Notes =
            [
                new MeetingNote
                {
                    MeetingId = "id-m-notes",
                    Kind = MeetingNoteKind.Manual,
                    Content = "needle-manual"
                }
            ]
        });

        var hit = Assert.Single(sqlite.Search.Search(new SearchQuery { Text = "needle-manual" }));
        Assert.Equal("id-m-notes", hit.RecordId);
        Assert.Equal(SearchRecordKind.Meeting, hit.Kind);
    }

    [Fact]
    public void CorruptHistoryWithoutBackupQuarantinesTheFileAndReturnsEmpty()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("windows-dictations.json"), "{not-json");
        var store = new AppDataStore(directory.Path);

        var loaded = store.LoadDictations();

        Assert.Empty(loaded);
        Assert.False(File.Exists(directory.File("windows-dictations.json")));
        Assert.Contains("windows-dictations.json", store.LastWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("body-", store.LastWarning, StringComparison.Ordinal);
        Assert.Single(
            Directory.EnumerateFiles(directory.Path),
            path => Path.GetFileName(path).Contains(".corrupt-", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptHistoryWithBackupRestoresThePreviousSnapshot()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations([Dictation("id-d-keep", Early, "body-keep")]);
        store.SaveDictations([Dictation("id-d-later", Late, "body-later")]);
        File.WriteAllText(directory.File("windows-dictations.json"), "{not-json");

        var recovered = store.LoadDictations();

        Assert.Equal("id-d-keep", Assert.Single(recovered).Id);
        Assert.True(File.Exists(directory.File("windows-dictations.json")));
        Assert.Contains("backup", store.LastWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForwardJsonEnvelopeSchemaIsTreatedAsCorruptionNotAsMigratedSuccess()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            directory.File("windows-meetings.json"),
            """{"schemaVersion":2,"data":[{"id":"id-future","title":"title-future"}]}""");
        var store = new AppDataStore(directory.Path);

        var loaded = store.LoadMeetings();

        Assert.Empty(loaded);
        Assert.False(File.Exists(directory.File("windows-meetings.json")));
        Assert.Contains("windows-meetings.json", store.LastWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("id-future", store.LastWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("title-future", store.LastWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void ForwardMeetingRecordSchemaThrowsInsteadOfLoadingOrFakingSuccess()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion + 1,
                Id = "id-future-record",
                Title = "title-future-record",
                CreatedAt = Late
            }
        ]);

        var exception = Assert.Throws<InvalidDataException>(store.LoadMeetings);
        Assert.Contains("id-future-record", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("title-future-record", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(directory.File("windows-meetings.json")));
    }

    [Fact]
    public void SchemaZeroMeetingMigratesToCurrentSchemaAndCompletes()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = 0,
                Id = "id-schema0",
                Title = "title-schema0",
                CreatedAt = Late,
                SourcePath = "owned-audio-legacy"
            }
        ]);

        var loaded = Assert.Single(store.LoadMeetings());
        Assert.Equal(AppDataStore.CurrentMeetingSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(MeetingSessionState.Completed, loaded.SessionState);
        Assert.Equal("owned-audio-legacy", loaded.MicrophoneAudioPath);
        Assert.Equal("legacy-unknown", loaded.SystemCaptureMode);
        Assert.Equal("off", loaded.LiveTranscriptOwnership);
    }

    [Fact]
    public void LastWarningSticksAcrossLaterSuccessfulLoads()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("windows-dictations.json"), "{not-json");
        var store = new AppDataStore(directory.Path);

        Assert.Empty(store.LoadDictations());
        var warning = store.LastWarning;
        Assert.False(string.IsNullOrWhiteSpace(warning));

        store.SaveMeetings([Meeting("id-m-ok", Late, "title-ok")]);
        Assert.Single(store.LoadMeetings());
        Assert.Equal(warning, store.LastWarning);
    }

    [Fact]
    public void SqliteDefaultListLimitDropsRowsJsonWouldReturn()
    {
        using var directory = new TestDirectory();
        var json = new AppDataStore(directory.Path);
        var records = Enumerable.Range(0, 101)
            .Select(index => Dictation($"id-d-{index:D3}", Early.AddMinutes(index), $"body-{index:D3}"))
            .ToList();
        json.SaveDictations(records);
        Assert.Equal(101, json.LoadDictations().Count);

        using var sqlite = MuesliPersistenceStore.OpenInMemory();
        sqlite.Dictations.UpsertRange(records.Select(item => new DictationRecord
        {
            Id = item.Id,
            CreatedAtUtc = new DateTimeOffset(item.Timestamp, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(item.Timestamp, TimeSpan.Zero),
            Text = item.Text,
            DurationMs = item.DurationMs,
            ModelProfile = item.ModelProfile
        }));

        Assert.Equal(100, sqlite.Dictations.List(new DictationQuery()).Count);
        Assert.Equal(101, sqlite.Dictations.List(new DictationQuery { Limit = int.MaxValue }).Count);
        Assert.Equal(101, sqlite.Dictations.Count());
    }

    [Fact]
    public void JsonHistoryIsNotASqliteDatabaseAndMigrationIsNotImplied()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveDictations([Dictation("id-d-json", Late, "body-json")]);

        var historyPath = directory.File("windows-dictations.json");
        var bytes = File.ReadAllBytes(historyPath);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.Null(store.LastWarning);
    }

    [Fact]
    public void DeletionSaveModePurgesArtifactsWithoutAMigratedSuccessPath()
    {
        using var directory = new TestDirectory();
        ILibraryHistoryAdapter store = new JsonLibraryHistoryAdapter(new AppDataStore(directory.Path));
        store.SaveDictations([Dictation("id-d-gone", Late, "secret-token")]);
        store.SaveDictations([Dictation("id-d-keep", Late, "keep-token")], afterExplicitDeletion: true);

        var remaining = Assert.Single(store.LoadDictations());
        Assert.Equal("id-d-keep", remaining.Id);
        foreach (var file in Directory.EnumerateFiles(directory.Path))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SqliteSearchDocumentsDoNotExistOnTheJsonPath()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([
            new PersistedMeeting
            {
                SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
                Id = "id-m-unindexed",
                Title = "title-unindexed",
                CreatedAt = Late,
                ManualNotes = "manual-unindexed"
            }
        ]);

        Assert.False(File.Exists(PersistencePaths.DatabasePathFor(directory.Path)));
        Assert.Equal("manual-unindexed", Assert.Single(store.LoadMeetings()).ManualNotes);
    }

    private static PersistedDictation Dictation(string id, DateTime timestamp, string body) =>
        new(id, timestamp, body, 100, "model-token");

    private static PersistedMeeting Meeting(string id, DateTime createdAt, string title) =>
        new()
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = id,
            Title = title,
            CreatedAt = createdAt,
            Transcript = "transcript-token",
            ModelProfile = "model-token"
        };

    private static IReadOnlyList<string> FileNames(string directory) =>
        Directory.EnumerateFiles(directory).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToList();

    private static HashSet<string> JsonPropertyNames(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNames(document.RootElement, names);
        return names;
    }

    private static void CollectNames(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    CollectNames(property.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectNames(item, names);
                }
                break;
        }
    }

    /// <summary>
    /// Test-only JSON adapter. Not production wiring. L27 replaces this delegation with
    /// repository calls behind the same contract.
    /// </summary>
    private sealed class JsonLibraryHistoryAdapter(AppDataStore store) : ILibraryHistoryAdapter
    {
        public string? LastWarning => store.LastWarning;

        public IReadOnlyList<PersistedDictation> LoadDictations() => store.LoadDictations();

        public void AppendDictation(PersistedDictation dictation)
        {
            var dictations = store.LoadDictations().ToList();
            dictations.Insert(0, dictation);
            store.SaveDictations(dictations);
        }

        public void SaveDictations(IEnumerable<PersistedDictation> dictations, bool afterExplicitDeletion = false)
        {
            if (afterExplicitDeletion)
            {
                store.SaveDictationsAfterDeletion(dictations);
            }
            else
            {
                store.SaveDictations(dictations);
            }
        }

        public IReadOnlyList<PersistedMeeting> LoadMeetings() => store.LoadMeetings();

        public void SaveMeetings(IEnumerable<PersistedMeeting> meetings, bool afterExplicitDeletion = false)
        {
            if (afterExplicitDeletion)
            {
                store.SaveMeetingsAfterDeletion(meetings);
            }
            else
            {
                store.SaveMeetings(meetings);
            }
        }

        public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() => store.LoadMeetingFolders();

        public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders) =>
            store.SaveMeetingFolders(folders);

        public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => store.LoadDictionary();

        public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries) =>
            store.SaveDictionary(entries);

        public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() => store.LoadMeetingTemplates();

        public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates) =>
            store.SaveMeetingTemplates(templates);
    }
}
