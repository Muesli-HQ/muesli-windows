using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muesli.Windows.ComputerUse;

public sealed class CuaPlannerException(string message, bool transient = false) : Exception(message)
{
    public bool Transient { get; } = transient;
}

/// <summary>
/// Plans Computer Use steps with an OpenAI Responses model and native tool calling. Signed in with
/// a ChatGPT account it uses the same Codex Responses endpoint and headers as macOS
/// (<c>ComputerUsePlannerClient</c>); with an API key it uses the public Responses API.
/// Each request replays the conversation (earlier tool calls and their results) with only the
/// latest screenshot, and the model may return several tool calls to run in order.
/// </summary>
public sealed class ResponsesPlanner : ICuaPlanner
{
    public const string DefaultModel = "gpt-6.1-sol";
    private const string CodexUrl = "https://chatgpt.com/backend-api/codex/responses";
    private const string ApiUrl = "https://api.openai.com/v1/responses";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<(string Token, string? AccountId)>> _credentials;
    private readonly bool _chatGpt;
    private readonly string _model;
    private readonly string? _reasoningEffort;
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly string _appVersion;

    private ResponsesPlanner(HttpClient http, Func<CancellationToken, Task<(string, string?)>> credentials, bool chatGpt, string? model, string? reasoningEffort, string appVersion)
    {
        _http = http;
        _credentials = credentials;
        _chatGpt = chatGpt;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;
        _appVersion = appVersion;
    }

    public static ResponsesPlanner WithChatGpt(HttpClient http, ChatGptAuth auth, string? model = null, string? reasoningEffort = null, string appVersion = "1.0") =>
        new(http, async token => await auth.GetAccessTokenAsync(token), true, model, reasoningEffort, appVersion);

    public static ResponsesPlanner WithApiKey(HttpClient http, string apiKey, string? model = null, string? reasoningEffort = null) =>
        new(http, _ => Task.FromResult<(string, string?)>((apiKey, null)), false, model, reasoningEffort, "1.0");

    public async Task<CuaPlan> PlanAsync(CuaPlannerRequest request, CancellationToken cancellationToken)
    {
        var body = RequestBody(request, _model, _reasoningEffort);
        for (var attempt = 0; ; attempt++)
        {
            try { return await Send(body, cancellationToken); }
            catch (CuaPlannerException exception) when (exception.Transient && attempt == 0)
            {
                await Task.Delay(800, cancellationToken);
            }
        }
    }

    private async Task<CuaPlan> Send(JsonObject body, CancellationToken cancellationToken)
    {
        var (token, accountId) = await _credentials(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, _chatGpt ? CodexUrl : ApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_chatGpt)
        {
            if (!string.IsNullOrEmpty(accountId)) message.Headers.Add("ChatGPT-Account-Id", accountId);
            message.Headers.Add("originator", "muesli");
            message.Headers.TryAddWithoutValidation("User-Agent", $"Muesli/{_appVersion} (Windows)");
            message.Headers.Add("session_id", _sessionId);
        }

        HttpResponseMessage response;
        try { response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException exception) { throw new CuaPlannerException($"The planner could not be reached: {exception.Message}", transient: true); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = (int)response.StatusCode;
                var detail = ErrorMessage(error) ?? response.ReasonPhrase ?? "";
                throw response.StatusCode == HttpStatusCode.Unauthorized
                    ? new CuaPlannerException(_chatGpt ? "ChatGPT rejected the sign-in. Sign in again in Muesli settings." : "OpenAI rejected the API key.")
                    : new CuaPlannerException($"The planner failed ({status}). {detail}".Trim(), transient: status is 408 or 429 or >= 500);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ParseStream(stream, cancellationToken);
        }
    }

