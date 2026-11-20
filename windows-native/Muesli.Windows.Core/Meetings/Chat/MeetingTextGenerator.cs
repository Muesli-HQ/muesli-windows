using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muesli.Windows.Services;

/// <summary>macOS MeetingTextGenerationRequest. OutputTokenLimit caps reasoning plus visible output.
/// OnReasoningSummary receives the provider's public reasoning summary so far (ChatGPT only); it is
/// transient display text and is never saved.</summary>
public sealed record MeetingTextGenerationRequest(string System, string User, int MaxOutputTokens = 1_500, int? OutputTokenLimit = null,
    Func<string, Task>? OnReasoningSummary = null);

/// <summary>macOS MeetingTextGenerating: the text transport Ask Meetings answers through.</summary>
public interface IMeetingTextGenerator
{
    string Provider { get; }
    string Model { get; }
    Task<string> GenerateAsync(MeetingTextGenerationRequest request, CancellationToken cancellationToken);
    bool SupportsNativeTools => false;
    Task<MeetingNativeToolResponse> GenerateNativeAsync(MeetingNativeToolRequest request, CancellationToken cancellationToken) =>
        throw new SummaryProviderException("unsupported-provider");
}

/// <summary>
/// macOS MeetingTextGenerationClient over the configured meeting-summary provider. "local" is keyword
/// notes with no model behind it, so Ask Meetings fails closed there with a connection prompt.
/// <para>ponytail: compatibility requests keep the summary transport's fixed 2,500-token output cap;
/// pass MaxOutputTokens through CompleteAsync if long answers truncate.</para>
/// </summary>
public sealed class MeetingSummaryTextGenerator(MuesliSettings settings, HttpClient? httpClient = null) : IMeetingTextGenerator
{
    /// <summary>macOS resourceTimeout: SSE heartbeats must not hold a chat open indefinitely.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly HttpClient _http = httpClient ?? Http;

    public string Provider { get; } = settings.MeetingSummaryProvider.Trim().ToLowerInvariant();

    public string Model => MeetingSummaryModels.Model(settings);

    /// <summary>macOS supportsNativeTools: by provider, and for OpenRouter only OpenAI Responses families.</summary>
    public bool SupportsNativeTools
    {
        get
        {
            if (Provider is "chatgpt" or "openai") return true;
            if (Provider != "openrouter") return false;
            var model = Model.ToLowerInvariant();
            return model.StartsWith("openai/gpt-", StringComparison.Ordinal) || new[] { "openai/o1", "openai/o3", "openai/o4" }
                .Any(family => model == family || model.StartsWith(family + "-", StringComparison.Ordinal) || model.StartsWith(family + ":", StringComparison.Ordinal));
        }
    }

    public async Task<string> GenerateAsync(MeetingTextGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!MeetingSummaryService.IsHostedProvider(Provider)) throw new SummaryProviderException("missing-connection");
        if (Provider == "chatgpt" && request.OnReasoningSummary is { } onReasoningSummary)
            return await MeetingSummaryService.CompleteWithChatGptAsync(request.System, request.User, httpClient, cancellationToken,
                Model, settings.MeetingSummaryReasoningEffort, onReasoningSummary);
        return await MeetingSummaryService.CompleteAsync(Provider, request.System, request.User, settings, httpClient, cancellationToken);
    }

    public async Task<MeetingNativeToolResponse> GenerateNativeAsync(MeetingNativeToolRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsNativeTools) throw new SummaryProviderException("unsupported-provider");
        if (Provider == "chatgpt") return await ChatGptNativeAsync(request, cancellationToken);
        var (url, key) = Provider == "openai"
            ? ("https://api.openai.com/v1/responses", Env("OPENAI_API_KEY") ?? settings.ResolvedOpenAIApiKey)
            : ("https://openrouter.ai/api/v1/responses", Env("OPENROUTER_API_KEY") ?? settings.ResolvedOpenRouterApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new SummaryProviderException("missing-key");
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (Provider == "openrouter") message.Headers.Add("X-OpenRouter-Title", "Muesli");
        message.Content = Json(MeetingNativeToolTransport.RequestBody(request, Model, stream: false, MeetingSummaryModels.Effort(settings)));
        using var response = await _http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        return MeetingNativeToolTransport.ParseResponse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>macOS ChatGPTResponsesClient.respondNative: the Codex endpoint only streams, and partial
    /// deltas are never a completed tool call or final answer.</summary>
    private async Task<MeetingNativeToolResponse> ChatGptNativeAsync(MeetingNativeToolRequest request, CancellationToken cancellationToken)
    {
        if (MeetingSummaryService.ChatGptCredentials is not { } credentials) throw new SummaryProviderException("not-signed-in");
        (string Token, string AccountId) signIn;
        try { signIn = await credentials(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { throw new SummaryProviderException("not-signed-in"); }
        var body = MeetingNativeToolTransport.RequestBody(request, Model, stream: true, MeetingSummaryModels.Effort(settings));
        // ChatGPT's Codex endpoint rejects this public Responses API field.
        body.Remove("max_output_tokens");
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signIn.Token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(signIn.AccountId)) message.Headers.Add("ChatGPT-Account-Id", signIn.AccountId);
        message.Headers.Add("originator", "muesli");
        message.Headers.Add("session_id", Guid.NewGuid().ToString());
        message.Content = Json(body);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadNativeStreamAsync(stream, cancellationToken, request.Base.OnReasoningSummary);
    }

    internal static async Task<MeetingNativeToolResponse> ReadNativeStreamAsync(Stream stream, CancellationToken cancellationToken,
        Func<string, Task>? onReasoningSummary = null)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var output = new MeetingNativeResponseAccumulator();
        var summary = new ReasoningSummaryAccumulator();
        var received = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            received += Encoding.UTF8.GetByteCount(line);
            if (received > 8_388_608 || line.Length > MeetingNativeToolTransport.MaximumResponseBytes) throw new SummaryProviderException("empty-response");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var raw = line[5..].Trim();
            if (raw == "[DONE]") break;
            JsonObject? payload;
            try { payload = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { continue; }
            if (payload is null) continue;
            if (summary.Apply(payload) && onReasoningSummary is not null) await onReasoningSummary(summary.Text);
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Apply(payload) is { } completed) return completed;
        }
        throw new SummaryProviderException("empty-response");
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
