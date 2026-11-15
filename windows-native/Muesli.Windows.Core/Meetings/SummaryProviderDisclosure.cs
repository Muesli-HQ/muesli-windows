namespace Muesli.Windows.Services;

public sealed record SummaryProviderInfo(
    string Id,
    string DisplayName,
    bool SendsTranscriptOffMachine,
    bool RequiresApiKey,
    string Disclosure);

/// <summary>
/// Single source of truth for what each notes provider does with the transcript.
///
/// The product rule is that a user must be told, before it happens, when the full meeting
/// transcript leaves their machine. Deciding that per call site invites drift, so every surface —
/// settings, the summary runner, and the tests — reads it from here.
/// </summary>
public static class SummaryProviderDisclosure
{
    public const string Local = "local";
    public const string OpenAI = "openai";
    public const string OpenRouter = "openrouter";
    public const string Ollama = "ollama";
    public const string LmStudio = "lmstudio";
    public const string CustomLlm = "custom";
    public const string ChatGpt = "chatgpt";
    public const string ClaudeCode = "claude_code";

    private static readonly SummaryProviderInfo LocalInfo = new(
        Local,
        "Local (deterministic)",
        SendsTranscriptOffMachine: false,
        RequiresApiKey: false,
        "Runs entirely on this machine. Nothing is uploaded.");

    private static readonly SummaryProviderInfo OllamaInfo = new(
        Ollama,
        "Ollama (local model)",
        SendsTranscriptOffMachine: false,
        RequiresApiKey: false,
        "Sends the transcript to Ollama on this machine. Nothing leaves your computer while the endpoint stays on localhost.");

    private static readonly SummaryProviderInfo LmStudioInfo = new(
        LmStudio,
        "LM Studio (local server)",
        SendsTranscriptOffMachine: false,
        RequiresApiKey: false,
        "Sends the transcript to an OpenAI-compatible LM Studio server on this machine. Nothing leaves your computer while the endpoint stays on localhost.");

    private static readonly SummaryProviderInfo CustomLlmInfo = new(
        CustomLlm,
        "Custom HTTP (OpenAI-compatible)",
        SendsTranscriptOffMachine: false,
        RequiresApiKey: false,
        "Sends the transcript to the OpenAI-compatible HTTP endpoint you configured. Keep it on localhost to keep the transcript on this machine.");

    private static readonly SummaryProviderInfo OpenAIInfo = new(
        OpenAI,
        "OpenAI API",
        SendsTranscriptOffMachine: true,
        RequiresApiKey: true,
        "Uploads the full meeting transcript to the OpenAI API over HTTPS.");

    private static readonly SummaryProviderInfo OpenRouterInfo = new(
        OpenRouter,
        "OpenRouter API",
        SendsTranscriptOffMachine: true,
        RequiresApiKey: true,
        "Uploads the full meeting transcript to OpenRouter over HTTPS, which routes it to the model you select.");

    private static readonly SummaryProviderInfo ChatGptInfo = new(
        ChatGpt,
        "ChatGPT",
        SendsTranscriptOffMachine: true,
        RequiresApiKey: false,
        "Uses your ChatGPT subscription. Uploads the meeting transcript and your notes to ChatGPT over HTTPS.");

    private static readonly SummaryProviderInfo ClaudeCodeInfo = new(
        ClaudeCode,
        "Claude Code",
        SendsTranscriptOffMachine: true,
        RequiresApiKey: false,
        "Uses the Claude Code sign-in on this PC. Meeting transcripts and notes go through your Claude account or configured proxy; the model does not run on this PC.");

    /// <summary>The macOS order for the account-backed providers, then the API and local ones.</summary>
    public static IReadOnlyList<SummaryProviderInfo> Available { get; } =
        [LocalInfo, ChatGptInfo, ClaudeCodeInfo, OpenAIInfo, OpenRouterInfo, OllamaInfo, LmStudioInfo, CustomLlmInfo];

    public static IReadOnlyList<string> AvailableIds { get; } = Available.Select(info => info.Id).ToList();

    public static SummaryProviderInfo For(string? providerId)
    {
        var id = providerId?.Trim().ToLowerInvariant() ?? Local;
        return Available.FirstOrDefault(info => info.Id.Equals(id, StringComparison.Ordinal)) ?? LocalInfo;
    }

    /// <summary>
    /// True when this provider, as configured, will transmit the transcript to another machine.
    /// Ollama, LM Studio, and the custom HTTP endpoint are local by default but can be pointed at
    /// a remote host, which changes the answer.
    /// </summary>
    public static bool LeavesMachine(
        string? providerId,
        string? ollamaEndpoint = null,
        string? lmStudioEndpoint = null,
        string? customEndpoint = null)
    {
        var info = For(providerId);
        return info.Id switch
        {
            Ollama => !IsLoopbackEndpoint(ollamaEndpoint),
            LmStudio => !IsLoopbackEndpoint(lmStudioEndpoint),
            CustomLlm => !IsLoopbackEndpoint(customEndpoint),
            _ => info.SendsTranscriptOffMachine
        };
    }

    public static string DisclosureFor(
        string? providerId,
        string? ollamaEndpoint = null,
        string? lmStudioEndpoint = null,
        string? customEndpoint = null)
    {
        var info = For(providerId);
        switch (info.Id)
        {
            case Ollama when !IsLoopbackEndpoint(ollamaEndpoint):
                return "Sends the full meeting transcript to a remote Ollama server you configured. It leaves this machine.";
            case LmStudio when !IsLoopbackEndpoint(lmStudioEndpoint):
                return "Sends the full meeting transcript to a remote LM Studio server you configured. It leaves this machine.";
            case LmStudio:
                return "Sends the transcript to LM Studio on this machine. Nothing leaves your computer while the endpoint stays on localhost.";
            case CustomLlm when !IsLoopbackEndpoint(customEndpoint):
                return "Sends the full meeting transcript to the OpenAI-compatible endpoint you configured. It leaves this machine.";
            case CustomLlm:
                return "Sends the transcript to the OpenAI-compatible endpoint on this machine. Nothing leaves your computer while the endpoint stays on localhost.";
            default:
                return info.Disclosure;
        }
    }

    public static bool IsLoopbackEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback;
    }
}