    /// <summary>Reads the Responses event stream into the tool calls it produced.</summary>
    internal static async Task<CuaPlan> ParseStream(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var calls = new Dictionary<string, (int Order, string Name, string Arguments)>();
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonObject? json;
            try { json = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException) { continue; }
            if (json is null) continue;
            var type = json["type"]?.GetValue<string>();
            switch (type)
            {
                case "error" or "response.failed":
                    throw new CuaPlannerException($"The planner failed. {ErrorMessage(data) ?? "The model reported an error."}");
                case "response.output_text.delta":
                    text.Append(json["delta"]?.GetValue<string>());
                    break;
                case "response.output_item.done":
                    AddCall(json["item"] as JsonObject);
                    break;
                case "response.completed" or "response.done":
                    foreach (var item in json["response"]?["output"] as JsonArray ?? []) AddCall(item as JsonObject);
                    break;
            }
        }
        if (calls.Count == 0)
        {
            var said = text.ToString().Trim();
            throw new CuaInvalidToolCallException("", said, said.Length == 0
                ? "The model did not call a tool."
                : $"The model replied with text instead of a tool call: {said[..Math.Min(said.Length, 400)]}");
        }
        var parsed = calls.OrderBy(pair => pair.Value.Order)
            .Select(pair => CuaToolCall.Parse(pair.Value.Name, pair.Value.Arguments, pair.Key))
            .ToArray();
        return new CuaPlan(parsed, text.Length > 0 ? text.ToString() : null);

        void AddCall(JsonObject? item)
        {
            if (item?["type"]?.GetValue<string>() != "function_call") return;
            var id = item["call_id"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? $"call_{calls.Count}";
            if (calls.ContainsKey(id)) return;
            var arguments = item["arguments"] is JsonValue value && value.TryGetValue<string>(out var raw) ? raw : item["arguments"]?.ToJsonString() ?? "{}";
            calls[id] = (calls.Count, item["name"]?.GetValue<string>() ?? "", arguments);
        }
    }

    /// <summary>The Responses request: instructions, catalog tools, and the conversation so far.</summary>
    internal static JsonObject RequestBody(CuaPlannerRequest request, string model, string? reasoningEffort)
    {
        var input = new JsonArray
        {
            Message("user", [Text($"Task: {request.Command}")]),
        };
        var callNumber = 0;
        foreach (var outcome in request.History)
        {
            var callId = outcome.Call.CallId ?? $"muesli_{outcome.Step}_{callNumber}";
            callNumber++;
            input.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = callId,
                ["name"] = outcome.Call.Name.Length > 0 ? outcome.Call.Name : "fail",
                ["arguments"] = outcome.Call.Arguments.ToJsonString(),
            });
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = $"{outcome.Status.ToString().ToLowerInvariant()}: {outcome.Message}",
            });
        }
        var content = new JsonArray { Text($"Step {request.Step} of at most {request.MaxSteps}.\n{CuaPrompt.Describe(request.State)}") };
        if (request.State.Screenshot?.ImageDataUrl is { } image)
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = image });
        input.Add(Message("user", content));

        var body = new JsonObject
        {
            ["model"] = model,
            ["store"] = false,
            ["stream"] = true,
            ["instructions"] = CuaPrompt.Instructions,
            ["tools"] = CuaCatalog.ResponsesTools(),
            ["tool_choice"] = "required",
            ["parallel_tool_calls"] = true,
            ["input"] = input,
        };
        if (reasoningEffort is not null) body["reasoning"] = new JsonObject { ["effort"] = reasoningEffort };
        return body;

        static JsonObject Text(string text) => new() { ["type"] = "input_text", ["text"] = text };
        static JsonObject Message(string role, JsonArray content) => new() { ["role"] = role, ["content"] = content };
    }

    private static string? ErrorMessage(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            for (var depth = 0; depth < 4 && node is JsonObject obj; depth++)
            {
                if (obj["message"] is JsonValue message && message.TryGetValue<string>(out var text) && text.Length > 0) return text[..Math.Min(text.Length, 600)];
                if (obj["detail"] is JsonValue detail && detail.TryGetValue<string>(out var detailText) && detailText.Length > 0) return detailText;
                node = obj["error"] ?? obj["response"]?["error"] ?? obj["response"];
            }
        }
        catch (JsonException) { }
        return null;
    }
}
