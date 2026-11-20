using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muesli.Windows.Services;

public sealed record MeetingNativeToolRequest(MeetingTextGenerationRequest Base, JsonArray InputItems, JsonArray Tools, bool AllowTools);

public sealed record MeetingNativeToolCall(string Id, string Name, string Arguments);

/// <summary>OutputItems is the complete provider output, including encrypted reasoning, for the next request.</summary>
public sealed record MeetingNativeToolResponse(JsonArray OutputItems, IReadOnlyList<MeetingNativeToolCall> Calls, string Text);

public sealed class MeetingNativeToolTransportException(string message) : Exception(message)
{
    public static MeetingNativeToolTransportException InvalidResponse() => new("Meeting AI returned an unreadable response. Please retry.");
    public static MeetingNativeToolTransportException IncompleteResponse() => new("Meeting AI stopped before completing its response. Please retry.");
    public static MeetingNativeToolTransportException EmptyOutput() => new("Meeting AI completed without an answer or retrieval request. Please retry.");
    public static MeetingNativeToolTransportException MalformedToolCall() => new("Meeting AI returned an invalid retrieval request. Please retry.");
    public static MeetingNativeToolTransportException ResponseTooLarge() => new("Meeting AI returned more data than this chat can safely process. Try a narrower question.");
}

/// <summary>Port of macOS MeetingNativeToolTransport (2f10b23d): stateless Responses function calling.</summary>
public static class MeetingNativeToolTransport
{
    public const int MaximumResponseBytes = 2_097_152;

    /// <summary>macOS MeetingNativeToolTransport: a resolved reasoning effort other than "none" reserves
    /// reasoning room in the output budget; a model without reasoning sends no reasoning parameter.</summary>
    public static JsonObject RequestBody(MeetingNativeToolRequest request, string model, bool stream, string? effort = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new SummaryProviderException("missing-connection");
        if (Encoding.UTF8.GetByteCount(request.InputItems.ToJsonString()) > MaximumResponseBytes
            || Encoding.UTF8.GetByteCount(request.Tools.ToJsonString()) > 32_768) throw new SummaryProviderException("empty-response");
        var requestedBudget = effort is not null and not "none" ? Math.Max(request.Base.MaxOutputTokens, 32_768) : request.Base.MaxOutputTokens;
        var budget = Math.Min(requestedBudget, Math.Max(1, request.Base.OutputTokenLimit ?? requestedBudget));
        var body = new JsonObject
        {
            ["model"] = model, ["instructions"] = request.Base.System, ["input"] = request.InputItems.DeepClone(),
            ["tools"] = request.Tools.DeepClone(), ["parallel_tool_calls"] = false,
            ["tool_choice"] = request.AllowTools ? "auto" : "none",
            ["store"] = false, ["stream"] = stream, ["max_output_tokens"] = budget,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema", ["name"] = "meeting_answer", ["strict"] = true,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("conversation", "answered", "insufficient_evidence") },
                            ["markdown"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("status", "markdown"), ["additionalProperties"] = false
                    }
                }
            }
        };
        if (effort is not null)
        {
            var reasoning = new JsonObject { ["effort"] = effort };
            if (request.Base.OnReasoningSummary is not null && effort != "none") reasoning["summary"] = "auto";
            body["reasoning"] = reasoning;
        }
        return body;
    }

    public static MeetingNativeToolResponse ParseResponse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes) throw MeetingNativeToolTransportException.ResponseTooLarge();
        JsonObject? payload;
        try { payload = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { payload = null; }
        return ParseResponse(payload ?? throw MeetingNativeToolTransportException.InvalidResponse());
    }

    public static MeetingNativeToolResponse ParseResponse(JsonObject payload)
    {
        if (Str(payload["status"]) != "completed" || payload["error"] is not null) throw MeetingNativeToolTransportException.IncompleteResponse();
        if (payload["output"] is not JsonArray output) throw MeetingNativeToolTransportException.InvalidResponse();
        var serialized = (JsonArray)output.DeepClone();
        if (Encoding.UTF8.GetByteCount(serialized.ToJsonString()) > MaximumResponseBytes) throw MeetingNativeToolTransportException.ResponseTooLarge();
        var calls = new List<MeetingNativeToolCall>();
        var seen = new HashSet<string>();
        foreach (var node in output)
        {
            if (node is not JsonObject item) continue;
            if (Str(item["status"]) is "incomplete" or "failed") throw MeetingNativeToolTransportException.IncompleteResponse();
            if (Str(item["type"]) != "function_call") continue;
            if (Str(item["call_id"]) is not { Length: > 0 and <= 256 } id || Str(item["name"]) is not { Length: > 0 and <= 256 } name
                || Str(item["arguments"]) is not { } arguments || Encoding.UTF8.GetByteCount(arguments) > 16_384 || !seen.Add(id))
                throw MeetingNativeToolTransportException.MalformedToolCall();
            calls.Add(new MeetingNativeToolCall(id, name, arguments));
        }
        var text = ExtractOutputText(payload) ?? "";
        if (calls.Count == 0 && text.Trim().Length == 0) throw MeetingNativeToolTransportException.EmptyOutput();
        return new MeetingNativeToolResponse(serialized, calls, text);
    }

    /// <summary>macOS ChatGPTResponsesClient.extractOutputText: Codex may emit commentary before its
    /// terminal answer, so the declared final assistant message wins; reasoning items never count.</summary>
    public static string? ExtractOutputText(JsonObject payload)
    {
        if (FinalAssistantAnswer(payload) is { } final) return final;
        if (Str(payload["output_text"]) is { Length: > 0 } outputText) return outputText;
        if (payload["response"] is JsonObject response && ExtractOutputText(response) is { } nested) return nested;
        return TextFromOutput(payload["output"]) ?? TextFromContent(payload["content"]);
    }

    private static string? FinalAssistantAnswer(JsonObject payload)
    {
        if (payload["response"] is JsonObject response && FinalAssistantAnswer(response) is { } answer) return answer;
        var items = payload["output"] switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject single => [single],
            _ => null
        };
        var item = items?.LastOrDefault(entry => Str(entry["type"]) == "message" && Str(entry["role"]) == "assistant" && Str(entry["phase"]) == "final_answer");
        if (item is null) return null;
        var text = TextFromContent(item["content"]) ?? Str(item["text"]);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? TextFromOutput(JsonNode? output)
    {
        switch (output)
        {
            case JsonValue value when Str(value) is { Length: > 0 } text: return text;
            case JsonObject item: return Str(item["type"]) == "reasoning" ? null : TextFromContent(item["content"]) ?? Str(item["text"]);
            case JsonArray items:
                var joined = string.Concat(items.OfType<JsonObject>().Where(item => Str(item["type"]) != "reasoning")
                    .Select(item => TextFromContent(item["content"]) ?? Str(item["text"])));
                return joined.Length == 0 ? null : joined;
            default: return null;
        }
    }

    private static string? TextFromContent(JsonNode? content)
    {
        static string? Part(JsonObject item) =>
            Str(item["text"]) is { Length: > 0 } text ? text
            : Str(item["content"]) is { Length: > 0 } inner ? inner
            : item["text"] is JsonObject nested && Str(nested["value"]) is { Length: > 0 } value ? value : null;
        switch (content)
        {
            case JsonValue value when Str(value) is { Length: > 0 } text: return text;
            case JsonObject item: return Part(item);
            case JsonArray items:
                var joined = string.Concat(items.OfType<JsonObject>().Select(Part));
                return joined.Length == 0 ? null : joined;
            default: return null;
        }
    }

    internal static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>Only completed output items are replayable. Codex can omit the full output array on its
