using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed record ChatGptModelOption(string Id, string Label, string DefaultEffort);

/// <summary>
/// Meeting-notes requests against the signed-in ChatGPT account, a port of the macOS
/// <c>ChatGPTResponsesClient</c>: a streamed Responses call whose text is assembled from the
/// server-sent events.
/// </summary>
public static class ChatGptResponsesClient
{
    public const string Endpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string DefaultModel = "gpt-6.1-sol";
    private const int MaxResponseBytes = 256_000;

    /// <summary>The macOS model presets, in its order.</summary>
    public static IReadOnlyList<ChatGptModelOption> Models { get; } =
    [
        new("gpt-6.1-sol", "GPT-6.1 Sol (default)", "medium"),
        new("gpt-6-astra", "GPT-6 Astra", "high"),
        new("gpt-6-sol", "GPT-6 Sol", "medium"),
        new("gpt-6-luna", "GPT-6 Luna", "medium")
    ];

    public static string NormalizeModel(string? model)
    {
        var trimmed = model?.Trim() ?? "";
        return trimmed.Length == 0 ? DefaultModel : trimmed;
    }

    public static HttpRequestMessage BuildRequest(
        ChatGptCredential credential, string model, string instructions, string userPrompt, string appVersion)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["store"] = false,
            ["stream"] = true,
            ["instructions"] = instructions,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = userPrompt } }
                }
            }
        };
        // Only the known presets declare a thinking level; a custom model id is sent without one.
        if (Models.FirstOrDefault(option => option.Id.Equals(model, StringComparison.OrdinalIgnoreCase)) is { } preset)
        {
            body["reasoning"] = new Dictionary<string, object?> { ["effort"] = preset.DefaultEffort };
        }

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (credential.AccountId.Length > 0) request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("originator", "muesli");
        request.Headers.TryAddWithoutValidation("User-Agent", $"Muesli/{appVersion}");
        request.Headers.TryAddWithoutValidation("session_id", Guid.NewGuid().ToString("D").ToLowerInvariant());
        return request;
    }

    public static async Task<string> RespondAsync(
        HttpClient http, ChatGptCredential credential, string model, string instructions, string userPrompt,
        string appVersion, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(credential, model, instructions, userPrompt, appVersion);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await ReadEventStreamAsync(reader, cancellationToken).ConfigureAwait(false);
        return text.Length == 0 ? throw new SummaryProviderException("empty-response") : text;
    }

    /// <summary>Assembles the answer from a Responses event stream.</summary>
    public static async Task<string> ReadEventStreamAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        var deltas = new StringBuilder();
        var final = "";
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..].Trim();
            if (payload == "[DONE]") break;
            if (payload is "ping" or "heartbeat" or "keep-alive" or "") continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(payload); }
            catch (JsonException) { throw new SummaryProviderException("malformed-response"); }
            using (document)
            {
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String
                    ? typeValue.GetString() : "";
                if (type == "response.output_text.delta")
                {
                    if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String) deltas.Append(delta.GetString());
                }
                else if (type is "response.failed" or "error")
                {
                    throw new SummaryProviderException("provider-error");
                }
                else if (root.TryGetProperty("response", out var completed) && FinalText(completed) is { Length: > 0 } text)
                {
                    final = text;
                }
            }
        }
        var result = (final.Length > 0 ? final : deltas.ToString()).Trim();
        return Encoding.UTF8.GetByteCount(result) > MaxResponseBytes ? result[..Math.Min(result.Length, MaxResponseBytes / 4)] : result;
    }

    // The final answer of a completed response: the last assistant message, skipping reasoning items.
    private static string FinalText(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return "";
        if (response.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String &&
            outputText.GetString() is { Length: > 0 } direct) return direct;
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return "";
        var last = "";
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("type", out var itemType) && itemType.GetString() == "reasoning") continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            var builder = new StringBuilder();
            foreach (var entry in content.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    builder.Append(text.GetString());
            }
            if (builder.Length > 0) last = builder.ToString();
        }
        return last;
    }
}
