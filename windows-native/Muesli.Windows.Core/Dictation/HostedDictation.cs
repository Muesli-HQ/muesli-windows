using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>
/// Who transcribes dictation (macOS <c>DictationProvider</c>). Local is the default; a hosted
/// provider uses the user's own key and receives the recorded audio. The local model selection is
/// kept so a failed hosted request can fall back to it and the user can switch back at any time.
/// </summary>
public static class DictationProviders
{
    public const string Local = "local";
    public const string OpenAI = "openai";
    public const string OpenRouter = "openrouter";

    public static IReadOnlyList<string> All { get; } = [Local, OpenAI, OpenRouter];

    /// <summary>Anything unknown or missing is local: no audio leaves the device by accident.</summary>
    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        OpenAI => OpenAI,
        OpenRouter => OpenRouter,
        _ => Local
    };

    public static bool IsHosted(string? value) => Normalize(value) != Local;

    public static string Label(string? value) => Normalize(value) switch
    {
        OpenAI => "OpenAI",
        OpenRouter => "OpenRouter",
        _ => "Local"
    };

    /// <summary>What the user is told happens to their audio. Shown beside the provider picker.</summary>
    public static string Disclosure(string? value) => Normalize(value) switch
    {
        OpenAI => "Your dictation audio is sent to OpenAI over the internet while you speak, using your OpenAI API key. " +
                  "If OpenAI fails, the recording is transcribed on this PC with the fallback model.",
        OpenRouter => "Your dictation audio is sent to OpenRouter over the internet after you stop speaking, using your OpenRouter API key. " +
                      "OpenRouter passes it to the model you choose. If it fails, the recording is transcribed on this PC with the fallback model.",
        _ => "Dictation is transcribed on this PC. No audio leaves your computer."
    };
}

public sealed record HostedDictationResult(string Text, string Backend, string Model, string Endpoint);

/// <summary>A hosted provider could not produce a transcript. The message is safe to show and log.</summary>
public sealed class HostedDictationException(string message, string category, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>A fixed word for logs: never provider text, a key, or anything the user said.</summary>
    public string Category { get; } = category;
}

/// <summary>
/// One dictation's worth of hosted transcription (macOS <c>HostedDictationSession</c>). Created
/// when recording starts and finished with the recorded WAV, so a provider may either stream the
/// audio as it is captured or upload the file afterwards.
/// </summary>
public interface IHostedDictationSession : IDisposable
{
    string Provider { get; }
    bool AcceptsLiveAudio { get; }

    /// <summary>16 kHz mono samples, called on the audio thread. Must not block.</summary>
    void Append(float[] samples);

    Task<HostedDictationResult> FinishAsync(string recordedWavPath, CancellationToken cancellationToken);
    void Cancel();
}

/// <summary>macOS <c>HostedDictationActivationPolicy</c>: what stops a hosted dictation before the microphone opens.</summary>
public static class HostedDictationActivationPolicy
{
    // "unavailable" is one of the words the floating pill shows as a warning.
    public static string? BlockingMessage(
        string? provider, string? openAIApiKey, string? openRouterApiKey, string? openRouterModel) =>
        DictationProviders.Normalize(provider) switch
        {
            DictationProviders.OpenAI when string.IsNullOrWhiteSpace(openAIApiKey) =>
                "OpenAI dictation unavailable: add an OpenAI API key in Settings → Dictation",
            DictationProviders.OpenRouter when string.IsNullOrWhiteSpace(openRouterApiKey) =>
                "OpenRouter dictation unavailable: add an OpenRouter API key in Settings → Dictation",
            DictationProviders.OpenRouter when string.IsNullOrWhiteSpace(openRouterModel) =>
                "OpenRouter dictation unavailable: choose a transcription model in Settings → Dictation",
            _ => null
        };
}

/// <summary>macOS <c>HostedDictationFallbackPolicy</c>: every failure falls back except the user cancelling.</summary>
public static class HostedDictationFallbackPolicy
{
    public static bool ShouldFallback(Exception error, bool cancellationRequested) =>
        !cancellationRequested && error is not OperationCanceledException;
}

