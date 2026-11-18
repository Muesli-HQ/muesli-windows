using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Muesli.Windows.Services;

/// <summary>The wire format of OpenAI's Realtime transcription session (macOS <c>OpenAIRealtimeProtocol</c>).</summary>
public static class OpenAIRealtimeProtocol
{
    public static readonly Uri Endpoint = new("wss://api.openai.com/v1/realtime?intent=transcription");
    public const string DefaultModel = "gpt-live-transcribe";
    public const string Commit = """{"type":"input_audio_buffer.commit"}""";

    /// <summary>The macOS presets: the streaming model, then the one that transcribes after the turn is committed.</summary>
    public static IReadOnlyList<string> ModelPresets { get; } = ["gpt-live-transcribe", "gpt-transcribe"];

    public static string NormalizeModel(string? model)
    {
        var trimmed = model?.Trim() ?? "";
        return trimmed.Length == 0 ? DefaultModel : trimmed;
    }

    /// <summary>A transcription-only session taking 24 kHz PCM16, with the turn ended by our commit rather than server VAD.</summary>
    public static string SessionUpdate(string? model) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["type"] = "session.update",
        ["session"] = new Dictionary<string, object?>
        {
            ["type"] = "transcription",
            ["audio"] = new Dictionary<string, object?>
            {
                ["input"] = new Dictionary<string, object?>
                {
                    ["format"] = new Dictionary<string, object?> { ["type"] = "audio/pcm", ["rate"] = 24_000 },
                    ["transcription"] = new Dictionary<string, object?> { ["model"] = NormalizeModel(model) },
                    ["turn_detection"] = null
                }
            }
        }
    });

    public static string Append(ReadOnlySpan<byte> pcm16) =>
        "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + Convert.ToBase64String(pcm16) + "\"}";
}

/// <summary>
/// 16 kHz float samples to 24 kHz signed 16-bit PCM by linear interpolation. The last source
/// sample and the fractional position carry over between calls, so the output is identical to
/// converting one continuous buffer (macOS <c>OpenAIRealtimePCMEncoder</c>).
/// </summary>
public sealed class OpenAIRealtimePcmEncoder
{
    private float? _previous;
    // Next output position in thirds of a source sample; 16 -> 24 kHz advances 2/3 per output.
    private int _nextPositionThirds;

    public byte[] Encode(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return [];
        var offset = _previous is null ? 0 : 1;
        var source = new float[samples.Length + offset];
        if (_previous is { } previous) source[0] = previous;
        samples.CopyTo(source.AsSpan(offset));

        var end = (source.Length - 1) * 3;
        var output = new byte[Math.Max(0, (end - _nextPositionThirds + 1) / 2) * 2];
        var written = 0;
        var position = _nextPositionThirds;
        while (position < end)
        {
            var lower = position / 3;
            var fraction = position % 3 / 3f;
            var sample = source[lower] + (source[lower + 1] - source[lower]) * fraction;
            var scaled = Math.Clamp(sample, -1f, 1f) * (sample < 0 ? 32768f : 32767f);
            var pcm = (short)MathF.Round(scaled);
            output[written++] = (byte)pcm;
            output[written++] = (byte)(pcm >> 8);
            position += 2;
        }

        _nextPositionThirds = position - end;
        _previous = samples[^1];
        return written == output.Length ? output : output[..written];
    }
}

/// <summary>The socket the Realtime session talks through, so tests can stand in for the service.</summary>
public interface IRealtimeSocket : IDisposable
{
    Task ConnectAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken);
    Task SendTextAsync(string message, CancellationToken cancellationToken);

    /// <summary>The next complete text message, or null once the peer has closed.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
    void Abort();
}

