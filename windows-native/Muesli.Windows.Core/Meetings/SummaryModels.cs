namespace Muesli.Windows.Services;

/// <summary>macOS SummaryModelPreset (2f10b23d). The first entry is the default an empty setting means.</summary>
public sealed record SummaryModelPreset(string Id, string Label)
{
    public static IReadOnlyList<SummaryModelPreset> OpenAIModels { get; } =
    [
        new("gpt-6.1-sol", "GPT-6.1 Sol (default)"), new("gpt-6-astra", "GPT-6 Astra"), new("gpt-6-sol", "GPT-6 Sol"),
        new("gpt-6-luna", "GPT-6 Luna"), new("chat-latest", "Chat Latest (Instant)")
    ];

    public static IReadOnlyList<SummaryModelPreset> ChatGptModels { get; } =
    [
        new("gpt-6.1-sol", "GPT-6.1 Sol (default)"), new("gpt-6-astra", "GPT-6 Astra"), new("gpt-6-sol", "GPT-6 Sol"),
        new("gpt-6-luna", "GPT-6 Luna")
    ];

    /// <summary>The preset list a provider's model menu offers; empty for free-text providers.</summary>
    public static IReadOnlyList<SummaryModelPreset> For(string provider) => provider switch
    {
        "chatgpt" => ChatGptModels,
        "openai" => OpenAIModels,
        _ => []
    };
}

/// <summary>
/// macOS ReasoningEffortPolicy: reasoning levels shared by OpenAI API and ChatGPT requests. An unsupported
/// preference falls back to the model's default instead of sending an invalid API value.
/// </summary>
public static class ReasoningEffortPolicy
{
    private static readonly string[] Standard = ["none", "low", "medium", "high", "xhigh"];
    private static readonly string[] Wide = ["none", "low", "medium", "high", "xhigh", "max"];
    private static readonly string[] Sol = ["low", "medium", "high", "xhigh", "max"];

    private static (string[] Efforts, string Default)? Capabilities(string model) => model.Trim() switch
    {
        "gpt-5.4-mini" or "gpt-5.4" or "gpt-5.4-nano" or "gpt-5.2" => (Standard, "none"),
        "gpt-5.4-pro" => (["medium", "high", "xhigh"], "medium"),
        "gpt-6-astra" => (Sol, "high"),
        "gpt-6.1-sol" => (Sol, "medium"),
        "gpt-6-sol" or "gpt-6-luna" => (Wide, "medium"),
        "gpt-5.6-sol" or "gpt-5.6-terra" or "gpt-5.6-luna" => (Wide, "high"),
        "gpt-5-mini" => (["minimal", "low", "medium", "high"], "medium"),
        _ => null
    };

    public static IReadOnlyList<string> SelectableEfforts(string model) => Capabilities(model)?.Efforts ?? [];

    /// <summary>The API value to send, or null when the model takes no reasoning parameter.</summary>
    public static string? Resolved(string model, string? preferred)
    {
        if (Capabilities(model) is not { } capabilities) return null;
        return preferred is { Length: > 0 } && capabilities.Efforts.Contains(preferred) ? preferred : capabilities.Default;
    }

    public static string Label(string effort) => effort switch
    {
        "none" => "None",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra High",
        "max" => "Maximum",
        _ => effort
    };
}

/// <summary>macOS MeetingTextGenerationClient.model and the summary_thinking model lookup.</summary>
public static class MeetingSummaryModels
{
    public static string Model(MuesliSettings settings) => settings.MeetingSummaryProvider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => settings.ChatGptSummaryModel is { Length: > 0 } model ? model : SummaryModelPreset.ChatGptModels[0].Id,
        "openai" => settings.OpenAIModel is { Length: > 0 } model ? model : SummaryModelPreset.OpenAIModels[0].Id,
        "openrouter" => settings.OpenRouterModel,
        "ollama" => settings.OllamaModel,
        "lmstudio" => settings.LmStudioModel,
        "custom" => settings.CustomLlmModel,
        _ => ""
    };

    /// <summary>The model whose reasoning levels apply; OpenRouter OpenAI models use the bare model name.</summary>
    public static string PolicyModel(MuesliSettings settings)
    {
        var provider = settings.MeetingSummaryProvider.Trim().ToLowerInvariant();
        var model = Model(settings);
        return provider == "openrouter" && model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ? model["openai/".Length..] : model;
    }

    /// <summary>The reasoning effort meeting summaries and Ask Meetings send, or null for none.</summary>
    public static string? Effort(MuesliSettings settings) =>
        ReasoningEffortPolicy.Resolved(PolicyModel(settings), settings.MeetingSummaryReasoningEffort);
}