/// <summary>
/// Runs a hosted session to its transcript and, when it fails, transcribes the same recording
/// locally. The first diagnostic line says which of the two produced the text.
/// </summary>
public static class HostedDictationTranscriber
{
    public const string HostedDiagnosticPrefix = "Hosted ASR: ";
    public const string FallbackDiagnosticPrefix = "Hosted fallback: ";

    /// <param name="localFallback">Transcribes the recording with the selected local model; null when that model is not installed.</param>
    public static async Task<TranscriptionResult> TranscribeAsync(
        IHostedDictationSession session,
        string recordedWavPath,
        Func<Task<TranscriptionResult>>? localFallback,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        try
        {
            var hosted = await session.FinishAsync(recordedWavPath, cancellationToken).ConfigureAwait(false);
            return new TranscriptionResult(
                hosted.Text,
                $"{HostedDiagnosticPrefix}{hosted.Backend}; model={hosted.Model}; endpoint={hosted.Endpoint}");
        }
        catch (Exception exception) when (
            HostedDictationFallbackPolicy.ShouldFallback(exception, cancellationToken.IsCancellationRequested))
        {
            var category = exception is HostedDictationException hosted ? hosted.Category : exception.GetType().Name;
            if (localFallback is null)
            {
                log($"Hosted dictation failed and no local model is installed to fall back to. provider={session.Provider}; reason={category}");
                throw exception is HostedDictationException
                    ? exception
                    : new HostedDictationException($"{DictationProviders.Label(session.Provider)} transcription failed.", category, exception);
            }

            log($"Hosted dictation failed; transcribing the recording locally. provider={session.Provider}; reason={category}");
            var local = await localFallback().ConfigureAwait(false);
            return local with
            {
                Diagnostic = $"{FallbackDiagnosticPrefix}{session.Provider}; reason={category}" +
                             (string.IsNullOrEmpty(local.Diagnostic) ? "" : Environment.NewLine + local.Diagnostic)
            };
        }
    }

    /// <summary>True when the hosted provider produced this text, so the local cleanup pipeline is skipped (as on macOS).</summary>
    public static bool IsHostedResult(TranscriptionResult result) =>
        result.Diagnostic?.StartsWith(HostedDiagnosticPrefix, StringComparison.Ordinal) == true;

    public static bool IsFallbackResult(TranscriptionResult result) =>
        result.Diagnostic?.StartsWith(FallbackDiagnosticPrefix, StringComparison.Ordinal) == true;

    /// <summary>What history records as the model of a hosted dictation, e.g. <c>openai-realtime:gpt-live-transcribe</c>.</summary>
    public static string ModelLabel(TranscriptionResult result)
    {
        var line = (result.Diagnostic ?? "").Split('\n')[0].TrimEnd('\r');
        if (!line.StartsWith(HostedDiagnosticPrefix, StringComparison.Ordinal)) return "";
        var fields = line[HostedDiagnosticPrefix.Length..].Split("; ");
        var model = fields.FirstOrDefault(field => field.StartsWith("model=", StringComparison.Ordinal))?["model=".Length..];
        return string.IsNullOrEmpty(model) ? fields[0] : $"{fields[0]}:{model}";
    }
}

/// <summary>Builds the hosted session the saved settings ask for. Local settings never produce one.</summary>
public static class HostedDictationSessions
{
    public static string? BlockingMessage(MuesliSettings settings) =>
        HostedDictationActivationPolicy.BlockingMessage(
            settings.DictationProvider,
            settings.ResolvedOpenAIApiKey,
            settings.ResolvedOpenRouterApiKey,
            settings.OpenRouterDictationModel);

    public static IHostedDictationSession? Create(
        MuesliSettings settings,
        Func<IRealtimeSocket>? socketFactory = null,
        OpenRouterTranscriptionClient? openRouterClient = null) =>
        DictationProviders.Normalize(settings.DictationProvider) switch
        {
            DictationProviders.OpenAI => new OpenAIRealtimeDictationSession(
                settings.ResolvedOpenAIApiKey, settings.OpenAIDictationModel, socketFactory),
            DictationProviders.OpenRouter => new OpenRouterHostedDictationSession(
                settings.ResolvedOpenRouterApiKey, settings.OpenRouterDictationModel, openRouterClient),
            _ => null
        };
}