public sealed class ClientWebSocketRealtimeSocket : IRealtimeSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken)
    {
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        return _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public Task SendTextAsync(string message, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new System.IO.MemoryStream();
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Abort() => _socket.Abort();
    public void Dispose() => _socket.Dispose();
}

/// <summary>
/// One dictation streamed to OpenAI's Realtime transcription service (macOS
/// <c>OpenAIRealtimeTranscriptionSession</c> and <c>OpenAIRealtimeDictationStream</c>). The
/// connection is opened when recording starts; audio is sent as it is captured, committed when
/// recording stops, and the session waits for the completed transcript.
/// </summary>
public sealed class OpenAIRealtimeDictationSession : IHostedDictationSession
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    // Audio waits here while the connection opens. Past this much, the network is not keeping up
    // and the recording is transcribed locally instead of holding ever more audio in memory.
    internal const int MaxQueuedSamples = 16_000 * 30;
    private const int MaxSamplesPerMessage = 16_000;

    private readonly string _apiKey;
    private readonly string _model;
    private readonly IRealtimeSocket _socket;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _finalTimeout;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Channel<float[]> _audio = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Dictionary<string, StringBuilder> _transcripts = new(StringComparer.Ordinal);
    private readonly List<string> _completedItems = [];
    private readonly Task _upload;
    private int _queuedSamples;
    private volatile bool _overflowed;
    private volatile bool _commitRequested;
    private int _disposed;

    public OpenAIRealtimeDictationSession(
        string apiKey,
        string? model,
        Func<IRealtimeSocket>? socketFactory = null,
        TimeSpan? readyTimeout = null,
        TimeSpan? finalTimeout = null)
    {
        _apiKey = apiKey?.Trim() ?? "";
        _model = OpenAIRealtimeProtocol.NormalizeModel(model);
        _socket = (socketFactory ?? (() => new ClientWebSocketRealtimeSocket()))();
        _readyTimeout = readyTimeout ?? DefaultTimeout;
        _finalTimeout = finalTimeout ?? DefaultTimeout;
        // Observed here so a failure nobody waits for (a cancelled dictation) is not unobserved.
        _ = _ready.Task.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        _ = _final.Task.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        _upload = Task.Run(RunAsync);
        _ = _upload.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
    }

    public string Provider => DictationProviders.OpenAI;
    public bool AcceptsLiveAudio => true;

    public void Append(float[] samples)
    {
        if (samples.Length == 0 || _overflowed) return;
        if (Interlocked.Add(ref _queuedSamples, samples.Length) > MaxQueuedSamples)
        {
            _overflowed = true;
            return;
        }

        _audio.Writer.TryWrite(samples);
    }

    public async Task<HostedDictationResult> FinishAsync(string recordedWavPath, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        _audio.Writer.TryComplete();
        try
        {
            await _upload.WaitAsync(linked.Token).ConfigureAwait(false);
            if (_overflowed)
                throw new HostedDictationException("The network could not keep up with microphone audio.", "audio-backpressure");

            // A failure the service already reported is more useful than the send error that follows it.
            if (_final.Task.IsFaulted) await _final.Task.ConfigureAwait(false);

            _commitRequested = true;
            await _socket.SendTextAsync(OpenAIRealtimeProtocol.Commit, linked.Token).ConfigureAwait(false);
            string text;
            try
            {
                text = await _final.Task.WaitAsync(_finalTimeout, linked.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new HostedDictationException("OpenAI did not finish the transcript in time.", "timeout");
            }

            return new HostedDictationResult(text, "openai-realtime", _model, OpenAIRealtimeProtocol.Endpoint.AbsoluteUri);
        }
        catch (Exception exception) when (
            exception is not HostedDictationException && exception is not OperationCanceledException)
        {
            throw new HostedDictationException("Could not reach OpenAI.", "network", exception);
        }
        finally
        {
            _socket.Abort();
        }
    }

    public void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        _audio.Writer.TryComplete();
        _socket.Abort();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Cancel();
        var cancelled = new OperationCanceledException();
        _ready.TrySetException(cancelled);
        _final.TrySetException(cancelled);
        _socket.Dispose();
        _cancellation.Dispose();
    }

    /// <summary>Connects, configures the session, then sends audio until recording stops.</summary>
    private async Task RunAsync()
    {
        var token = _cancellation.Token;
        if (_apiKey.Length == 0)
            throw Fail(new HostedDictationException("OpenAI API key is not configured.", "missing-key"));
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(_readyTimeout);
                try
                {
                    await _socket.ConnectAsync(OpenAIRealtimeProtocol.Endpoint, _apiKey, connect.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new HostedDictationException("OpenAI did not answer in time.", "timeout");
                }
            }

            _ = Task.Run(() => ReceiveLoopAsync(token), CancellationToken.None);
            try
            {
                await _ready.Task.WaitAsync(_readyTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new HostedDictationException("OpenAI did not answer in time.", "timeout");
            }

            var encoder = new OpenAIRealtimePcmEncoder();
            var batch = new List<float>(MaxSamplesPerMessage);
            while (await _audio.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < MaxSamplesPerMessage && _audio.Reader.TryRead(out var samples))
                {
                    Interlocked.Add(ref _queuedSamples, -samples.Length);
                    batch.AddRange(samples);
                }

                var pcm = encoder.Encode(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch));
                if (pcm.Length > 0)
                    await _socket.SendTextAsync(OpenAIRealtimeProtocol.Append(pcm), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HostedDictationException exception)
        {
            throw Fail(exception);
        }
        catch (Exception exception)
        {
            throw Fail(new HostedDictationException("Could not reach OpenAI.", "network", exception));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await _socket.ReceiveTextAsync(token).ConfigureAwait(false);
                if (message is null)
                {
                    Fail(new HostedDictationException("OpenAI closed the connection.", "connection-closed"));
                    return;
                }

                await HandleAsync(message, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HostedDictationException exception)
        {
            Fail(exception);
        }
        catch (Exception exception)
        {
            // Aborting the socket once the transcript is in also lands here; Fail is then a no-op.
            Fail(new HostedDictationException("Could not reach OpenAI.", "network", exception));
        }
    }

    private async Task HandleAsync(string message, CancellationToken token)
    {
        string? type, itemId, transcript, delta, errorMessage = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            type = Text(root, "type");
            itemId = Text(root, "item_id");
            transcript = Text(root, "transcript");
            delta = Text(root, "delta");
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                errorMessage = Text(error, "message") ?? Text(error, "code");
            }
        }
        catch (JsonException exception)
        {
            throw new HostedDictationException("OpenAI sent a message that could not be read.", "malformed-response", exception);
        }

        switch (type)
        {
            case "session.created":
                await _socket.SendTextAsync(OpenAIRealtimeProtocol.SessionUpdate(_model), token).ConfigureAwait(false);
                break;
            case "session.updated":
                _ready.TrySetResult();
                break;
            case "conversation.item.input_audio_transcription.delta" when itemId is not null && delta is not null:
                lock (_gate) Transcript(itemId).Append(delta);
                break;
            case "conversation.item.input_audio_transcription.completed" when itemId is not null:
                lock (_gate)
                {
                    if (transcript is not null) Transcript(itemId).Clear().Append(transcript);
                    if (!_completedItems.Contains(itemId)) _completedItems.Add(itemId);
                }

                ResolveFinal();
                break;
            case "conversation.item.input_audio_transcription.failed":
                throw new HostedDictationException(
                    $"OpenAI transcription failed: {Short(errorMessage) ?? "the audio could not be transcribed"}", "transcription-failed");
            case "error":
                throw new HostedDictationException(
                    $"OpenAI transcription failed: {Short(errorMessage) ?? "unknown server error"}", "server-error");
        }
    }

    private void ResolveFinal()
    {
        if (!_commitRequested) return;
        string text;
        lock (_gate)
        {
            text = string.Join(' ', _completedItems
                .Select(id => _transcripts.TryGetValue(id, out var value) ? value.ToString() : "")
                .Where(part => part.Length > 0)).Trim();
        }

        if (text.Length == 0)
            _final.TrySetException(new HostedDictationException("OpenAI returned an empty transcript.", "empty-transcript"));
        else
            _final.TrySetResult(text);
    }

    private StringBuilder Transcript(string itemId)
    {
        if (!_transcripts.TryGetValue(itemId, out var builder)) _transcripts[itemId] = builder = new StringBuilder();
        return builder;
    }

    /// <summary>Records the first failure for whoever is waiting on the session, and returns it to throw.</summary>
    private HostedDictationException Fail(HostedDictationException exception)
    {
        _ready.TrySetException(exception);
        _final.TrySetException(exception);
        return exception;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Short(string? message)
    {
        var collapsed = string.Join(' ', (message ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed[..Math.Min(collapsed.Length, 240)];
    }
}
