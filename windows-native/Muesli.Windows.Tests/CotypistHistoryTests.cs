using Muesli.Windows.Cotypist;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

// Ported from macOS CotypistHistoryTests (2f10b23d).
public sealed class CotypistHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);
    private readonly MuesliPersistenceStore _persistence = MuesliPersistenceStore.OpenInMemory();
    private readonly CotypistHistoryStore _store;

    public CotypistHistoryTests() => _store = new CotypistHistoryStore(_persistence.Database);

    public void Dispose() => _persistence.Dispose();

    private static CotypistSuggestionEvent Event(string text, CotypistOutcome outcome, double at, string accepted = "", string before = "", string app = "notes.exe") =>
        new(Epoch.AddSeconds(at), app, app == "notes.exe" ? "Notes" : "Codex", "boundary", text, accepted, outcome, -1.2, 80, 120, "field", "test", before);

    private List<(string Text, string Before, string App)> Rows() => _persistence.Database.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT suggestion_text, context_before, target_app_id || target_app_name FROM cotypist_suggestions ORDER BY id;";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    });

    [Fact]
    public void AcceptedSuggestionsInOneAppFormASessionAndGapsOrOtherAppsStartNewOnes()
    {
        Assert.Null(_store.Record(Event("maybe", CotypistOutcome.Ignored, 0)));
        var first = _store.Record(Event("the next word", CotypistOutcome.Accepted, 10, "the next word", "It can predict "))!.Value;
        Assert.Equal(first, _store.Record(Event("later", CotypistOutcome.Ignored, 20)));
        Assert.Equal(first, _store.Record(Event("of a word.", CotypistOutcome.Partial, 30, "of", "stuck in the middle ")));
        Assert.NotEqual(first, _store.Record(Event("tests", CotypistOutcome.Accepted, 40, "tests", app: "codex.exe")));
        Assert.NotEqual(first, _store.Record(Event("again", CotypistOutcome.Accepted, 30 + CotypistHistoryStore.SessionGap.TotalSeconds + 1, "again")));

        var session = _store.Session(first)!;
        Assert.Equal("Notes", session.TargetAppName);
        Assert.Equal([new CotypistAcceptance("It can predict ", "the next word"), new CotypistAcceptance("stuck in the middle ", "of")], session.Acceptances);
        Assert.Equal(4, session.WordsAccepted);
        Assert.Equal(3, session.SuggestionsShown);
        Assert.Equal(2, session.SuggestionsAccepted);
        Assert.Equal(3, _store.Sessions().Count);
    }

    [Fact]
    public void KeepsTheSentenceAndAppOnlyForTakenSuggestionsAndRemovesThemWithTheSession()
    {
        _store.Record(Event("stale", CotypistOutcome.Ignored, -CotypistHistoryStore.UnacceptedRetention.TotalSeconds - 60, before: "private draft "));
        var id = _store.Record(Event("the next word", CotypistOutcome.Accepted, 0, "the next word", "It can predict "))!.Value;
        _store.Record(Event("maybe", CotypistOutcome.Ignored, 5, before: "a sentence I typed "));
        var other = _store.Record(Event("elsewhere", CotypistOutcome.Accepted, 6, "elsewhere", "In Codex ", "codex.exe"))!.Value;

        Assert.Equal(["the next word", "maybe", "elsewhere"], Rows().Select(row => row.Text));
        Assert.Equal(["It can predict ", "", "In Codex "], Rows().Select(row => row.Before));
        Assert.Equal(["notes.exeNotes", "", "codex.exeCodex"], Rows().Select(row => row.App));

        _store.DeleteSession(id);
        Assert.Equal(["elsewhere"], Rows().Select(row => row.Text));
        Assert.Null(_store.Session(id));
        Assert.Equal(["elsewhere"], _store.Session(other)!.Acceptances.Select(acceptance => acceptance.Text));
    }

    [Fact]
    public void InsightsSumWordsAndRateAndSkipThePrunedSpan()
    {
        var now = Epoch.AddSeconds(100);
        Assert.Null(_store.InsightsTotals(now.AddDays(-30), now));
        _store.Record(Event("maybe", CotypistOutcome.Ignored, 0));
        _store.Record(Event("the next word", CotypistOutcome.Accepted, 10, "the next word"));
        _store.Record(Event("so it goes", CotypistOutcome.Partial, 20, "so"));
        _store.Record(Event("later", CotypistOutcome.Dismissed, 30));
        var totals = _store.InsightsTotals(now.AddDays(-30), now)!;
        Assert.Equal(4, totals.WordsAccepted);
        Assert.Equal(2, totals.SuggestionsAccepted);
        Assert.Equal(0.5, totals.AcceptanceRate);

        // Fill to the count cap with untaken suggestions newer than everything above.
        _persistence.Database.Write(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {CotypistHistoryStore.UnacceptedLimit})
                INSERT INTO cotypist_suggestions (created_at_utc, trigger, suggestion_text, outcome)
                SELECT {Epoch.AddSeconds(50).UtcTicks}, 'boundary', 'later', 'ignored' FROM n;
                """;
            command.ExecuteNonQuery();
        });
        _store.Record(Event("after", CotypistOutcome.Accepted, 60, "after"));
        var capped = _store.InsightsTotals(now.AddDays(-30), now)!;
        Assert.Equal(3, capped.SuggestionsAccepted);
        Assert.Equal(1.0 / (CotypistHistoryStore.UnacceptedLimit + 1), capped.AcceptanceRate);
    }

    [Fact]
    public void PrunesOldUntakenSuggestionsWithoutANewWrite()
    {
        _store.Record(Event("stale", CotypistOutcome.Ignored, 0));
        var session = _store.Record(Event("kept", CotypistOutcome.Accepted, 1, "kept"))!.Value;
        _store.Prune(Epoch + CotypistHistoryStore.UnacceptedRetention + TimeSpan.FromSeconds(60));
        Assert.Equal(1, _store.Session(session)!.SuggestionsShown);
    }
}

public sealed class CotypistSettingsTests
{
    [Fact]
    public void SettingsNormaliseToKnownValuesAndStayOffWithoutTheHelper()
    {
        Assert.Equal("phrase", CotypistOptions.Normalize(" Phrase ", ["word", "phrase", "sentence"], "sentence"));
        Assert.Equal("sentence", CotypistOptions.Normalize("paragraph", ["word", "phrase", "sentence"], "sentence"));
        Assert.Equal(CotypistFrequency.More, CotypistOptions.Parse<CotypistFrequency>("more"));
        Assert.Equal(CotypistWordPause.Relaxed, CotypistOptions.Parse<CotypistWordPause>("relaxed"));
        Assert.False(CotypistAvailability.IsHelperBundled);
    }
}

public sealed class CotypistProtocolTests
{
    [Fact]
    public void ANewerRequestSupersedesOlderOnesFromTheSameHelperOnly()
    {
        var ledger = new CotypistRequestLedger();
        var first = ledger.Note("notes", 4);
        var other = ledger.Note("codex", 1);
        Assert.True(first());
        var second = ledger.Note("notes", 5);
        Assert.False(first());
        Assert.True(second());
        Assert.True(other());
        Assert.True(ledger.Note(null, null)());
    }

    [Fact]
    public void WithManyHelpersTheLeastRecentlyActiveIsForgotten()
    {
        var ledger = new CotypistRequestLedger();
        var first = ledger.Note("notes", 4);
        for (var index = 0; index < 63; index++) ledger.Note($"app{index}", 1);
        ledger.Note("notes", 5);
        ledger.Note("one more", 1); // 65 helpers: app0 is forgotten, not notes
        Assert.False(first());
    }

    [Fact]
    public void KeepsTheSentenceBeingWrittenAndReadsOutcomes()
    {
        Assert.Equal("Let me know if you have any quest", CotypistMessages.Sentence("Hi team.\nThanks for the notes. Let me know if you have any quest"));
        Assert.Equal("The numbers look right. I will ", CotypistMessages.Sentence("The numbers look right. I will "));
        Assert.Equal("…words more words more words ", CotypistMessages.Sentence("word " + string.Concat(Enumerable.Repeat("more words ", 20)), 30));
        Assert.Equal("", CotypistMessages.Sentence(""));

        var message = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"outcome","app":"none.exe","trigger":"mid-word","text":"dle of a word.","accepted":"dle","outcome":"partial",
             "first_ms":90,"full_ms":140,"first_word_logprob":-0.4,"source":"field"}
            """)!.AsObject();
        var parsed = CotypistMessages.Event(message, "", "test", "", DateTimeOffset.UnixEpoch)!;
        Assert.Equal(CotypistOutcome.Partial, parsed.Outcome);
        Assert.Equal("dle", parsed.AcceptedText);
        Assert.Equal("mid-word", parsed.Trigger);
        Assert.Equal(90, parsed.FirstMs);
        Assert.Equal(-0.4, parsed.FirstWordLogProb);
        Assert.Null(CotypistMessages.Event(System.Text.Json.Nodes.JsonNode.Parse("""{"text":"x","outcome":"7"}""")!.AsObject(), "", "test", "", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void FindsAnAgentPromptBoxAndTheConversationAboveIt()
    {
        var rule = new string('─', 40);
        var screen = string.Join('\n', "⏺ Built the engine.", "", rule, "❯ check the CI and updated PR    ", "  reviews for the  ", rule, "  status line");
        Assert.Equal("⏺ Built the engine.\n\ncheck the CI and updated PR    reviews for the ", CotypistAgentPrompt.Context(screen));
        Assert.Null(CotypistAgentPrompt.Context("$ ls\n❯ not a box"));
    }
}
