using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Muesli.Windows.ComputerUse;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class CuaPlannerTests
{
    [Fact]
    public async Task StreamYieldsEveryToolCallInOrder()
    {
        var sse = Sse(
            new { type = "response.output_text.delta", delta = "Opening it." },
            new { type = "response.output_item.done", item = new { type = "function_call", call_id = "c1", name = "launch_app", arguments = "{\"app_name\":\"Notepad\"}" } },
            new { type = "response.output_item.done", item = new { type = "function_call", call_id = "c2", name = "type_text", arguments = "{\"text\":\"hi\"}" } },
            new { type = "response.completed", response = new { output = new object[] { new { type = "function_call", call_id = "c1", name = "launch_app", arguments = "{}" } } } });

        var plan = await ResponsesPlanner.ParseStream(sse, default);

        Assert.Equal(new[] { "launch_app", "type_text" }, plan.Calls.Select(call => call.Name));
        Assert.Equal("c2", plan.Calls[1].CallId);
        Assert.Equal("Notepad", plan.Calls[0].String("app_name"));
        Assert.Equal("Opening it.", plan.Text);
    }

    [Fact]
    public async Task CallsOnlyInTheCompletedEventAreRead()
    {
        var sse = Sse(new { type = "response.completed", response = new { output = new object[] { new { type = "function_call", call_id = "x", name = "finish", arguments = "{\"reason\":\"ok\"}" } } } });
        var plan = await ResponsesPlanner.ParseStream(sse, default);
        Assert.Equal("finish", Assert.Single(plan.Calls).Name);
    }

    [Fact]
    public async Task TextWithoutToolCallIsARepairableError()
    {
        var sse = Sse(new { type = "response.output_text.delta", delta = "I think you should" });
        var error = await Assert.ThrowsAsync<CuaInvalidToolCallException>(() => ResponsesPlanner.ParseStream(sse, default));
        Assert.Contains("I think you should", error.Message);
    }

    [Fact]
    public async Task StreamErrorsSurfaceTheMessage()
    {
        var sse = Sse(new { type = "response.failed", response = new { error = new { message = "usage limit reached" } } });
        var error = await Assert.ThrowsAsync<CuaPlannerException>(() => ResponsesPlanner.ParseStream(sse, default));
        Assert.Contains("usage limit reached", error.Message);
    }

    [Fact]
    public void RequestReplaysHistoryAndSendsOnlyTheLatestScreenshot()
    {
        var state = new CuaWindowState("s1", "Notepad", "Notepad", 7, "Untitled - Notepad", new CuaRect(0, 0, 800, 600),
            new CuaScreenshot("shot-1", 800, 600, new CuaRect(0, 0, 800, 600), 1, 1) { ImageDataUrl = "data:image/jpeg;base64,AAAA" },
            null, null, null, null, null,
            [new CuaElement("e1", 1, "Document", "Text editor", "focused", "", "", true, null, "", ["click", "set_value"], 1)],
            DateTimeOffset.UtcNow);
        var history = new[]
        {
            new CuaToolOutcome(1, CuaToolCall.Parse("launch_app", "{\"app_name\":\"Notepad\"}", "c1"), CuaResultStatus.Executed, "Opened Notepad."),
            new CuaToolOutcome(2, CuaToolCall.Parse("type_text", "{}"), CuaResultStatus.Failed, "Invalid tool call."),
        };

        var body = ResponsesPlanner.RequestBody(new CuaPlannerRequest("write hi", 3, 50, state, history), "gpt-test", "low");

        Assert.False(body["store"]!.GetValue<bool>());
        Assert.True(body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Null(body["max_output_tokens"]);
        Assert.Equal("low", body["reasoning"]!["effort"]!.GetValue<string>());
        var input = body["input"]!.AsArray();
        Assert.Equal(6, input.Count);
        Assert.Equal("c1", input[1]!["call_id"]!.GetValue<string>());
        Assert.Equal("c1", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("executed: Opened Notepad.", input[2]!["output"]!.GetValue<string>());
        Assert.Equal(input[3]!["call_id"]!.GetValue<string>(), input[4]!["call_id"]!.GetValue<string>());
        var last = input[5]!["content"]!.AsArray();
        Assert.Contains("[1] Document \"Text editor\" (focused) actions=set_value", last[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AAAA", last[1]!["image_url"]!.GetValue<string>());
        Assert.Single(input, item => item?["content"] is JsonArray content && content.Any(part => part?["type"]?.GetValue<string>() == "input_image"));
        Assert.Equal(CuaCatalog.Tools.Count, body["tools"]!.AsArray().Count);
    }

    [Fact]
    public async Task ChatGptRequestCarriesAccountHeadersAndRetriesTransientFailures()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{\"detail\":\"upstream\"}") });
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(Sse(
            new { type = "response.output_item.done", item = new { type = "function_call", call_id = "c1", name = "finish", arguments = "{\"reason\":\"ok\"}" } })) });
        var secrets = new InMemorySecretStore();
        var auth = new ChatGptAuth(secrets, new HttpClient(handler), _ => { });
        SaveTokens(secrets, new ChatGptAuth.Tokens(Jwt("acct-1"), "refresh", DateTimeOffset.UtcNow.AddHours(1), "acct-1"));

        var planner = ResponsesPlanner.WithChatGpt(new HttpClient(handler), auth, appVersion: "2.0");
        var plan = await planner.PlanAsync(new CuaPlannerRequest("x", 1, 5, CuaWindowState.Empty("none"), []), default);

        Assert.Equal("finish", plan.Calls[0].Name);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[1];
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.AbsoluteUri);
        Assert.Equal("acct-1", request.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("muesli", request.Headers.GetValues("originator").Single());
        Assert.StartsWith("Muesli/2.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshedAndSaved()
    {
        var handler = new FakeHandler();
        var fresh = Jwt("acct-2");
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = fresh, refresh_token = "r2", expires_in = 3600 })),
        });
        var secrets = new InMemorySecretStore();
        var auth = new ChatGptAuth(secrets, new HttpClient(handler), _ => { });
        SaveTokens(secrets, new ChatGptAuth.Tokens(Jwt("acct-1"), "r1", DateTimeOffset.UtcNow.AddSeconds(-5), "acct-1"));

        var (token, account) = await auth.GetAccessTokenAsync(default);

        Assert.Equal(fresh, token);
        Assert.Equal("acct-2", account);
        Assert.Contains("grant_type=refresh_token", handler.Bodies[0]);
        Assert.Contains("refresh_token=r1", handler.Bodies[0]);
        Assert.Equal((fresh, "acct-2"), await auth.GetAccessTokenAsync(default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void LongTokensAreSplitAcrossCredentials()
    {
        var secrets = new InMemorySecretStore();
        var auth = new ChatGptAuth(secrets, new HttpClient(new FakeHandler()), _ => { });
        Assert.False(auth.IsSignedIn);
        SaveTokens(secrets, new ChatGptAuth.Tokens(new string('a', 4000), "r", DateTimeOffset.UtcNow.AddHours(1), "x"));
        Assert.True(auth.IsSignedIn);
        auth.SignOut();
        Assert.False(auth.IsSignedIn);
        Assert.False(secrets.IsConfigured("chatgpt-auth/0"));
    }

    [Fact]
    public void AuthorizationUrlMatchesTheMacOSFlow()
    {
        var uri = ChatGptAuth.AuthorizationUri("challenge", "state");
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?", uri.AbsoluteUri);
        foreach (var part in new[] { "client_id=app_EMoamEEZ73f0CkXaXp7hrann", "redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback",
                     "code_challenge_method=S256", "codex_cli_simplified_flow=true", "originator=muesli", "scope=openid%20profile%20email%20offline_access" })
            Assert.Contains(part, uri.AbsoluteUri);
    }

    [Fact]
    public void AccountIdComesFromEitherClaimPath()
    {
        Assert.Equal("acct-9", ChatGptAuth.AccountId(Jwt("acct-9")));
        var nested = Encode(new Dictionary<string, object> { ["https://api.openai.com/auth"] = new { chatgpt_account_id = "acct-n" } });
        Assert.Equal("acct-n", ChatGptAuth.AccountId($"h.{nested}.s"));
        Assert.Equal("", ChatGptAuth.AccountId("garbage"));
    }

    /// <summary>Writes tokens the way <see cref="ChatGptAuth"/> stores them.</summary>
    private static void SaveTokens(ISecretStore secrets, ChatGptAuth.Tokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        var parts = json.Chunk(1000).Select(chunk => new string(chunk)).ToArray();
        for (var index = 0; index < parts.Length; index++) secrets.Write($"chatgpt-auth/{index}", parts[index]);
        secrets.Write("chatgpt-auth", parts.Length.ToString());
    }

    private static string Jwt(string account) => $"h.{Encode(new { chatgpt_account_id = account, pad = new string('p', 1500) })}.s";

    private static string Encode(object payload) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static MemoryStream Sse(params object[] events) =>
        new(Encoding.UTF8.GetBytes(string.Concat(events.Select(e => $"event: x\ndata: {JsonSerializer.Serialize(e)}\n\n")) + "data: [DONE]\n\n"));

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Responses.Dequeue();
        }
    }
}