/// terminal event after sending these items individually.</summary>
public sealed class MeetingNativeResponseAccumulator
{
    private readonly List<(int? Index, JsonObject Value)> _items = [];

    public MeetingNativeToolResponse? Apply(JsonObject payload)
    {
        switch (MeetingNativeToolTransport.Str(payload["type"]))
        {
            case "response.output_item.done":
                if (payload["item"] is not JsonObject value) throw MeetingNativeToolTransportException.InvalidResponse();
                Merge(value, Index(payload["output_index"]));
                return null;
            case "response.completed":
                if (payload["response"]?.DeepClone() is not JsonObject response) throw MeetingNativeToolTransportException.InvalidResponse();
                response["status"] ??= "completed";
                // A terminal snapshot may be absent, empty, or contain only a subset of items already
                // completed by the stream. Merge rather than erase.
                if (response["output"] is { } rawOutput)
                {
                    if (rawOutput is not JsonArray output) throw MeetingNativeToolTransportException.InvalidResponse();
                    for (var index = 0; index < output.Count; index++)
                        if (output[index] is JsonObject item) Merge((JsonObject)item.DeepClone(), index);
                }
                response["output"] = OrderedOutput();
                return MeetingNativeToolTransport.ParseResponse(response);
            case "response.failed" or "response.incomplete" or "error":
                throw MeetingNativeToolTransportException.IncompleteResponse();
            default:
                return null;
        }
    }

    private static int? Index(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var index) ? index : null;

    private void Merge(JsonObject value, int? index)
    {
        if (index is < 0 or >= 256) throw MeetingNativeToolTransportException.ResponseTooLarge();
        var id = MeetingNativeToolTransport.Str(value["id"]);
        var callId = MeetingNativeToolTransport.Str(value["call_id"]);
        var type = MeetingNativeToolTransport.Str(value["type"]);
        var existing = _items.FindIndex(item =>
            (id is not null && MeetingNativeToolTransport.Str(item.Value["id"]) == id) ||
            (callId is not null && MeetingNativeToolTransport.Str(item.Value["call_id"]) == callId));
        if (existing < 0)
        {
            existing = _items.FindIndex(item =>
            {
                if (index is null || item.Index != index || MeetingNativeToolTransport.Str(item.Value["type"]) != type) return false;
                // Partial terminal arrays need not retain original positions. Never replace a different
                // explicitly identified item merely by position.
                if (id is not null && MeetingNativeToolTransport.Str(item.Value["id"]) is { } otherId && otherId != id) return false;
                if (callId is not null && MeetingNativeToolTransport.Str(item.Value["call_id"]) is { } otherCall && otherCall != callId) return false;
                return true;
            });
        }
        if (existing >= 0)
        {
            if (MeetingNativeToolTransport.Str(_items[existing].Value["type"]) != type) throw MeetingNativeToolTransportException.InvalidResponse();
            foreach (var (key, node) in value.ToList()) _items[existing].Value[key] = node?.DeepClone();
        }
        else
        {
            if (_items.Count >= 256) throw MeetingNativeToolTransportException.ResponseTooLarge();
            _items.Add((index, value));
        }
        if (Encoding.UTF8.GetByteCount(OrderedOutput().ToJsonString()) > MeetingNativeToolTransport.MaximumResponseBytes)
            throw MeetingNativeToolTransportException.ResponseTooLarge();
    }

