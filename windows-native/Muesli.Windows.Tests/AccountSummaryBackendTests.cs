using System.Net;
using System.Text;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// The account-backed notes providers ported from macOS: "Sign in with ChatGPT" and the installed
/// Claude Code CLI. Nothing here talks to a real service or starts a real CLI.
/// </summary>
public sealed class AccountSummaryBackendTests
{
    private static string Jwt(string payloadJson) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void AuthorizeUrlCarriesTheMacOsFlowParametersAndAPkceChallenge()
    {
        var (verifier, challenge) = ChatGptAuthManager.CreatePkce();
        Assert.Equal(43, verifier.Length);
        Assert.Equal(43, challenge.Length);
        Assert.NotEqual(verifier, challenge);

        var uri = ChatGptAuthManager.BuildAuthorizeUri("state-1", challenge).AbsoluteUri;
        Assert.StartsWith("https://auth.openai.com/oauth/authorize?client_id=", uri, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback", uri, StringComparison.Ordinal);
        Assert.Contains("scope=openid%20profile%20email%20offline_access", uri, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", uri, StringComparison.Ordinal);
        Assert.Contains($"code_challenge={challenge}", uri, StringComparison.Ordinal);
        Assert.Contains("state=state-1", uri, StringComparison.Ordinal);
        Assert.Contains("originator=muesli", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackAcceptsOnlyTheMatchingStateAndIgnoresStrayRequests()
    {
        Assert.Equal(("abc", null), ChatGptAuthManager.ParseCallback("GET /auth/callback?code=abc&state=s1 HTTP/1.1", "s1"));
        var mismatch = ChatGptAuthManager.ParseCallback("GET /auth/callback?code=abc&state=other HTTP/1.1", "s1");
        Assert.Null(mismatch.Code);
        Assert.Contains("state mismatch", mismatch.Error, StringComparison.OrdinalIgnoreCase);
        var missing = ChatGptAuthManager.ParseCallback("GET /auth/callback?state=s1 HTTP/1.1", "s1");
        Assert.Null(missing.Code);
        Assert.Contains("missing authorization code", missing.Error, StringComparison.OrdinalIgnoreCase);
        // A favicon request is neither a result nor a failure; sign-in keeps waiting.
        Assert.Equal((null, null), ChatGptAuthManager.ParseCallback("GET /favicon.ico HTTP/1.1", "s1"));
    }

    [Fact]
    public void AccountIdIsReadFromTheAccessTokenClaimsInTheMacOsOrder()
    {
        Assert.Equal("acct-top", ChatGptAuthManager.AccountIdFromAccessToken(Jwt("""{"chatgpt_account_id":"acct-top"}""")));
        Assert.Equal("acct-nested", ChatGptAuthManager.AccountIdFromAccessToken(
            Jwt("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-nested"}}""")));
        Assert.Equal("org-1", ChatGptAuthManager.AccountIdFromAccessToken(Jwt("""{"organizations":[{"id":"org-1"}]}""")));
        Assert.Equal("", ChatGptAuthManager.AccountIdFromAccessToken("not-a-token"));
    }

    [Fact]
    public async Task AnExpiringTokenIsRefreshedAndStoredInPartsThatFitACredential()
    {
        var now = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        var secrets = new InMemorySecretStore();
        var longToken = Jwt("{\"chatgpt_account_id\":\"acct-9\",\"pad\":\"" + new string('x', 3000) + "\"}");
        var handler = new RecordingHandler(HttpStatusCode.OK,
            "{\"access_token\":\"" + longToken + "\",\"expires_in\":3600}");
        var auth = new ChatGptAuthManager(secrets, new HttpClient(handler), () => now);
        Assert.False(auth.IsSignedIn);
        await Assert.ThrowsAsync<ChatGptAuthException>(() => auth.GetValidCredentialAsync());

        // Seed a session that expires in ten seconds, in the stored layout (part count + parts).
        var seed = "{\"access_token\":\"old\",\"refresh_token\":\"refresh-1\",\"expires_at\":\"" +
                   now.AddSeconds(10).ToUnixTimeMilliseconds() + "\",\"account_id\":\"acct-old\"}";
        secrets.Write(ChatGptAuthManager.SecretKey + ".0", seed);
        secrets.Write(ChatGptAuthManager.SecretKey, "1");
        Assert.True(auth.IsSignedIn);

        var credential = await auth.GetValidCredentialAsync();
        Assert.Equal(longToken, credential.AccessToken);
        Assert.Equal("acct-9", credential.AccountId);
        Assert.Contains("grant_type=refresh_token", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-1", handler.LastBody, StringComparison.Ordinal);

        // Windows credentials hold 2560 bytes; every stored part must stay below that in UTF-16.
        var parts = int.Parse(secrets.Read(ChatGptAuthManager.SecretKey)!);
        Assert.True(parts > 1);
        for (var index = 0; index < parts; index++)
        {
            Assert.True(Encoding.Unicode.GetByteCount(secrets.Read($"{ChatGptAuthManager.SecretKey}.{index}")!) <= 2560);
        }

        // The refreshed token is not about to expire, so the next call does not hit the network.
        handler.Calls = 0;
        await auth.GetValidCredentialAsync();
        Assert.Equal(0, handler.Calls);

        auth.SignOut();
        Assert.False(auth.IsSignedIn);
        Assert.Null(secrets.Read(ChatGptAuthManager.SecretKey + ".0"));
    }

    [Fact]
    public async Task NotesRequestMatchesTheMacOsShapeAndTheStreamIsAssembledIntoText()
    {
        using var request = ChatGptResponsesClient.BuildRequest(
            new ChatGptCredential("token-1", "acct-1"), "gpt-6.1-sol", "SYSTEM", "USER", "1.2.3");
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", request.RequestUri!.ToString());
        Assert.Equal("Bearer token-1", request.Headers.Authorization!.ToString());
        Assert.Equal("acct-1", request.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("muesli", request.Headers.GetValues("originator").Single());
        Assert.Equal("text/event-stream", request.Headers.Accept.Single().MediaType);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"store\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"instructions\":\"SYSTEM\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_text\",\"text\":\"USER\"", body, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":{\"effort\":\"medium\"}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("max_output_tokens", body, StringComparison.Ordinal);

        const string deltasOnly = "event: x\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"## Notes\"}\n" +
                                  "data: ping\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"\\n- one\"}\ndata: [DONE]\n";
        Assert.Equal("## Notes\n- one", await ChatGptResponsesClient.ReadEventStreamAsync(new StringReader(deltasOnly)));

        // A completed response wins over the deltas, and reasoning items are not notes.
        const string completed = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"draft\"}\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"output\":[" +
            "{\"type\":\"reasoning\",\"content\":[{\"text\":\"thinking\"}]}," +
            "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Final notes\"}]}]}}\n";
        Assert.Equal("Final notes", await ChatGptResponsesClient.ReadEventStreamAsync(new StringReader(completed)));

        await Assert.ThrowsAsync<SummaryProviderException>(() =>
            ChatGptResponsesClient.ReadEventStreamAsync(new StringReader("data: not json\n")));
    }

    [Fact]
    public void ClaudeCodeRunsInPrintModeWithToolsOffAndNeverPutsThePromptOnTheCommandLine()
    {
        var arguments = ClaudeCodeSummarizer.BuildArguments("sonnet");
        Assert.Equal(
            ["-p", "--output-format", "json", "--no-session-persistence", "--disable-slash-commands",
             "--strict-mcp-config", "--setting-sources", "user", "--model", "sonnet", "--tools", ""],
            arguments);

        // No model follows Claude Code's own setting; anything that is not a plain model id is dropped.
        Assert.DoesNotContain("--model", ClaudeCodeSummarizer.BuildArguments(""));
        Assert.DoesNotContain("--model", ClaudeCodeSummarizer.BuildArguments("sonnet & calc.exe"));
        Assert.Null(ClaudeCodeSummarizer.LocateExecutable(@"C:\definitely\missing\claude.exe"));
    }

    [Fact]
    public void ClaudeCodeResultIsTheResultFieldAndErrorsAreNotNotes()
    {
        Assert.Equal("Notes", ClaudeCodeSummarizer.ParseResult(
            """{"type":"result","subtype":"success","is_error":false,"result":"  Notes  "}""", 0));
        Assert.Equal("Later", ClaudeCodeSummarizer.ParseResult(
            "{\"type\":\"system\"}\n{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"Later\"}", 0));
        Assert.Equal("From array", ClaudeCodeSummarizer.ParseResult(
            """[{"type":"system"},{"type":"result","result":"From array"}]""", 0));

        foreach (var (output, exitCode) in new[]
                 {
                     ("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in"}""", 0),
                     ("""{"type":"result","subtype":"error_max_turns","result":""}""", 0),
                     ("""{"type":"result","subtype":"success","result":"text"}""", 1),
                     ("""{"type":"result","subtype":"success","result":"   "}""", 0),
                     ("plain text, not JSON", 0)
                 })
        {
            Assert.Throws<SummaryProviderException>(() => ClaudeCodeSummarizer.ParseResult(output, exitCode));
        }
    }

    [Fact]
    public async Task ChatGptNotesFallBackToLocalNotesWhenNobodyIsSignedIn()
    {
        var previous = ChatGptAuthManager.Shared;
        ChatGptAuthManager.Shared = new ChatGptAuthManager(new InMemorySecretStore());
        try
        {
            var result = await MeetingSummaryService.CreateSummaryResultAsync(
                "[You] We agreed to ship the importer on Friday.", "Planning",
                new MuesliSettings { MeetingSummaryProvider = SummaryProviderDisclosure.ChatGpt });
            Assert.True(result.UsedLocalFallback);
            Assert.Equal("not-signed-in", result.SafeFailureReason);
            Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        }
        finally
        {
            ChatGptAuthManager.Shared = previous;
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
