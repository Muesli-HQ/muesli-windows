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
    public const string ChatGptSubscription = "chatgpt-subscription";

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

    public static IReadOnlyList<SummaryProviderInfo> Available { get; } =
        [LocalInfo, OllamaInfo, OpenAIInfo, OpenRouterInfo];

    public static IReadOnlyList<string> AvailableIds { get; } = Available.Select(info => info.Id).ToList();

    public static SummaryProviderInfo For(string? providerId)
    {
        var id = providerId?.Trim().ToLowerInvariant() ?? Local;
        return Available.FirstOrDefault(info => info.Id.Equals(id, StringComparison.Ordinal)) ?? LocalInfo;
    }

    /// <summary>
    /// True when this provider, as configured, will transmit the transcript to another machine.
    /// Ollama is local by default but can be pointed at a remote host, which changes the answer.
    /// </summary>
    public static bool LeavesMachine(string? providerId, string? ollamaEndpoint = null)
    {
        var info = For(providerId);
        if (info.Id != Ollama) return info.SendsTranscriptOffMachine;
        return !IsLoopbackEndpoint(ollamaEndpoint);
    }

    public static string DisclosureFor(string? providerId, string? ollamaEndpoint = null)
    {
        var info = For(providerId);
        if (info.Id == Ollama && !IsLoopbackEndpoint(ollamaEndpoint))
        {
            return "Sends the full meeting transcript to a remote Ollama server you configured. It leaves this machine.";
        }
        return info.Disclosure;
    }

    public static bool IsLoopbackEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback;
    }

    /// <summary>
    /// The ChatGPT subscription provider is deliberately absent from <see cref="Available"/>.
    /// It is not offered as a disabled row either, because an unusable control is still a promise.
    /// </summary>
    public const string ChatGptSubscriptionBlocker =
        "ChatGPT subscription sign-in is not available on Windows. The macOS build authenticates against a " +
        "private, undocumented ChatGPT desktop endpoint using a client identity issued to that application. " +
        "There is no published OAuth client, scope set, or redirect contract that a third-party Windows " +
        "application may legitimately use, and reproducing the macOS flow would mean impersonating another " +
        "application's client credentials. Muesli therefore does not offer it. Unblocking it requires an " +
        "official, documented OAuth client registration from OpenAI that permits third-party desktop use.";
}