/// <summary>
/// OpenRouter speech-to-text: one JSON request after recording stops, carrying the whole WAV as
/// base64 (macOS <c>OpenRouterTranscriptionClient</c>).
/// </summary>
public sealed class OpenRouterTranscriptionClient(HttpClient? httpClient = null)
{
    public static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/audio/transcriptions");
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(65);

    // The 65 s limit is applied per request so cancellation and a timeout stay distinguishable.
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http = httpClient ?? SharedHttp;

    public static string NormalizeModel(string? model) => model?.Trim() ?? "";

    public static HttpRequestMessage BuildRequest(byte[] wavBytes, string apiKey, string model)
    {
        var key = apiKey?.Trim() ?? "";
        if (key.Length == 0)
            throw new HostedDictationException("OpenRouter API key is not configured.", "missing-key");
        var normalized = NormalizeModel(model);
        if (normalized.Length == 0)
            throw new HostedDictationException("No OpenRouter transcription model is chosen.", "missing-model");

        var body = new Dictionary<string, object?>
        {
            ["model"] = normalized,
            ["input_audio"] = new Dictionary<string, object?>
            {
                ["data"] = Convert.ToBase64String(wavBytes),
                ["format"] = "wav"
            }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "Muesli Windows");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<string> TranscribeAsync(
        string wavPath, string apiKey, string model, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] audio;
        try
        {
            audio = await File.ReadAllBytesAsync(wavPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HostedDictationException("The recording could not be read.", "recording-unreadable", exception);
        }

        using var request = BuildRequest(audio, apiKey, model);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? RequestTimeout);
        try
        {
            using var response = await _http.SendAsync(request, limit.Token).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var detail = ServerMessage(payload);
                throw new HostedDictationException(
                    detail is null
                        ? $"OpenRouter transcription failed (HTTP {status})."
                        : $"OpenRouter transcription failed (HTTP {status}): {detail}",
                    $"http-{status}");
            }

            string? text;
            try
            {
                using var document = JsonDocument.Parse(payload);
                text = document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException exception)
            {
                throw new HostedDictationException("OpenRouter returned a response that could not be read.", "malformed-response", exception);
            }

            text = text?.Trim();
            return string.IsNullOrEmpty(text)
                ? throw new HostedDictationException("OpenRouter returned an empty transcript.", "empty-transcript")
                : text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostedDictationException("OpenRouter did not answer in time.", "timeout");
        }
        catch (HttpRequestException exception)
        {
            throw new HostedDictationException("Could not reach OpenRouter.", "network", exception);
        }
    }

    /// <summary>The provider's own error text, collapsed to one short line. Shown to the user, never logged.</summary>
    private static string? ServerMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? raw = null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.String)
            {
                raw = nested.GetString();
            }
            else if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                raw = message.GetString();
            }

            var collapsed = string.Join(' ', (raw ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length == 0 ? null : collapsed[..Math.Min(collapsed.Length, 240)];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class OpenRouterHostedDictationSession(
    string apiKey, string model, OpenRouterTranscriptionClient? client = null) : IHostedDictationSession
{
    private readonly OpenRouterTranscriptionClient _client = client ?? new OpenRouterTranscriptionClient();
    private readonly CancellationTokenSource _cancellation = new();

    public string Provider => DictationProviders.OpenRouter;
    public bool AcceptsLiveAudio => false;

    public void Append(float[] samples)
    {
    }

    public async Task<HostedDictationResult> FinishAsync(string recordedWavPath, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        var text = await _client.TranscribeAsync(recordedWavPath, apiKey, model, linked.Token).ConfigureAwait(false);
        return new HostedDictationResult(
            text, "openrouter-stt", OpenRouterTranscriptionClient.NormalizeModel(model),
            OpenRouterTranscriptionClient.Endpoint.AbsoluteUri);
    }

    public void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Cancel();
        _cancellation.Dispose();
    }
}
