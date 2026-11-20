using System.Text;
using System.Text.Json.Nodes;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingChatClientTests (native cases), MeetingChatNativeBudgetTests and the native
// accumulator cases of ChatGPTCodexTransportTests (2f10b23d). Meeting IDs are GUID strings on Windows.
public sealed class MeetingChatNativeHarnessTests
{
    private const string MeetingId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private sealed class NativeProbe(Func<NativeProbe, MeetingNativeToolRequest, MeetingNativeToolResponse> reply) : IMeetingTextGenerator
    {
        public List<MeetingNativeToolRequest> Requests { get; } = [];
        public string Provider => "chatgpt";
        public string Model => "test";
        public bool SupportsNativeTools => true;
        public Task<string> GenerateAsync(MeetingTextGenerationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Native-capable provider must not use JSON-in-text tool requests");
        public Task<MeetingNativeToolResponse> GenerateNativeAsync(MeetingNativeToolRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(reply(this, request));
        }
    }

    private static MeetingNativeToolResponse Text(string text) => new([], [], text);

    private static MeetingNativeToolResponse ReadCall(string id, int page, JsonObject? before = null)
    {
        var arguments = $$"""{"meeting_id":"{{MeetingId}}","page":{{page}}}""";
        var output = new JsonArray();
        if (before is not null) output.Add(before);
        output.Add(new JsonObject { ["type"] = "function_call", ["name"] = "read_full_meeting", ["call_id"] = id, ["arguments"] = arguments });
        return new MeetingNativeToolResponse(output, [new MeetingNativeToolCall(id, "read_full_meeting", arguments)], "");
    }

    private static MeetingChatEvidence Evidence(string excerpt = "Ship Friday.")
    {
        var passage = new MeetingChatPassage
        {
            Id = "p1", SourceKey = "S1", MeetingId = MeetingId, Title = "Planning", StartDate = DateTimeOffset.FromUnixTimeSeconds(1_000),
            Kind = MeetingChatSourceKind.Transcript, Excerpt = excerpt, Range = new(0, excerpt.Length), Revision = "r1"
        };
        return new MeetingChatEvidence(MeetingChatScope.All, [passage], [new(MeetingId, "r1")], new MeetingChatCoverage(1, 1, true), new MeetingChatRetrievalMetrics());
    }

    private static readonly MeetingChatContextBudget Budget = new("chatgpt", nativeTools: true);

    private static Task<(MeetingChatAnswer, MeetingChatEvidence)> Run(NativeProbe probe,
        Func<MeetingChatToolCall, MeetingChatEvidence, MeetingChatToolResult> execute, List<string>? progress = null) =>
        MeetingChatNativeHarness.RunAsync("Read the entire meeting", Evidence(), [], probe,
            (call, current, _) => Task.FromResult(execute(call, current)), null, progress is null ? null : progress.Add, null, Budget, CancellationToken.None);

    [Fact]
    public async Task NativeCallsUseRegisteredToolsAndReplayOutputsWithReasoning()
    {
        var probe = new NativeProbe((self, _) => self.Requests.Count == 1
            ? ReadCall("native-call-1", 0, new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = "opaque-test-state" })
            : Text("""{"status":"answered","markdown":"Full meeting detail [[S2]]"}"""));
        var executed = new List<MeetingChatToolCall>();
        var (answer, final) = await Run(probe, (call, current) =>
        {
            executed.Add(call);
            var extra = current.Passages[0] with { Id = "p2", SourceKey = "S2", Excerpt = "Full meeting detail." };
            return new MeetingChatToolResult(current with { Passages = [.. current.Passages, extra] }, 1);
        });
        Assert.Equal([new MeetingChatToolCall(MeetingChatToolKind.ReadMeeting, MeetingId, 0)], executed);
        Assert.Equal(["S2"], answer.Citations.Select(c => c.SourceKey));
        Assert.Equal(2, final.Passages.Count);
        Assert.False(answer.Coverage.IsPartialRecap);
        Assert.Equal(2, probe.Requests.Count);
        Assert.Equal(["browse_meeting_sources", "read_full_meeting", "read_meeting_source", "search_meeting_sources"],
            probe.Requests[0].Tools.Select(tool => MeetingNativeToolTransport.Str(tool!["name"])).Order());
        var replay = probe.Requests[1].InputItems.OfType<JsonObject>().ToList();
        Assert.Contains(replay, item => MeetingNativeToolTransport.Str(item["type"]) == "reasoning" && MeetingNativeToolTransport.Str(item["encrypted_content"]) == "opaque-test-state");
        var result = replay.First(item => MeetingNativeToolTransport.Str(item["type"]) == "function_call_output");
        Assert.Equal("native-call-1", MeetingNativeToolTransport.Str(result["call_id"]));
        Assert.Contains("Full meeting detail.", MeetingNativeToolTransport.Str(result["output"]));
        Assert.DoesNotContain("returning exactly {\"tool\"", probe.Requests[0].Base.System);
    }