    private JsonArray OrderedOutput() => new(_items.Select((item, offset) => (item, offset))
        .OrderBy(entry => entry.item.Index ?? entry.offset).ThenBy(entry => entry.offset)
        .Select(entry => (JsonNode)entry.item.Value.DeepClone()).ToArray());
}

/// <summary>
/// macOS ChatGPTReasoningSummaryAccumulator: collects only provider-designated reasoning summaries.
/// Raw reasoning text, encrypted reasoning and unrelated output events never enter this display channel.
/// </summary>
public sealed class ReasoningSummaryAccumulator
{
    private sealed record Part(int OutputIndex, string Text, bool IsDone);

    private readonly Dictionary<(string ItemId, int SummaryIndex), Part> _parts = [];
    private readonly HashSet<string> _completedItems = [];

    public string Text { get; private set; } = "";

    /// <summary>True when the visible summary text changed.</summary>
    public bool Apply(JsonObject payload)
    {
        if (MeetingNativeToolTransport.Str(payload["type"]) is not { } type || Int(payload["output_index"]) is not ({ } outputIndex and >= 0)) return false;
        if (type == "response.output_item.done")
        {
            if (payload["item"] is not JsonObject item || MeetingNativeToolTransport.Str(item["type"]) != "reasoning"
                || MeetingNativeToolTransport.Str(item["id"]) is not { Length: > 0 } itemId || item["summary"] is not JsonArray summary
                || !(_completedItems.Contains(itemId) || _completedItems.Count < 32)) return false;
            // The final item contains only public summary_text parts here. Never consult content
            // (raw reasoning) or encrypted_content.
            foreach (var key in _parts.Keys.Where(key => key.ItemId == itemId).ToList()) _parts.Remove(key);
            foreach (var (entry, index) in summary.Take(32).Select((entry, index) => (entry as JsonObject, index)))
            {
                if (entry is null || MeetingNativeToolTransport.Str(entry["type"]) != "summary_text" || MeetingNativeToolTransport.Str(entry["text"]) is not { } fragment) continue;
                Store(itemId, index, outputIndex, fragment, isDelta: false);
            }
            _completedItems.Add(itemId);
        }
        else
        {
            if (MeetingNativeToolTransport.Str(payload["item_id"]) is not { Length: > 0 } itemId
                || Int(payload["summary_index"]) is not ({ } index and >= 0 and < 32) || _completedItems.Contains(itemId)) return false;
            var isDelta = type == "response.reasoning_summary_text.delta";
            string? fragment = type switch
            {
                "response.reasoning_summary_text.delta" or "response.reasoning_summary_text.done" =>
                    MeetingNativeToolTransport.Str(payload[isDelta ? "delta" : "text"]),
                "response.reasoning_summary_part.done" when payload["part"] is JsonObject part
                    && MeetingNativeToolTransport.Str(part["type"]) == "summary_text" => MeetingNativeToolTransport.Str(part["text"]),
                _ => null
            };
            if (fragment is null) return false;
            Store(itemId, index, outputIndex, fragment, isDelta);
        }
        var updated = string.Join("\n\n", _parts
            .OrderBy(pair => pair.Value.OutputIndex).ThenBy(pair => pair.Key.SummaryIndex).ThenBy(pair => pair.Key.ItemId, StringComparer.Ordinal)
            .Select(pair => pair.Value.Text).Where(text => text.Length > 0));
        if (updated.Length > 6_000) updated = updated[..6_000];
        if (updated == Text) return false;
        Text = updated;
        return true;
    }

    private void Store(string itemId, int index, int outputIndex, string fragment, bool isDelta)
    {
        var key = (itemId, index);
        var previous = _parts.GetValueOrDefault(key);
        if ((previous is null && _parts.Count >= 32) || (previous is not null && previous.OutputIndex != outputIndex)
            || (isDelta && previous?.IsDone == true)) return;
        var bounded = fragment.Length > 6_000 ? fragment[..6_000] : fragment;
        var content = isDelta ? (previous?.Text ?? "") + bounded : bounded;
        if (content.Length > 6_000) content = content[..6_000];
        _parts[key] = new Part(outputIndex, content, !isDelta);
    }

    private static int? Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}
