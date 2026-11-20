using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingChatClientTests.swift (2f10b23d): the compatibility tool loop, response
// validation and the coordinator answer path. Native tool-calling tests arrive with that transport.
public sealed class MeetingChatClientTests : IDisposable
{
    private sealed class ScriptedReplies(params string[] replies) : IMeetingTextGenerator
    {
        private readonly Queue<string> _replies = new(replies);
        public List<MeetingTextGenerationRequest> Requests { get; } = [];
        public Func<Task>? BeforeReply { get; init; }
        public string Provider => "openai";
        public string Model => "test";

        public async Task<string> GenerateAsync(MeetingTextGenerationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (BeforeReply is not null) await BeforeReply();
            return _replies.Count > 0 ? _replies.Dequeue() : """{"status":"answered","markdown":"Friday [[S1]]"}""";
        }
    }

    private static readonly MeetingChatContextBudget Budget = new("openai", nativeTools: false);

    private static MeetingChatEvidence Evidence(string title = "Planning", bool withPassage = true, int eligible = 1)
    {
        var passage = new MeetingChatPassage
        {
            Id = "p1", SourceKey = "S1", MeetingId = "m1", Title = title, StartDate = DateTimeOffset.FromUnixTimeSeconds(1_000),
            Kind = MeetingChatSourceKind.Transcript, Excerpt = "[00:00:05] You: Ship Friday.", Range = new(0, 34), Timestamp = "00:00:05", Revision = "r1"
        };
        return new MeetingChatEvidence(MeetingChatScope.All, withPassage ? [passage] : [], withPassage ? [new("m1", "r1")] : [],
            new MeetingChatCoverage(eligible, withPassage ? 1 : 0, false), new MeetingChatRetrievalMetrics());
    }

