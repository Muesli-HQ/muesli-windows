using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class PersistenceSearchTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("quarterly", SearchRecordKind.Meeting, "m1")]      // title
    [InlineData("welcome", SearchRecordKind.Meeting, "m1")]        // raw transcript
    [InlineData("track", SearchRecordKind.Meeting, "m1")]          // generated note
    [InlineData("overage", SearchRecordKind.Meeting, "m1")]        // manual note
    [InlineData("whitfield", SearchRecordKind.Meeting, "m1")]      // speaker alias
    [InlineData("quote", SearchRecordKind.Meeting, "m1")]          // follow-up
    [InlineData("procurement", SearchRecordKind.Dictation, "d1")]  // dictation text
    [InlineData("renewals", SearchRecordKind.Folder, "clients")]   // folder metadata
    public void EverySearchableFieldIsIndexed(string term, SearchRecordKind kind, string recordId)
    {
        using var store = Seed();

        var hit = store.Search.Search(new SearchQuery { Text = term })
            .Single(candidate => candidate.Kind == kind && candidate.RecordId == recordId);

        Assert.Contains("[", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictingTheFieldsExcludesMatchesFoundElsewhere()
    {
        using var store = Seed();

        Assert.Empty(store.Search.Search(new SearchQuery { Text = "procurement", Fields = SearchFields.Title }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "procurement", Fields = SearchFields.DictationText }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "quarterly", Fields = SearchFields.Title }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "welcome", Fields = SearchFields.Notes }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "welcome", Fields = SearchFields.Transcript }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "anything", Fields = SearchFields.None }));
    }

    [Fact]
    public void SearchCanBeNarrowedByRecordKindAndFolder()
    {
        using var store = Seed();

        var meetingsOnly = store.Search.Search(new SearchQuery { Text = "renewals", Kinds = SearchRecordKinds.Meeting });
        Assert.All(meetingsOnly, hit => Assert.Equal(SearchRecordKind.Meeting, hit.Kind));

        var inFolder = store.Search.Search(new SearchQuery { Text = "renewals", FolderId = "clients" });
        Assert.All(inFolder, hit => Assert.Equal("clients", hit.FolderId));
        Assert.NotEmpty(inFolder);

        Assert.Empty(store.Search.Search(new SearchQuery { Text = "renewals", Kinds = SearchRecordKinds.None }));
    }

    [Fact]
    public void TitleMatchesOutrankBodyMatches()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Meetings.Upsert(new MeetingRecord { Id = "body", Title = "Unrelated", CreatedAtUtc = Anchor });
        store.Meetings.SetTranscript("body", MeetingTranscriptKind.Raw, "a passing mention of migration in the body");
        store.Meetings.Upsert(new MeetingRecord { Id = "titled", Title = "Migration planning", CreatedAtUtc = Anchor });

        var hits = store.Search.Search(new SearchQuery { Text = "migration" });

        Assert.Equal("titled", hits[0].RecordId);
        Assert.True(hits[0].Rank < hits[1].Rank, "BM25 ranks better matches lower, so the title hit must sort first.");
    }

    [Fact]
    public void ResultsCanBeOrderedByDateAndPaged()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        for (var index = 0; index < 12; index++)
        {
            store.Dictations.Upsert(new DictationRecord
            {
                Id = $"d{index:D2}",
                Text = "shared term",
                CreatedAtUtc = Anchor.AddMinutes(index),
                UpdatedAtUtc = Anchor.AddMinutes(index)
            });
        }

        var query = new SearchQuery { Text = "shared", Sort = SearchSort.NewestFirst, Limit = 5 };
        var first = store.Search.Search(query);
        var second = store.Search.Search(query with { Offset = 5 });

        Assert.Equal(["d11", "d10", "d09", "d08", "d07"], first.Select(hit => hit.RecordId));
        Assert.Equal(["d06", "d05", "d04", "d03", "d02"], second.Select(hit => hit.RecordId));
        Assert.Equal(12, store.Search.CountMatches(query));
    }

    [Fact]
    public void EditingARecordUpdatesWhatSearchFinds()
    {
        using var store = Seed();

        store.Meetings.SetNote("m1", MeetingNoteKind.Generated, "## Summary\nThe account churned.");

        Assert.Empty(store.Search.Search(new SearchQuery { Text = "track", Kinds = SearchRecordKinds.Meeting }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "churned" }));
    }

    [Fact]
    public void RenamingAFolderRefreshesTheRecordsFiledInIt()
    {
        using var store = Seed();

        store.Folders.Upsert(new FolderRecord
        {
            Id = "clients",
            Name = "Accounts",
            Metadata = "external renewals",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor
        });

        Assert.Contains(
            store.Search.Search(new SearchQuery { Text = "accounts" }),
            hit => hit is { Kind: SearchRecordKind.Meeting, RecordId: "m1" });
        Assert.DoesNotContain(
            store.Search.Search(new SearchQuery { Text = "clients" }),
            hit => hit.Kind == SearchRecordKind.Meeting);
    }

    [Fact]
    public void RebuildRestoresTheIndexFromTheRelationalTables()
    {
        using var store = Seed();
        var expected = store.Search.Search(new SearchQuery { Text = "renewals" });

        store.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM search_index; DELETE FROM search_documents;";
            command.ExecuteNonQuery();
        });
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "renewals" }));

        store.Search.Rebuild();

        Assert.Equal(
            expected.Select(hit => (hit.Kind, hit.RecordId)).Order(),
            store.Search.Search(new SearchQuery { Text = "renewals" }).Select(hit => (hit.Kind, hit.RecordId)).Order());
    }

    [Fact]
    public void QuerySyntaxTypedByTheUserIsSearchedForLiterallyRatherThanExecuted()
    {
        using var store = Seed();

        // None of these may throw: FTS5 would reject most of them as query syntax.
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "\"" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "*" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "(unbalanced" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "   " }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "NEAR(a b" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "'; DROP TABLE meetings; --" }));

        // The database is still intact after all of that.
        Assert.Equal(1, store.Meetings.Count());
        Assert.Single(store.Search.Search(new SearchQuery { Text = "quarterly" }));
    }

    [Fact]
    public void AllTermsMustMatchAndOnlyTheLastOneIsTreatedAsAPrefix()
    {
        using var store = Seed();

        Assert.Single(store.Search.Search(new SearchQuery { Text = "quarterly review" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "quarterly absent" }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "quarter" }));
        Assert.Empty(store.Search.Search(new SearchQuery { Text = "quarter", PrefixMatchLastTerm = false }));
    }

    [Fact]
    public void SearchIgnoresAccentsAndCase()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        store.Meetings.Upsert(new MeetingRecord { Id = "m1", Title = "Réunion with Renée", CreatedAtUtc = Anchor });

        Assert.Single(store.Search.Search(new SearchQuery { Text = "reunion" }));
        Assert.Single(store.Search.Search(new SearchQuery { Text = "RENEE" }));
    }

    [Fact]
    public void CountMatchesReportsTheTotalBeyondTheRequestedPage()
    {
        using var store = MuesliPersistenceStore.OpenInMemory();
        for (var index = 0; index < 7; index++)
        {
            store.Dictations.Upsert(new DictationRecord
            {
                Id = $"d{index}",
                Text = "budget review",
                CreatedAtUtc = Anchor.AddMinutes(index)
            });
        }

        var query = new SearchQuery { Text = "budget", Limit = 3 };

        Assert.Equal(3, store.Search.Search(query).Count);
        Assert.Equal(7, store.Search.CountMatches(query));
    }

    private static MuesliPersistenceStore Seed()
    {
        var store = MuesliPersistenceStore.OpenInMemory();
        store.Folders.Upsert(new FolderRecord
        {
            Id = "clients",
            Name = "Clients",
            Metadata = "external renewals",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor
        });
        store.Meetings.Save(PersistenceRepositoryTests.Meeting());
        store.Dictations.Upsert(new DictationRecord
        {
            Id = "d1",
            Text = "Send the pricing deck to procurement.",
            CreatedAtUtc = Anchor,
            UpdatedAtUtc = Anchor,
            FolderId = "clients"
        });
        return store;
    }
}
