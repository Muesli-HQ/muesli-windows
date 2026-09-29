using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class PersistenceRepositoryTests
{
    private static readonly DateTimeOffset Anchor =
        new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DictationRoundTripsEveryField()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        var dictation = new DictationRecord
        {
            Id = "d1",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor.AddMinutes(1),
            Title = "Pricing note",
            Text = "Send the pricing deck to procurement.",
            DurationMs = 4200,
            ModelProfile = "parakeet",
            WordCount = 6,
            AudioPath = @"C:\captures\d1.wav",
            AudioRetained = true
        };

        store.Dictations.Upsert(dictation);

        Assert.Equal(dictation, store.Dictations.Find("d1"));
        Assert.Equal(1, store.Dictations.Count());
    }

    [Fact]
    public void TimestampsSurviveToTheTick()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        var exact = Anchor.AddTicks(1234567);
        store.Dictations.Upsert(new DictationRecord { Id = "d1", CreatedAtUtc = exact, UpdatedAtUtc = exact });

        Assert.Equal(exact.UtcTicks, store.Dictations.Find("d1")!.CreatedAtUtc.UtcTicks);
    }

    [Fact]
    public void UpsertReplacesTheExistingRowRatherThanAddingOne()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "first", CreatedAtUtc = Anchor });
        store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "second", CreatedAtUtc = Anchor });

        Assert.Equal(1, store.Dictations.Count());
        Assert.Equal("second", store.Dictations.Find("d1")!.Text);
    }

    [Fact]
    public void DeleteReportsWhetherAnythingWasRemoved()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Dictations.Upsert(new DictationRecord { Id = "d1", CreatedAtUtc = Anchor });

        Assert.True(store.Dictations.Delete("d1"));
        Assert.False(store.Dictations.Delete("d1"));
        Assert.Null(store.Dictations.Find("d1"));
    }

    [Fact]
    public void DictationListPagesAndFiltersWithoutLoadingTheWholeHistory()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "f1", Name = "Filed", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        for (var index = 0; index < 30; index++)
        {
            store.Dictations.Upsert(new DictationRecord
            {
                Id = $"d{index:D2}",
                CreatedAtUtc = Anchor.AddMinutes(index),
                UpdatedAtUtc = Anchor.AddMinutes(index),
                FolderId = index % 3 == 0 ? "f1" : null
            });
        }

        var newest = store.Dictations.List(new DictationQuery { Limit = 5 });
        Assert.Equal(5, newest.Count);
        Assert.Equal("d29", newest[0].Id);

        var second = store.Dictations.List(new DictationQuery { Limit = 5, Offset = 5 });
        Assert.Equal("d24", second[0].Id);
        Assert.Empty(newest.Select(item => item.Id).Intersect(second.Select(item => item.Id)));

        var oldest = store.Dictations.List(new DictationQuery { Limit = 1, Sort = HistorySort.OldestFirst });
        Assert.Equal("d00", Assert.Single(oldest).Id);

        var filed = store.Dictations.List(new DictationQuery { FilterByFolder = true, FolderId = "f1", Limit = 100 });
        Assert.Equal(10, filed.Count);
        Assert.All(filed, item => Assert.Equal("f1", item.FolderId));

        var unfiled = store.Dictations.List(new DictationQuery { FilterByFolder = true, FolderId = null, Limit = 100 });
        Assert.Equal(20, unfiled.Count);

        var window = store.Dictations.List(new DictationQuery
        {
            Since = Anchor.AddMinutes(10),
            Until = Anchor.AddMinutes(12),
            Limit = 100
        });
        Assert.Equal(["d12", "d11", "d10"], window.Select(item => item.Id));
    }

    [Fact]
    public void DeleteAllRemovesEveryDictationAndItsSearchEntries()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "confidential transcript", CreatedAtUtc = Anchor });
        store.Dictations.Upsert(new DictationRecord { Id = "d2", Text = "another transcript", CreatedAtUtc = Anchor });

        Assert.Equal(2, store.Dictations.DeleteAll());
        Assert.Equal(0, store.Dictations.Count());
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "confidential" }));
    }

    [Fact]
    public void MeetingSavesAndReloadsItsNotesTranscriptsAliasesAndFollowUps()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Save(Meeting());

        var detail = store.Meetings.FindDetail("m1")!;
        Assert.Equal("Northwind quarterly review", detail.Meeting.Title);
        Assert.True(detail.Meeting.TitleIsManual);
        Assert.Equal("clients", detail.Meeting.FolderId);
        Assert.Equal(["microphone dropped for 3s"], detail.Meeting.HealthWarnings);
        Assert.Equal(@"C:\captures\m1-microphone.wav", detail.Meeting.Audio.MicrophoneAudioPath);
        Assert.Equal("process-loopback", detail.Meeting.Audio.SystemCaptureMode);
        Assert.Equal("""{"runId":"abc"}""", detail.Meeting.AutomationResultJson);
        Assert.Equal("## Summary\nRenewal is on track.", detail.Note(MeetingNoteKind.Generated));
        Assert.Equal("Ask Dana about the overage.", detail.Note(MeetingNoteKind.Manual));
        Assert.Equal("Speaker 1: welcome.", detail.Transcript(MeetingTranscriptKind.Raw));
        Assert.Equal("Dana: welcome.", detail.Transcript(MeetingTranscriptKind.Edited));
        Assert.Equal("Dana Whitfield", Assert.Single(detail.SpeakerAliases).Alias);
        Assert.Equal("Send the revised quote", Assert.Single(detail.FollowUps).Text);
    }

    [Fact]
    public void RegeneratingTheGeneratedNoteLeavesTheManualNoteAlone()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Save(Meeting());

        store.Meetings.SetNote("m1", MeetingNoteKind.Generated, "## Summary\nRewritten.");

        var detail = store.Meetings.FindDetail("m1")!;
        Assert.Equal("## Summary\nRewritten.", detail.Note(MeetingNoteKind.Generated));
        Assert.Equal("Ask Dana about the overage.", detail.Note(MeetingNoteKind.Manual));
    }

    [Fact]
    public void EditingATranscriptDoesNotOverwriteTheModelOutput()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Save(Meeting());

        store.Meetings.SetTranscript("m1", MeetingTranscriptKind.Edited, "Dana: welcome everybody.");

        var detail = store.Meetings.FindDetail("m1")!;
        Assert.Equal("Speaker 1: welcome.", detail.Transcript(MeetingTranscriptKind.Raw));
        Assert.Equal("Dana: welcome everybody.", detail.Transcript(MeetingTranscriptKind.Edited));
    }

    [Fact]
    public void SavingAMeetingDropsTheChildRowsTheCallerRemoved()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        var meeting = Meeting();
        store.Meetings.Save(meeting);

        store.Meetings.Save(meeting with
        {
            Notes = [meeting.Notes.Single(note => note.Kind == MeetingNoteKind.Manual)],
            Transcripts = [],
            SpeakerAliases = [],
            FollowUps = []
        });

        var detail = store.Meetings.FindDetail("m1")!;
        Assert.Null(detail.Note(MeetingNoteKind.Generated));
        Assert.Equal("Ask Dana about the overage.", detail.Note(MeetingNoteKind.Manual));
        Assert.Empty(detail.Transcripts);
        Assert.Empty(detail.SpeakerAliases);
        Assert.Empty(detail.FollowUps);
    }

    [Fact]
    public void DeletingAMeetingRemovesEverythingThatHungOffIt()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Save(Meeting());

        Assert.True(store.Meetings.Delete("m1"));

        Assert.Null(store.Meetings.FindDetail("m1"));
        Assert.Empty(store.Meetings.ListNotes("m1"));
        Assert.Empty(store.Meetings.ListTranscripts("m1"));
        Assert.Empty(store.Meetings.GetSpeakerAliases("m1"));
        Assert.Empty(store.Meetings.ListFollowUps("m1"));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "renewal" }));
    }

    [Fact]
    public void FollowUpsLinkTheMeetingThatResolvedThem()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Save(Meeting());
        store.Meetings.Upsert(new MeetingRecord { Id = "m2", Title = "Follow-up call", CreatedAtUtc = Anchor.AddDays(7) });
        store.Dictations.Upsert(new DictationRecord { Id = "d1", Text = "quote sent", CreatedAtUtc = Anchor.AddDays(6) });

        store.Meetings.UpsertFollowUp(new FollowUpRecord
        {
            Id = "f1",
            MeetingId = "m1",
            Text = "Send the revised quote",
            Owner = "Dana Whitfield",
            Status = FollowUpStatus.Done,
            DueAtUtc = Anchor.AddDays(3),
            LinkedMeetingId = "m2",
            LinkedDictationId = "d1",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor.AddDays(7)
        });

        var followUp = Assert.Single(store.Meetings.ListFollowUps("m1"));
        Assert.Equal(FollowUpStatus.Done, followUp.Status);
        Assert.Equal("m2", followUp.LinkedMeetingId);
        Assert.Equal("d1", followUp.LinkedDictationId);
        Assert.Equal(Anchor.AddDays(3), followUp.DueAtUtc);
        Assert.Equal("f1", Assert.Single(store.Meetings.ListFollowUpsLinkedTo("m2")).Id);

        // Deleting the meeting a follow-up merely points at must not delete the follow-up.
        Assert.True(store.Meetings.Delete("m2"));
        Assert.Null(Assert.Single(store.Meetings.ListFollowUps("m1")).LinkedMeetingId);
    }

    [Fact]
    public void MeetingListFiltersBySessionStateAndFolder()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord { Id = "f1", Name = "Filed", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
        store.Meetings.Upsert(new MeetingRecord { Id = "m1", CreatedAtUtc = Anchor, FolderId = "f1" });
        store.Meetings.Upsert(new MeetingRecord { Id = "m2", CreatedAtUtc = Anchor.AddHours(1), SessionState = MeetingSessionState.Failed });
        store.Meetings.Upsert(new MeetingRecord { Id = "m3", CreatedAtUtc = Anchor.AddHours(2) });

        Assert.Equal(["m3", "m1"], store.Meetings
            .List(new MeetingQuery { SessionState = MeetingSessionState.Completed, Limit = 10 })
            .Select(meeting => meeting.Id));
        Assert.Equal("m1", Assert.Single(store.Meetings
            .List(new MeetingQuery { FilterByFolder = true, FolderId = "f1", Limit = 10 })).Id);
        Assert.Equal(3, store.Meetings.Count());
    }

    [Fact]
    public void SpeakerAliasesAreReplacedWholesale()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Meetings.Upsert(new MeetingRecord { Id = "m1", CreatedAtUtc = Anchor });

        store.Meetings.ReplaceSpeakerAliases("m1", new Dictionary<string, string>
        {
            ["Speaker 1"] = "Dana",
            ["Speaker 2"] = "Ravi"
        });
        store.Meetings.ReplaceSpeakerAliases("m1", new Dictionary<string, string> { ["Speaker 1"] = "Dana Whitfield" });

        var aliases = store.Meetings.GetSpeakerAliases("m1");
        Assert.Equal("Dana Whitfield", Assert.Single(aliases).Value);
    }

    [Fact]
    public void WritingChildRowsForAMissingMeetingIsRejected()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        Assert.Throws<PersistenceException>(() => store.Meetings.SetNote("nope", MeetingNoteKind.Manual, "orphan"));
        Assert.Throws<PersistenceException>(() => store.Meetings.SetTranscript("nope", MeetingTranscriptKind.Raw, "orphan"));
    }

    [Fact]
    public void FoldersFormATreeWithAncestorsAndSubtrees()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        Folder(store, "root", "Clients", null);
        Folder(store, "child", "Northwind", "root");
        Folder(store, "grandchild", "Renewals", "child");

        Assert.Equal(["root", "child"], store.Folders.ListAncestors("grandchild").Select(folder => folder.Id));
        Assert.Equal(["root", "child", "grandchild"], store.Folders.ListSubtree("root").Select(folder => folder.Id));
        Assert.Equal("child", Assert.Single(store.Folders.ListChildren("root")).Id);
        Assert.Equal("root", Assert.Single(store.Folders.ListChildren(null)).Id);
        Assert.Equal(3, store.Folders.Count());
    }

    [Fact]
    public void AFolderCannotBeMovedIntoItsOwnSubtree()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        Folder(store, "root", "Clients", null);
        Folder(store, "child", "Northwind", "root");

        Assert.Throws<PersistenceException>(() => store.Folders.Move("root", "child"));
        Assert.Throws<PersistenceException>(() => store.Folders.Move("root", "root"));
        Assert.Equal("root", store.Folders.Find("child")!.ParentId);
    }

    [Fact]
    public void DeletingAFolderUnfilesItsHistoryInsteadOfDeletingIt()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        Folder(store, "root", "Clients", null);
        Folder(store, "child", "Northwind", "root");
        store.Meetings.Upsert(new MeetingRecord { Id = "m1", CreatedAtUtc = Anchor, FolderId = "child" });
        store.Dictations.Upsert(new DictationRecord { Id = "d1", CreatedAtUtc = Anchor, FolderId = "root" });

        Assert.True(store.Folders.Delete("root", FolderDeleteMode.CascadeFolders));

        Assert.Equal(0, store.Folders.Count());
        Assert.NotNull(store.Meetings.Find("m1"));
        Assert.Null(store.Meetings.Find("m1")!.FolderId);
        Assert.NotNull(store.Dictations.Find("d1"));
        Assert.Null(store.Dictations.Find("d1")!.FolderId);
    }

    [Fact]
    public void DetachingAFolderPromotesItsChildrenToItsParent()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        Folder(store, "root", "Clients", null);
        Folder(store, "child", "Northwind", "root");
        Folder(store, "grandchild", "Renewals", "child");

        Assert.True(store.Folders.Delete("child"));

        Assert.Equal(["grandchild", "root"], store.Folders.List().Select(folder => folder.Id).Order());
        Assert.Equal("root", store.Folders.Find("grandchild")!.ParentId);
    }

    [Fact]
    public void TemplatesListInPresentationOrderAndResolveByName()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Templates.UpsertRange(
        [
            new TemplateRecord { Id = "t2", Name = "Standup", SortOrder = 2, CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor },
            new TemplateRecord { Id = "t1", Name = "Client review", Prompt = "Summarise risk", SortOrder = 1, CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor }
        ]);

        Assert.Equal(["t1", "t2"], store.Templates.List().Select(template => template.Id));
        Assert.Equal("Summarise risk", store.Templates.FindByName("client review")!.Prompt);
        Assert.True(store.Templates.Delete("t1"));
        Assert.Equal(1, store.Templates.Count());
    }

    [Fact]
    public void CommittedTransactionsPersistAndRolledBackOnesLeaveNothing()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        using (var transaction = store.BeginTransaction())
        {
            store.Dictations.Upsert(new DictationRecord { Id = "kept", Text = "kept text", CreatedAtUtc = Anchor });
            transaction.Commit();
        }

        using (var transaction = store.BeginTransaction())
        {
            store.Dictations.Upsert(new DictationRecord { Id = "discarded", Text = "discarded text", CreatedAtUtc = Anchor });
            transaction.Rollback();
        }

        Assert.NotNull(store.Dictations.Find("kept"));
        Assert.Null(store.Dictations.Find("discarded"));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "kept" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "discarded" }));
    }

    [Fact]
    public void ATransactionLeftUncommittedRollsBackOnDispose()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        Action failingUnitOfWork = () =>
        {
            using var transaction = store.BeginTransaction();
            store.Dictations.Upsert(new DictationRecord { Id = "half-written", CreatedAtUtc = Anchor });
            throw new InvalidOperationException("simulated failure mid-unit-of-work");
        };

        Assert.Throws<InvalidOperationException>(failingUnitOfWork);

        Assert.Null(store.Dictations.Find("half-written"));
        Assert.Equal(0, store.Dictations.Count());
    }

    [Fact]
    public void ATransactionSpansEveryRepository()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        using (var transaction = store.BeginTransaction())
        {
            Folder(store, "f1", "Clients", null);
            store.Templates.Upsert(new TemplateRecord { Id = "t1", Name = "Review", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
            store.Meetings.Upsert(new MeetingRecord { Id = "m1", CreatedAtUtc = Anchor, FolderId = "f1" });
            store.Dictations.Upsert(new DictationRecord { Id = "d1", CreatedAtUtc = Anchor, FolderId = "f1" });
            transaction.Rollback();
        }

        Assert.Equal(0, store.Folders.Count());
        Assert.Equal(0, store.Templates.Count());
        Assert.Equal(0, store.Meetings.Count());
        Assert.Equal(0, store.Dictations.Count());
    }

    [Fact]
    public void ANestedScopeThatRollsBackStopsTheOuterCommit()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();

        using (var outer = store.BeginTransaction())
        {
            store.Dictations.Upsert(new DictationRecord { Id = "d1", CreatedAtUtc = Anchor });
            using (var inner = store.BeginTransaction())
            {
                inner.Rollback();
            }

            Assert.Throws<PersistenceException>(() => outer.Commit());
        }

        Assert.Equal(0, store.Dictations.Count());
    }

    [Fact]
    public void DataSurvivesClosingAndReopeningTheDatabaseFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.db");

        using (var store = MuesliPersistenceStore.Open(path))
        {
            store.Folders.Upsert(new FolderRecord { Id = "clients", Name = "Clients", CreatedAtUtc = Anchor, UpdatedAtUtc = Anchor });
            store.Meetings.Save(Meeting());
        }

        using (var store = MuesliPersistenceStore.Open(path))
        {
            Assert.Equal("Ask Dana about the overage.", store.Meetings.FindDetail("m1")!.Note(MeetingNoteKind.Manual));
            Assert.Single(store.Search.Search(new SearchQuery { Text = "renewal" }));
        }
    }

    internal static MeetingDetail Meeting() =>
        new()
        {
            Meeting = new MeetingRecord
            {
                Id = "m1",
                Title = "Northwind quarterly review",
                TitleIsManual = true,
                CreatedAtUtc = Anchor.AddHours(-2),
                UpdatedAtUtc = Anchor,
                DurationMs = 3_600_000,
                ModelProfile = "whisper-large",
                FolderId = "clients",
                WordCount = 5400,
                TemplateName = "Client review",
                HealthWarnings = ["microphone dropped for 3s"],
                Audio = new MeetingAudioOwnership
                {
                    SourcePath = @"C:\captures\m1-microphone.wav;C:\captures\m1-system.wav",
                    MicrophoneAudioPath = @"C:\captures\m1-microphone.wav",
                    SystemAudioPath = @"C:\captures\m1-system.wav",
                    SystemCaptureMode = "process-loopback",
                    LiveTranscriptOwnership = "preview",
                    FinalTranscriptOwnerModelId = "whisper-large"
                },
                AutomationResultJson = """{"runId":"abc"}"""
            },
            Notes =
            [
                new MeetingNote { MeetingId = "m1", Kind = MeetingNoteKind.Generated, Content = "## Summary\nRenewal is on track." },
                new MeetingNote { MeetingId = "m1", Kind = MeetingNoteKind.Manual, Content = "Ask Dana about the overage." }
            ],
            Transcripts =
            [
                new MeetingTranscript { MeetingId = "m1", Kind = MeetingTranscriptKind.Raw, Content = "Speaker 1: welcome." },
                new MeetingTranscript { MeetingId = "m1", Kind = MeetingTranscriptKind.Edited, Content = "Dana: welcome." }
            ],
            SpeakerAliases =
            [
                new SpeakerAliasRecord { MeetingId = "m1", SpeakerKey = "Speaker 1", Alias = "Dana Whitfield" }
            ],
            FollowUps =
            [
                new FollowUpRecord
                {
                    Id = "f1",
                    MeetingId = "m1",
                    Text = "Send the revised quote",
                    Owner = "Dana Whitfield",
                    CreatedAtUtc = Anchor,
                    UpdatedAtUtc = Anchor
                }
            ]
        };

    private static void Folder(MuesliPersistenceStore store, string id, string name, string? parentId) =>
        store.Folders.Upsert(new FolderRecord
        {
            Id = id,
            Name = name,
            ParentId = parentId,
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor
        });
}