    private static MeetingChatTurn PriorExchange(MeetingChatTurnState state = MeetingChatTurnState.Completed) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), Ordinal = 0, Question = "Summarize launch",
        Scope = new MeetingChatScope(MeetingChatSelection.Meetings(["m1"])), State = state,
        OriginalAnswer = "The launch is Friday [[S1]].", Provider = "test", Model = "test"
    };

    private static Task<(MeetingChatAnswer, MeetingChatEvidence)> Run(ScriptedReplies generator, MeetingChatEvidence evidence,
        IReadOnlyList<MeetingChatTurn>? history = null, Func<MeetingChatToolCall, MeetingChatEvidence, MeetingChatToolResult>? execute = null,
        List<string>? progress = null) =>
        MeetingChatClient.RunAsync("When?", evidence, history ?? [], generator,
            (call, current, _) => Task.FromResult(execute?.Invoke(call, current) ?? throw new InvalidOperationException("Unexpected tool execution")),
            null, progress is null ? null : progress.Add, null, Budget, CancellationToken.None);

    [Fact]
    public void RollingMemoryIsPortableContextAndLongRecentAnswersAreNotClipped()
    {
        var turn = PriorExchange() with { OriginalAnswer = string.Concat(Enumerable.Repeat("A useful detail. ", 330)) + "KEEP THE LAST DETAIL" };
        var prompt = MeetingChatClient.MakePrompt("What remains?", Evidence(), [turn], "Earlier we agreed to compare options, not commit to a deadline.",
            new MeetingChatContextBudget("chatgpt", nativeTools: false));
        Assert.Contains("KEEP THE LAST DETAIL", prompt.User);
        Assert.Contains("Earlier we agreed to compare options", prompt.User);
        var response = MeetingChatClient.ValidateResponse("""{"status":"conversation","markdown":"The comparison remains open."}""",
            Evidence(), [], rollingSummary: "Earlier we agreed to compare options.");
        Assert.Equal("The comparison remains open.", response.Markdown);
    }

    [Fact]
    public void ConversationalRepliesNeedUsableHistoryButNoNewCitations()
    {
        var empty = Evidence(withPassage: false);
        const string raw = """{"status":"conversation","markdown":"Suggestion: confirm the launch checklist."}""";
        var answer = MeetingChatClient.ValidateResponse(raw, empty, [PriorExchange()]);
        Assert.False(answer.InsufficientEvidence);
        Assert.Empty(answer.Citations);
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse(raw, empty));
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse(raw, empty, [PriorExchange(MeetingChatTurnState.SourceDeleted)]));
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse("""{"status":"answered","markdown":"Friday [[S1]]"}""", empty, [PriorExchange()]));
    }

    [Fact]
    public async Task HistoryOnlyTransformationDoesNotRunRetrieval()
    {
        var generator = new ScriptedReplies("""{"status":"conversation","markdown":"Suggestion: review the launch checklist."}""");
        var (answer, _) = await Run(generator, Evidence(withPassage: false, eligible: 0), [PriorExchange()]);
        Assert.False(answer.InsufficientEvidence);
        var request = Assert.Single(generator.Requests);
        Assert.Contains("The launch is Friday", request.User);
        Assert.DoesNotContain("[[S1]]", request.User);
        Assert.Contains("Retrieval tools are unavailable", request.System);
    }

    [Fact]
    public async Task NewMeetingFactsCanRetrieveLazilyFromSafeConversation()
    {
        var generator = new ScriptedReplies("""{"tool":"search_meeting_sources","query":"launch owner"}""", """{"status":"answered","markdown":"Friday [[S1]]"}""");
        var calls = new List<MeetingChatToolCall>();
        var (answer, _) = await Run(generator, Evidence(withPassage: false), [PriorExchange()], (call, _) => { calls.Add(call); return new(Evidence()); });
        Assert.Equal([new MeetingChatToolCall(MeetingChatToolKind.Search, "launch owner")], calls);
        Assert.Equal(["S1"], answer.Citations.Select(c => c.SourceKey));
    }

    [Fact]
    public async Task RemovedScopeCannotRetrieveNewFactsThroughConversationMode()
    {
        var generator = new ScriptedReplies("""{"tool":"search_meeting_sources","query":"launch owner"}""",
            """{"status":"insufficient_evidence","markdown":"Attach a meeting to check who was assigned."}""");
        var (answer, _) = await Run(generator, Evidence(withPassage: false, eligible: 0), [PriorExchange()]);
        Assert.True(answer.InsufficientEvidence);
        Assert.Equal(2, generator.Requests.Count);
    }

    [Fact]
    public void ValidatesRetrievalToolArguments()
    {
        Assert.Equal(new MeetingChatToolCall(MeetingChatToolKind.Search, "release date"),
            MeetingChatClient.ParseToolCall("""{"tool":"search_meeting_sources","query":" release date "}"""));
        Assert.Equal(new MeetingChatToolCall(MeetingChatToolKind.Read, "S2"), MeetingChatClient.ParseToolCall("""{"tool":"read_meeting_source","source":"S2"}"""));
        Assert.Equal(new MeetingChatToolCall(MeetingChatToolKind.Browse), MeetingChatClient.ParseToolCall("""{"tool":"browse_meeting_sources"}"""));
        foreach (var invalid in new[] { """{"tool":"shell","query":"ls"}""", """{"tool":"search_meeting_sources","query":"","scope":"all"}""", """{"tool":"read_meeting_source","source":"../secret"}""" })
            Assert.Throws<MeetingChatException>(() => MeetingChatClient.ParseToolCall(invalid));
    }

    [Fact]
    public async Task ToolLoopFeedsResultsBack()
    {
        var generator = new ScriptedReplies("""{"tool":"search_meeting_sources","query":"launch"}""", """{"status":"answered","markdown":"Ship Friday [[S1]]"}""");
        var (answer, _) = await Run(generator, Evidence(withPassage: false), execute: (_, _) => new(Evidence()));
        Assert.Single(answer.Citations);
        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains("Ship Friday", generator.Requests[1].User);
        Assert.Contains("QUOTED TOOL HISTORY", generator.Requests[1].User);
        Assert.Contains("For a final answer, return exactly two keys", generator.Requests[0].System);
        Assert.Contains("The final-answer schema does not apply to tool requests", generator.Requests[0].System);
    }

    [Fact]
    public async Task ToolLoopCannotExceedBudget()
    {
        var generator = new ScriptedReplies(Enumerable.Range(1, 5).Select(i => $$"""{"tool":"search_meeting_sources","query":"query {{i}}"}""").ToArray());
        var calls = 0;
        await Assert.ThrowsAsync<MeetingChatHarnessException>(() => Run(generator, Evidence(), execute: (_, current) => { calls++; return new(current); }));
        Assert.Equal(3, calls);
        Assert.Equal(5, generator.Requests.Count);
    }

    [Fact]
    public async Task ExhaustedToolBudgetGetsOneFinalizationPass()
    {
        var generator = new ScriptedReplies(Enumerable.Range(1, 4).Select(i => $$"""{"tool":"search_meeting_sources","query":"query {{i}}"}""")
            .Append("""{"status":"answered","markdown":"Friday [[S1]]"}""").ToArray());
        var executions = 0;
        var (answer, _) = await Run(generator, Evidence(), execute: (_, current) => { executions++; return new(current); });
        Assert.Equal(3, executions);
        Assert.Equal(5, generator.Requests.Count);
        Assert.Single(answer.Citations);
    }

    [Fact]
    public async Task EmptyScopeDoesNotContactProvider()
    {
        var generator = new ScriptedReplies();
        var (answer, _) = await Run(generator, Evidence(withPassage: false, eligible: 0));
        Assert.True(answer.InsufficientEvidence);
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task RepeatedCallFinalizesWithoutExecutingAgain()
    {
        const string repeated = """{"tool":"search_meeting_sources","query":"launch"}""";
        var generator = new ScriptedReplies(repeated, repeated, """{"status":"answered","markdown":"Friday [[S1]]"}""");
        var executions = 0;
        var (answer, _) = await Run(generator, Evidence(), execute: (_, current) => { executions++; return new(current); });
        Assert.Equal(1, executions);
        Assert.Single(answer.Citations);
        Assert.Contains("Retrieval tools are unavailable", generator.Requests[^1].System);
    }

    [Theory]
    [InlineData("not JSON", "Formatting answer")]
    [InlineData("""{"status":"answered","markdown":"Friday"}""", "Checking source references")]
    [InlineData("""{"status":"answered","markdown":"Friday [[S999]]"}""", "Checking source references")]
    [InlineData("""{"status":"conversation","markdown":"As discussed earlier"}""", "Checking conversation context")]
    [InlineData("""{"tool":"unavailable_tool"}""", "Preparing final answer")]
    [InlineData("oversized", "Shortening answer")]
    public async Task RepairProgressDescribesTheActualValidationIssue(string raw, string expected)
    {
        if (raw == "oversized") raw = new string('x', 64_001);
        var generator = new ScriptedReplies(raw, """{"status":"answered","markdown":"Friday [[S1]]"}""");
        var progress = new List<string>();
        var (answer, _) = await Run(generator, Evidence(), progress: progress);
        Assert.Single(answer.Citations);
        Assert.Equal("Preparing answer", progress[0]);
        // Successful final validation adds the last source-check event; the preceding event describes the repair.
        Assert.Equal(expected, progress[^2]);
        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains("Do not request a tool", generator.Requests[^1].System);
    }

    [Fact]
    public async Task RepeatedInvalidCitationsHavePrecisePrivateDiagnostic()
    {
        const string raw = """{"status":"answered","markdown":"PRIVATE_MODEL_OUTPUT [[S999]]"}""";
        var generator = new ScriptedReplies(raw, raw);
        var error = await Assert.ThrowsAsync<MeetingChatHarnessException>(() => Run(generator, Evidence()));
        Assert.Contains("outside the retrieved context", error.Message);
        Assert.DoesNotContain("PRIVATE_MODEL_OUTPUT", error.Message);
        Assert.DoesNotContain("PRIVATE_MODEL_OUTPUT", generator.Requests[^1].User);
    }

    [Fact]
    public void ResolvesOnlySuppliedSourceMarkersAndStripsModelLinks()
    {
        var answer = MeetingChatClient.ValidateResponse("""{"status":"answered","markdown":"We ship Friday. [[S1]]"}""", Evidence());
        Assert.Equal("[00:00:05] You: Ship Friday.", answer.Citations.Single().Excerpt);
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse("""{"status":"answered","markdown":"Friday [[S999]]"}""", Evidence()));
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse("""{"status":"answered","markdown":"Friday"}""", Evidence()));
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.ValidateResponse("not JSON", Evidence()));
        Assert.True(MeetingChatClient.ValidateResponse("""{"status":"insufficient_evidence","markdown":"The saved meetings do not say."}""", Evidence()).InsufficientEvidence);
        var linked = MeetingChatClient.ValidateResponse("""{"status":"answered","markdown":"Friday [open](file:///private/secret) [[S1]]"}""", Evidence());
        Assert.Equal("m1", linked.Citations.Single().MeetingId);
        Assert.DoesNotContain("file:///", MeetingChatClient.DisplayText(linked.Markdown));
    }

    [Fact]
    public void PromptBudgetsAndUntrustedContext()
    {
        var prompt = MeetingChatClient.MakePrompt(new string('a', 2_000), Evidence(), [], null, Budget);
        Assert.Contains("untrusted", prompt.System);
        Assert.Throws<MeetingChatException>(() => MeetingChatClient.MakePrompt(new string('a', 2_001), Evidence(), [], null, Budget));
        var sunflowers = string.Concat(Enumerable.Repeat("🌻", 10_000));
        var fitted = MeetingChatClient.MakePrompt("When?", Evidence(sunflowers), [], null, Budget);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(fitted.System + fitted.User) <= 24_000);
    }

    [Fact]
    public void ConversationTitlesAreBoundedPlainMetadata()
    {
        Assert.Equal("Launch timing", MeetingChatClient.ValidatedConversationTitle("""{"title":" Launch timing "}"""));
        Assert.Null(MeetingChatClient.ValidatedConversationTitle("""{"title":"See [[S1]]"}"""));
        Assert.Null(MeetingChatClient.ValidatedConversationTitle("""{"title":"<b>x</b>"}"""));
        Assert.Null(MeetingChatClient.ValidatedConversationTitle("""{"title":"x","extra":1}"""));
        Assert.Null(MeetingChatClient.ValidatedConversationTitle($$"""{"title":"{{new string('a', 81)}}"}"""));
    }

    // Coordinator answer path over the real store.

    private readonly MuesliPersistenceStore _persistence = MuesliPersistenceStore.OpenInMemory();

    public void Dispose() => _persistence.Dispose();

    private string Meeting(string title, string transcript)
    {
        var id = Guid.NewGuid().ToString();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        _persistence.Meetings.Upsert(new MeetingRecord { Id = id, Title = title, CreatedAtUtc = at, UpdatedAtUtc = at });
        _persistence.Meetings.SetTranscript(id, MeetingTranscriptKind.Raw, transcript);
        return id;
    }

    private (MeetingChatEngine Engine, MeetingChatTurn Turn) Begin(MeetingChatScope scope, string question, Guid? sessionId = null)
    {
        var engine = new MeetingChatEngine(new MeetingChatStore(_persistence.Database));
        var id = sessionId ?? engine.Store.CreateSession(scope, "New chat").Id;
        return (engine, engine.Store.BeginTurn(id, question, scope, "openai", "test", Guid.NewGuid()));
    }

    [Fact]
    public async Task SourceReadUsesAdjacentPassagesAndPreservesScope()
    {
        var id = Meeting("Launch", "Launch Friday\nActually Monday is agreed.");
        var generator = new ScriptedReplies("""{"tool":"read_meeting_source","source":"S1"}""", """{"status":"answered","markdown":"Launch Monday [[S2]]"}""");
        var (engine, turn) = Begin(new MeetingChatScope(MeetingChatSelection.Meetings([id])), "Friday");
        await engine.AnswerAsync(turn, generator, isDraft: false);
        Assert.Equal(2, generator.Requests.Count);
        Assert.Contains("Actually Monday is agreed", generator.Requests[^1].User);
        var stored = engine.Store.Turns(turn.SessionId).Single();
        Assert.Equal(MeetingChatTurnState.Completed, stored.State);
        Assert.Equal(id, stored.Citations.Single().MeetingId);
    }

    [Fact]
    public async Task EmptySearchPreservesEvidenceAndReportsZeroMatches()
    {
        var id = Meeting("Launch", "Launch Friday");
        var generator = new ScriptedReplies("""{"tool":"search_meeting_sources","query":"zznomatch"}""",
            """{"tool":"search_meeting_sources","query":"launch"}""", """{"status":"answered","markdown":"Friday [[S1]]"}""");
        var (engine, turn) = Begin(new MeetingChatScope(MeetingChatSelection.Meetings([id])), "launch");
        await engine.AnswerAsync(turn, generator, isDraft: false);
        Assert.Equal(3, generator.Requests.Count);
        Assert.Contains("Tool returned 0 matching passages", generator.Requests[1].User);
        Assert.Contains("Launch Friday", generator.Requests[1].User);
        Assert.Contains("\"id\":\"S1\"", generator.Requests[2].User);
        Assert.Equal(MeetingChatTurnState.Completed, engine.Store.Turns(turn.SessionId).Single().State);
    }

    [Fact]
    public async Task SourceDeleteDuringAwaitCannotPersist()
    {
        var id = Meeting("Launch", "Launch Friday");
        var generator = new ScriptedReplies { BeforeReply = () => { _persistence.Meetings.Delete(id); return Task.CompletedTask; } };
        var (engine, turn) = Begin(MeetingChatScope.All, "launch");
        await Assert.ThrowsAsync<MeetingChatException>(() => engine.AnswerAsync(turn, generator, isDraft: false));
        var stored = engine.Store.Turns(turn.SessionId).Single();
        Assert.Equal(MeetingChatTurnState.SourceDeleted, stored.State);
        Assert.Null(stored.OriginalAnswer);
    }

    [Fact]
    public async Task EditedSourceDoesNotBlockFreshFollowUpAndTitleIsGenerated()
    {
        var id = Meeting("Launch", "Launch Friday");
        var generator = new ScriptedReplies("""{"status":"answered","markdown":"Friday [[S1]]"}""", """{"title":"Launch timing"}""");
        var (engine, first) = Begin(MeetingChatScope.All, "launch");
        await engine.AnswerAsync(first, generator, isDraft: false);
        Assert.True(await engine.GenerateTitleAsync(engine.Store.Turns(first.SessionId).Single(), generator));
        Assert.Equal("Launch timing", engine.Store.Sessions().Single().Title);

        _persistence.Meetings.SetTranscript(id, MeetingTranscriptKind.Raw, "Launch Monday");
        var (_, next) = Begin(MeetingChatScope.All, "launch now?", first.SessionId);
        await engine.AnswerAsync(next, generator, isDraft: false);
        Assert.Equal(MeetingChatTurnState.Completed, engine.Store.Turns(first.SessionId)[^1].State);
    }

    [Fact]
    public void ProviderFailuresBecomeSafeMessages()
    {
        Assert.Equal("Configure your meeting AI connection and model in Settings.",
            MeetingChatEngine.FailureMessage(new SummaryProviderException("missing-connection")));
        Assert.Contains("HTTP 429", MeetingChatEngine.FailureMessage(new SummaryProviderException("http-429")));
    }
}
