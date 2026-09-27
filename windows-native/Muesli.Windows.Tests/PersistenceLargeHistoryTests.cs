using System.Diagnostics;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

/// <summary>
/// A history the size of a heavy user's: ten thousand dictations and two thousand meetings, written
/// as JSON and migrated once, then shared by every test in the class. It is generated from a fixed
/// seed so a failure here is reproducible rather than a story about one machine's data.
/// </summary>
public sealed class LargeHistoryFixture : IDisposable
{
    public const int DictationCount = 10_000;
    public const int MeetingCount = 2_000;
    public const int FolderCount = 25;
    public const int TemplateCount = 10;
    private const int Seed = 20260817;

    private static readonly string[] Vocabulary =
    [
        "renewal", "budget", "pricing", "roadmap", "latency", "migration", "storage", "invoice",
        "onboarding", "diarization", "throughput", "quarterly", "escalation", "provisioning",
        "handover", "retention", "forecast", "capacity", "incident", "compliance"
    ];

    private readonly TestDirectory _directory = new();

    public LargeHistoryFixture()
    {
        var random = new Random(Seed);
        var start = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var appData = new AppDataStore(_directory.Path);

        appData.SaveMeetingFolders(Enumerable.Range(0, FolderCount)
            .Select(index => new PersistedMeetingFolder($"folder-{index}", $"Folder {index}"))
            .ToList());
        appData.SaveMeetingTemplates(Enumerable.Range(0, TemplateCount)
            .Select(index => new PersistedMeetingTemplate
            {
                Id = $"tmpl-{index}",
                Name = $"Template {index}",
                Prompt = Sentence(random, 12)
            })
            .ToList());
        appData.SaveDictations(Enumerable.Range(0, DictationCount)
            .Select(index => new PersistedDictation(
                $"dictation-{index:D6}",
                start.AddMinutes(index),
                $"{Sentence(random, 40)} marker{index:D6}",
                random.Next(1_000, 90_000),
                "parakeet"))
            .ToList());
        appData.SaveMeetings(Enumerable.Range(0, MeetingCount)
            .Select(index => new PersistedMeeting
            {
                SchemaVersion = 5,
                Id = $"meeting-{index:D5}",
                Title = $"Meeting {index} {Vocabulary[index % Vocabulary.Length]}",
                CreatedAt = start.AddHours(index),
                DurationMs = random.Next(600_000, 5_400_000),
                Transcript = $"{Sentence(random, 250)} tag{index:D5}",
                Summary = Sentence(random, 40),
                ManualNotes = index % 7 == 0 ? Sentence(random, 15) : "",
                SourcePath = $@"C:\captures\meeting-{index:D5}-microphone.wav",
                MicrophoneAudioPath = $@"C:\captures\meeting-{index:D5}-microphone.wav",
                SystemCaptureMode = "process-loopback",
                ModelProfile = "whisper-large",
                FolderId = $"folder-{index % FolderCount}",
                WordCount = 250,
                TemplateName = $"Template {index % TemplateCount}",
                SpeakerAliases = new Dictionary<string, string>
                {
                    ["Speaker 1"] = $"Person {index % 97}",
                    ["Speaker 2"] = $"Person {(index + 13) % 97}"
                },
                FinalTranscriptOwnerModelId = "whisper-large"
            })
            .ToList());

        var service = new JsonToSqliteMigrationService(_directory.Path);
        var migrationTimer = Stopwatch.StartNew();
        Result = service.Migrate();
        MigrationDuration = migrationTimer.Elapsed;
        Store = MuesliPersistenceStore.Open(service.DatabasePath);
        DatabasePath = service.DatabasePath;
    }

    public JsonToSqliteMigrationResult Result { get; }

    public TimeSpan MigrationDuration { get; }

    public MuesliPersistenceStore Store { get; }

    public string DatabasePath { get; }

    public void Dispose()
    {
        Store.Dispose();
        _directory.Dispose();
    }

    private static string Sentence(Random random, int length) =>
        string.Join(' ', Enumerable.Range(0, length).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]));
}

public sealed class PersistenceLargeHistoryTests : IClassFixture<LargeHistoryFixture>
{
    /// <summary>
    /// The budget a search has to stay inside on the development baseline. Each measurement is the
    /// best of several runs: the point is the query plan, not whatever else the machine was doing.
    /// </summary>
    private static readonly TimeSpan SearchBudget = TimeSpan.FromMilliseconds(300);

    private readonly LargeHistoryFixture _fixture;

    public PersistenceLargeHistoryTests(LargeHistoryFixture fixture) => _fixture = fixture;

