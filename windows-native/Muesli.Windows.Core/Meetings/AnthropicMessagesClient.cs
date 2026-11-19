using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed record AnthropicModelOption(string Id, string Label);

/// <summary>
/// The Anthropic Messages API, called with the user's own API key (macOS
/// <c>MeetingSummaryClient.summarizeWithAnthropicMessages</c>). The key, the prompt and the
/// response text never reach a log or a failure reason.
/// </summary>
public static class AnthropicMessagesClient
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");
    public const string ApiVersion = "2023-06-01";
    public const string DefaultModel = "claude-sonnet-5-5";

    /// <summary>
    /// Thinking is on by default for these models and counts against the limit, so the limit
    /// leaves room for it. It stays at the size Anthropic recommends for a request that is not
    /// streamed, which keeps one summary inside an ordinary HTTP timeout.
    /// </summary>
    public const int MaxOutputTokens = 16_000;

    /// <summary>Sonnet is the macOS default; the rest are the current models of each tier.</summary>
    public static IReadOnlyList<AnthropicModelOption> Models { get; } =
    [
        new("claude-sonnet-5-5", "Claude Sonnet 5.5 (default)"),
        new("claude-opus-5-5", "Claude Opus 5.5"),
        new("claude-fable-5-2", "Claude Fable 5.2"),
        new("claude-haiku-5-5", "Claude Haiku 5.5"),
    ];

    public static string NormalizeModel(string? model)
    {
        var trimmed = model?.Trim() ?? "";
        return trimmed.Length == 0 ? DefaultModel : trimmed;
    }

    /// <param name="briefTask">
    /// A short side request such as a meeting title: asks for low effort, as macOS does
    /// (<c>AnthropicModelPolicy.briefTaskEffort</c>).
    /// </param>
    public static HttpRequestMessage BuildRequest(
        string apiKey, string model, string systemPrompt, string userPrompt, bool briefTask = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = NormalizeModel(model),
            ["max_tokens"] = MaxOutputTokens,
            ["system"] = systemPrompt,
            ["messages"] = new object[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt } },
        };
        if (briefTask)
        {
            body["output_config"] = new Dictionary<string, object?> { ["effort"] = "low" };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    public static async Task<string> RespondAsync(
        HttpClient httpClient, string apiKey, string model, string systemPrompt, string userPrompt,
        bool briefTask, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(apiKey, model, systemPrompt, userPrompt, briefTask);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ReadText(json);
    }

    /// <summary>
    /// Joins the response's text blocks. A reply that was cut short or declined is a failure, not
    /// notes: saving half a summary would look like a finished one.
    /// </summary>
    public static string ReadText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SummaryProviderException("malformed-response");
        }

        var stopReason = root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String
            ? stop.GetString()
            : null;
        switch (stopReason)
        {
            case "max_tokens": throw new SummaryProviderException("output-limit-reached");
            case "model_context_window_exceeded": throw new SummaryProviderException("context-window-exceeded");
            case "refusal": throw new SummaryProviderException("refused");
        }

        var parts = new List<string>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
                    text.GetString() is { Length: > 0 } value)
                {
                    parts.Add(value);
                }
            }
        }

        var joined = string.Join("\n", parts).Trim();
        return joined.Length == 0 ? throw new SummaryProviderException("empty-response") : joined;
    }
}
