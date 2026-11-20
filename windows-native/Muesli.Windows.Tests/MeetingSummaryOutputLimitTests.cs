using System.Net;
using System.Text.Json;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-07 (macOS 7d2c1ff0, f3771ce9): no default summary output cap; an explicit limit uses each API's field.</summary>
public sealed class MeetingSummaryOutputLimitTests
{
    private static readonly MuesliSettings Base = new()
    {
        MeetingSummaryRetryCount = 0,
        ResolvedOpenAIApiKey = "test-key",
        ResolvedOpenRouterApiKey = "test-key",
        OllamaEndpoint = "http://localhost:11434",
        OllamaModel = "llama3",
        LmStudioEndpoint = "http://localhost:1234",
        LmStudioModel = "local",
        CustomLlmEndpoint = "http://localhost:8080/v1/chat/completions",
        CustomLlmModel = "custom"
    };

    private static async Task<JsonElement> RequestBody(string provider, int? limit, string? customEndpoint = null)
    {
        string? body = null;
        using var client = new HttpClient(new CaptureHandler(request => body = request));
        var settings = Base with
        {
            MeetingSummaryProvider = provider,
            MeetingSummaryMaxOutputTokens = limit,
            CustomLlmEndpoint = customEndpoint ?? Base.CustomLlmEndpoint
        };
        await MeetingSummaryService.CreateSummaryResultAsync("We shipped it.", "Sync", settings, httpClient: client);
        return JsonDocument.Parse(body!).RootElement.Clone();
    }

    [Theory]
    [InlineData("openai", "max_output_tokens")]
    [InlineData("openrouter", "max_tokens")]
    [InlineData("lmstudio", "max_tokens")]
    [InlineData("custom", "max_tokens")]
    public async Task ProvidersDecideByDefaultAndAnExplicitLimitUsesTheirField(string provider, string field)
    {
        var unlimited = await RequestBody(provider, null);
        Assert.False(unlimited.TryGetProperty(field, out _));
        Assert.False(unlimited.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(10_000, (await RequestBody(provider, 10_000)).GetProperty(field).GetInt32());
    }

    [Fact]
    public async Task OllamaGeneratesUntilTheModelStopsUnlessLimited()
    {
        Assert.Equal(-1, (await RequestBody("ollama", null)).GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(5_000, (await RequestBody("ollama", 5_000)).GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task ACustomOpenAIEndpointUsesMaxCompletionTokens()
    {
        var body = await RequestBody("custom", 20_000, "https://api.openai.com/v1/chat/completions");
        Assert.Equal(20_000, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(2_500, 2_500)]
    public void OnlyPositiveLimitsCount(int? value, int? expected) =>
        Assert.Equal(expected, MeetingSummaryOutputPolicy.NormalizedLimit(value));

    private sealed class CaptureHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