    [Fact]
    public void TheWholeLargeHistoryIsImportedAndVerified()
    {
        Assert.Equal(JsonMigrationOutcome.Migrated, _fixture.Result.Outcome);
        Assert.Null(_fixture.Result.Failure);
        Assert.Equal(
            new MigrationCounts(
                LargeHistoryFixture.DictationCount,
                LargeHistoryFixture.MeetingCount,
                LargeHistoryFixture.FolderCount,
                LargeHistoryFixture.TemplateCount),
            _fixture.Result.Counts);

        Assert.Equal(LargeHistoryFixture.DictationCount, _fixture.Store.Dictations.Count());
        Assert.Equal(LargeHistoryFixture.MeetingCount, _fixture.Store.Meetings.Count());
    }

    [Theory]
    [InlineData("common term", "renewal", SearchSort.Relevance)]
    [InlineData("common term by date", "renewal", SearchSort.NewestFirst)]
    [InlineData("two terms", "storage invoice", SearchSort.Relevance)]
    [InlineData("two terms by date", "storage invoice", SearchSort.NewestFirst)]
    [InlineData("prefix", "diariz", SearchSort.Relevance)]
    [InlineData("unique dictation marker", "marker004242", SearchSort.Relevance)]
    [InlineData("unique meeting tag", "tag01234", SearchSort.Relevance)]
    [InlineData("no match", "unfindableterm", SearchSort.Relevance)]
    public void SearchOverTheLargeHistoryStaysInsideTheBudget(string label, string text, SearchSort sort)
    {
        var query = new SearchQuery { Text = text, Sort = sort };

        var elapsed = Fastest(() => _fixture.Store.Search.Search(query));

        Assert.True(
            elapsed < SearchBudget,
            $"'{label}' took {elapsed.TotalMilliseconds:F0} ms, over the {SearchBudget.TotalMilliseconds:F0} ms budget.");
    }

    [Fact]
    public void FieldScopedAndFolderScopedSearchesStayInsideTheBudget()
    {
        var aliasQuery = new SearchQuery { Text = "Person 42", Fields = SearchFields.SpeakerAliases };
        var folderQuery = new SearchQuery { Text = "renewal", FolderId = "folder-7", Sort = SearchSort.NewestFirst };
        var titleQuery = new SearchQuery { Text = "meeting", Fields = SearchFields.Title };

        Assert.True(Fastest(() => _fixture.Store.Search.Search(aliasQuery)) < SearchBudget);
        Assert.True(Fastest(() => _fixture.Store.Search.Search(folderQuery)) < SearchBudget);
        Assert.True(Fastest(() => _fixture.Store.Search.Search(titleQuery)) < SearchBudget);
        Assert.True(Fastest(() => _fixture.Store.Search.CountMatches(titleQuery)) < SearchBudget);
    }

    [Fact]
    public void SearchFindsTheOneRecordThatCarriesAUniqueTerm()
    {
        var dictationHit = Assert.Single(_fixture.Store.Search.Search(new SearchQuery { Text = "marker004242" }));
        Assert.Equal("dictation-004242", dictationHit.RecordId);

        var meetingHit = Assert.Single(_fixture.Store.Search.Search(new SearchQuery { Text = "tag01234" }));
        Assert.Equal("meeting-01234", meetingHit.RecordId);
    }

    [Fact]
    public void DateOrderedSearchReturnsTheNewestMatchesFirst()
    {
        var hits = _fixture.Store.Search.Search(new SearchQuery
        {
            Text = "renewal",
            Sort = SearchSort.NewestFirst,
            Limit = 25
        });

        Assert.Equal(25, hits.Count);
        Assert.Equal(
            hits.Select(hit => hit.CreatedAtUtc).OrderByDescending(value => value),
            hits.Select(hit => hit.CreatedAtUtc));
    }

    [Fact]
    public void ReadingAPageOfHistoryDoesNotDependOnItsSize()
    {
        var elapsed = Fastest(() => _fixture.Store.Dictations.List(new DictationQuery { Limit = 100 }));

        Assert.Equal(100, _fixture.Store.Dictations.List(new DictationQuery { Limit = 100 }).Count);
        Assert.True(
            elapsed < SearchBudget,
            $"Reading one page took {elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void RunningTheMigrationAgainOverTheSameHistoryIsANoOp()
    {
        var service = new JsonToSqliteMigrationService(
            Path.GetDirectoryName(_fixture.DatabasePath)!,
            _fixture.DatabasePath);

        var result = service.Migrate();

        Assert.Equal(JsonMigrationOutcome.AlreadyCurrent, result.Outcome);
        Assert.Equal(_fixture.Result.SourceFingerprint, result.SourceFingerprint);
    }

    private static TimeSpan Fastest<T>(Func<T> work)
    {
        // The first call warms the page cache and prepares the statement; neither is what the
        // budget is about.
        _ = work();
        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var timer = Stopwatch.StartNew();
            _ = work();
            timer.Stop();
            if (timer.Elapsed < best)
            {
                best = timer.Elapsed;
            }
        }

        return best;
    }
}