    [Fact]
    public void NativeFullReadRejectsForgedArguments()
    {
        Assert.Equal(new MeetingChatToolCall(MeetingChatToolKind.ReadMeeting, MeetingId, 0),
            MeetingChatNativeHarness.Call("read_full_meeting", $$"""{"meeting_id":"{{MeetingId}}","page":0}"""));
        foreach (var bad in new[] { """{"meeting_id":true,"page":0}""", $$"""{"meeting_id":"{{MeetingId}}","page":-1}""",
                     $$"""{"meeting_id":"{{MeetingId}}","page":10000}""", $$"""{"meeting_id":"{{MeetingId}}","page":"0"}""",
                     $$"""{"meeting_id":"{{MeetingId}}","page":0,"scope":"all"}""" })
            Assert.Throws<MeetingChatHarnessException>(() => MeetingChatNativeHarness.Call("read_full_meeting", bad));
        Assert.Throws<MeetingChatHarnessException>(() => MeetingChatNativeHarness.Call("search_meeting_sources", """{"query":"x","tool":"shell"}"""));
    }

    [Theory]
    [InlineData("chatgpt", "", true)]
    [InlineData("openai", "", true)]
    [InlineData("ollama", "", false)]
    [InlineData("custom", "", false)]
    [InlineData("openrouter", "openai/gpt-5.4", true)]
    [InlineData("openrouter", "openai/o3-mini", true)]
    [InlineData("openrouter", "openai/o4-mini:free", true)]
    [InlineData("openrouter", "openai/o10", false)]
    [InlineData("openrouter", "anthropic/claude-sonnet-4", false)]
    [InlineData("openrouter", "custom/gpt-5", false)]
    public void NativeCapabilityFollowsProviderAndOpenRouterFamily(string provider, string model, bool expected) =>
        Assert.Equal(expected, new MeetingSummaryTextGenerator(new MuesliSettings { MeetingSummaryProvider = provider, OpenRouterModel = model }).SupportsNativeTools);

    [Fact]
    public async Task MultipleLargePagesFinalizeFromDeliveredEvidence()
    {
        var probe = new NativeProbe((self, request) => request.AllowTools
            ? ReadCall($"call-{self.Requests.Count}", self.Requests.Count - 1)
            : Text("""{"status":"answered","markdown":"Available detail [[S2]]. Additional pages remain unread."}"""));
        var executed = 0;
        var (answer, evidence) = await Run(probe, (_, current) =>
        {
            executed++;
            var excerpt = string.Concat(Enumerable.Repeat("Page detail. ", 3_200));
            var page = current.Passages[0] with { Id = $"page{executed}", SourceKey = $"S{executed + 1}", Excerpt = excerpt, Range = new(0, excerpt.Length) };
            return new MeetingChatToolResult(current with
            {
                Passages = [.. current.Passages, page], Dependencies = [.. current.Dependencies, new($"dep-{executed}", "r1")]
            }, 1, executed);
        });
        Assert.True(executed >= 2);
        Assert.True(answer.Coverage.IsPartialRecap);
        Assert.Equal(["S2"], answer.Citations.Select(c => c.SourceKey));
        Assert.DoesNotContain(evidence.Passages, p => p.SourceKey == $"S{executed + 1}");
        Assert.Contains(evidence.Dependencies, d => d.MeetingId == $"dep-{executed}");
        Assert.False(probe.Requests[^1].AllowTools);
        Assert.Contains("budget_exhausted", probe.Requests[^1].InputItems.ToJsonString());
        foreach (var request in probe.Requests)
            Assert.True(MeetingChatContextBudget.EstimateTokens(request.Base.System + request.InputItems.ToJsonString() + request.Tools.ToJsonString()) <= Budget.MaximumInputTokens);
    }

    [Fact]
    public async Task OversizedReplayFallsBackWithoutExecutingOrDanglingCalls()
    {
        var probe = new NativeProbe((self, request) => request.AllowTools
            ? ReadCall($"call-{self.Requests.Count}", 0, new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = new string('x', 400_000) })
            : Text("""{"status":"answered","markdown":"Ship Friday [[S1]]. Additional pages remain unread."}"""));
        var (answer, _) = await Run(probe, (_, _) => throw new InvalidOperationException("Oversized replay must not execute tools"));
        Assert.Equal(["S1"], answer.Citations.Select(c => c.SourceKey));
        Assert.True(answer.Coverage.IsPartialRecap);
        Assert.Equal(2, probe.Requests.Count);
        Assert.False(probe.Requests[1].AllowTools);
        Assert.DoesNotContain(probe.Requests[1].InputItems.OfType<JsonObject>(), item => MeetingNativeToolTransport.Str(item["type"]) == "function_call");
    }

    [Fact]
    public async Task RepairProgressSurvivesNextInference()
    {
        var probe = new NativeProbe((self, _) => Text(self.Requests.Count == 1
            ? """{"status":"answered","markdown":"Uncited answer"}""" : """{"status":"answered","markdown":"Ship Friday [[S1]]"}"""));
        var phases = new List<string>();
        await Run(probe, (_, current) => new MeetingChatToolResult(current), phases);
        Assert.Single(phases, phase => phase == "Preparing answer");
        Assert.True(phases.Count >= 4);
        Assert.Equal(phases[2], phases[3]);
    }

    private static JsonObject Event(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void NativeStreamAccumulatesCompletedItemsWithoutDuplicates()
    {
        var accumulator = new MeetingNativeResponseAccumulator();
        const string function = """{"type":"function_call","id":"fc1","call_id":"call1","name":"browse_meeting_sources","arguments":"{}"}""";
        Assert.Null(accumulator.Apply(Event($$"""{"type":"response.output_item.done","output_index":1,"item":{{function}}}""")));
        Assert.Null(accumulator.Apply(Event("""{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"r1","encrypted_content":"opaque","summary":[]}}""")));
        // Repeated item completion must not duplicate the call or reasoning.
        Assert.Null(accumulator.Apply(Event($$"""{"type":"response.output_item.done","output_index":1,"item":{{function}}}""")));
        var result = accumulator.Apply(Event("""{"type":"response.completed","response":{"id":"resp1"}}"""))!;
        Assert.Equal("call1", Assert.Single(result.Calls).Id);
        Assert.Equal(2, result.OutputItems.Count);
        Assert.Equal("opaque", MeetingNativeToolTransport.Str(result.OutputItems[0]!["encrypted_content"]));
    }

    [Fact]
    public void NativeStreamRejectsPartialCallsAndFailedTerminalEvents()
    {
        var accumulator = new MeetingNativeResponseAccumulator();
        Assert.Null(accumulator.Apply(Event("""{"type":"response.function_call_arguments.delta","item_id":"fc1","delta":"{}"}""")));
        Assert.ThrowsAny<Exception>(() => accumulator.Apply(Event("""{"type":"response.completed","response":{"id":"resp1"}}""")));
        foreach (var type in new[] { "response.incomplete", "response.failed", "error" })
            Assert.ThrowsAny<Exception>(() => new MeetingNativeResponseAccumulator().Apply(Event($$"""{"type":"{{type}}"}""")));
    }

    [Fact]
    public void NativeStreamEmptyTerminalRetainsFinalAnswer()
    {
        var accumulator = new MeetingNativeResponseAccumulator();
        accumulator.Apply(Event("""{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m0","role":"assistant","content":[{"type":"output_text","text":"Thinking aloud"}]}}"""));
        accumulator.Apply(Event("""{"type":"response.output_item.done","output_index":1,"item":{"type":"message","id":"m1","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"{\"status\":\"conversation\",\"markdown\":\"Shorter.\"}"}]}}"""));
        var response = accumulator.Apply(Event("""{"type":"response.completed","response":{"id":"resp1","status":"completed","output":[]}}"""))!;
        Assert.Empty(response.Calls);
        Assert.Equal("""{"status":"conversation","markdown":"Shorter."}""", response.Text);
    }

    [Fact]
    public async Task CodexStreamReaderReturnsTheCompletedResponse()
    {
        var sse = """
            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"m1","role":"assistant","content":[{"type":"output_text","text":"{\"status\":\"insufficient_evidence\",\"markdown\":\"No evidence\"}"}]}}

            data: {"type":"response.completed","response":{"id":"resp1"}}

            """;
        var response = await MeetingSummaryTextGenerator.ReadNativeStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), CancellationToken.None);
        Assert.Contains("insufficient_evidence", response.Text);
    }

    [Fact]
    public void RequestBodyRegistersSchemaToolsAndReasoningBudget()
    {
        var request = new MeetingNativeToolRequest(new MeetingTextGenerationRequest("Meeting instructions", "", 1_500, 32_768), [], MeetingChatNativeHarness.Tools, AllowTools: true);
        var body = MeetingNativeToolTransport.RequestBody(request, "openai/gpt-5.4", stream: false, "low");
        Assert.Equal("openai/gpt-5.4", MeetingNativeToolTransport.Str(body["model"]));
        Assert.Null(body["messages"]);
        Assert.Equal("auto", MeetingNativeToolTransport.Str(body["tool_choice"]));
        Assert.Equal(32_768, body["max_output_tokens"]!.GetValue<int>());
        Assert.Equal("low", MeetingNativeToolTransport.Str(body["reasoning"]!["effort"]));
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("none", MeetingNativeToolTransport.Str(MeetingNativeToolTransport.RequestBody(request with { AllowTools = false }, "m", false)["tool_choice"]));
        // No reasoning level: no parameter and no reserved reasoning budget.
        var plain = MeetingNativeToolTransport.RequestBody(request, "chat-latest", stream: false);
        Assert.Null(plain["reasoning"]);
        Assert.Equal(1_500, plain["max_output_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReasoningSummariesStreamOnlyPublicSummaryText()
    {
        var shown = new List<string>();
        var request = new MeetingNativeToolRequest(new MeetingTextGenerationRequest("i", "", OnReasoningSummary: text => { shown.Add(text); return Task.CompletedTask; }),
            [], MeetingChatNativeHarness.Tools, AllowTools: true);
        Assert.Equal("auto", MeetingNativeToolTransport.Str(MeetingNativeToolTransport.RequestBody(request, "gpt-6.1-sol", true, "medium")["reasoning"]!["summary"]));
        Assert.Null(MeetingNativeToolTransport.RequestBody(request, "gpt-5.4", true, "none")["reasoning"]!["summary"]);
        Assert.Null(MeetingNativeToolTransport.RequestBody(request with { Base = request.Base with { OnReasoningSummary = null } }, "gpt-6.1-sol", true, "medium")["reasoning"]!["summary"]);

        var sse = string.Join("\n", new[]
        {
            """{"type":"response.reasoning_summary_text.delta","item_id":"r1","output_index":0,"summary_index":0,"delta":"Checking "}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"r1","output_index":0,"summary_index":0,"delta":"the notes"}""",
            """{"type":"response.reasoning_text.delta","item_id":"r1","output_index":0,"content_index":0,"delta":"RAW"}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"r1","output_index":0,"summary_index":1,"delta":"Second"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"r1","encrypted_content":"SECRET","content":[{"type":"reasoning_text","text":"RAW"}],"summary":[{"type":"summary_text","text":"Checking the notes."}]}}""",
            """{"type":"response.reasoning_summary_text.delta","item_id":"r1","output_index":0,"summary_index":0,"delta":"late"}""",
            """{"type":"response.completed","response":{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done"}]}]}}"""
        }.Select(line => "data: " + line));
        var response = await MeetingSummaryTextGenerator.ReadNativeStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), CancellationToken.None, request.Base.OnReasoningSummary);
        Assert.Equal("Done", response.Text);
        Assert.Equal(["Checking ", "Checking the notes", "Checking the notes\n\nSecond", "Checking the notes."], shown);
        Assert.DoesNotContain(shown, text => text.Contains("RAW") || text.Contains("SECRET"));
    }

    [Fact]
    public void SummaryThinkingFollowsTheModelsLevelsAndDefaults()
    {
        Assert.Equal("medium", ReasoningEffortPolicy.Resolved("gpt-6.1-sol", null));
        Assert.Equal("max", ReasoningEffortPolicy.Resolved("gpt-6.1-sol", "max"));
        Assert.Equal("medium", ReasoningEffortPolicy.Resolved("gpt-6.1-sol", "none"));
        Assert.Null(ReasoningEffortPolicy.Resolved("chat-latest", "high"));
        var settings = new MuesliSettings { MeetingSummaryProvider = "chatgpt", MeetingSummaryReasoningEffort = "high" };
        Assert.Equal("gpt-6.1-sol", MeetingSummaryModels.Model(settings));
        Assert.Equal("high", MeetingSummaryModels.Effort(settings));
        var openRouter = settings with { MeetingSummaryProvider = "openrouter", OpenRouterModel = "openai/gpt-6-sol" };
        Assert.Equal("gpt-6-sol", MeetingSummaryModels.PolicyModel(openRouter));
        Assert.Equal("Extra High", ReasoningEffortPolicy.Label("xhigh"));
    }
}
