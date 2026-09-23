using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

public sealed class TranscriptionWorkerClient : IDisposable
{
    internal static readonly TimeSpan LongFileThreshold = TimeSpan.FromMinutes(8);
    internal static readonly TimeSpan ChunkDuration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerEnvelope>> _pending = new();
    private Process? _worker;
    private int _requestId;
    private string _stderr = "";

    public async Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes, TranscriptionOptions options)
    {
        return await RequestTranscriptionAsync(new WorkerPayload(
            "Dictation",
            "memory://dictation.wav",
            "microphone",
            options.AsrEngine,
            options.ModelProfile,
            "final",
            options.LanguageHint,
            Convert.ToBase64String(audioBytes)));
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string title, string filePath, TranscriptionOptions options)
    {
        if (ShouldChunkWaveFile(filePath))
        {
            return await TranscribeWaveFileInChunksAsync(title, filePath, options);
        }

        return await TranscribeFileDirectAsync(title, filePath, options);
    }

    internal static bool ShouldChunkWaveFile(string filePath)
    {
        if (!Path.GetExtension(filePath).Equals(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return WaveFileUtilities.GetDuration(filePath) > LongFileThreshold;
        }
        catch
        {
            return false;
        }
    }

    private async Task<TranscriptionResult> TranscribeFileDirectAsync(
        string title,
        string filePath,
        TranscriptionOptions options)
    {
        return await RequestTranscriptionAsync(new WorkerPayload(
            title,
            filePath,
            "file",
            options.AsrEngine,
            options.ModelProfile,
            "final",
            options.LanguageHint,
            ""));
    }

    private async Task<TranscriptionResult> TranscribeWaveFileInChunksAsync(
        string title,
        string filePath,
        TranscriptionOptions options)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"muesli-transcription-{Guid.NewGuid():N}");
        var combinedText = new List<string>();
        var combinedSegments = new List<TranscriptSegment>();
        var diagnostics = new List<string>
        {
            $"Long recording split into {ChunkDuration.TotalMinutes:0}-minute chunks."
        };
        var durationMs = 0;

        try
        {
            var chunks = WaveFileUtilities.CreateChunks(filePath, temporaryDirectory, ChunkDuration);
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                try
                {
                    var result = await TranscribeFileDirectAsync(
                        $"{title} part {index + 1} of {chunks.Count}",
                        chunk.Path,
                        options);
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        combinedText.Add(result.Text.Trim());
                    }

                    foreach (var segment in result.Segments ?? [])
                    {
                        combinedSegments.Add(segment with
                        {
                            Id = $"chunk_{index + 1}_{segment.Id}",
                            StartMs = SafeAdd(segment.StartMs, chunk.OffsetMs),
                            EndMs = SafeAdd(segment.EndMs, chunk.OffsetMs)
                        });
                    }

                    durationMs = Math.Max(durationMs, SafeAdd(result.DurationMs, chunk.OffsetMs));
                    if (!string.IsNullOrWhiteSpace(result.Diagnostic))
                    {
                        diagnostics.Add($"Chunk {index + 1}: {result.Diagnostic}");
                    }
                }
                catch (Exception exception)
                {
                    diagnostics.Add($"Chunk {index + 1} failed: {exception.Message}");
                    if (combinedSegments.Count == 0 && combinedText.Count == 0)
                    {
                        throw;
                    }

                    break;
                }
            }

            return new TranscriptionResult(
                string.Join(" ", combinedText),
                string.Join(Environment.NewLine, diagnostics),
                durationMs,
                combinedSegments);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
            catch
            {
                // Temporary chunk cleanup is best-effort.
            }
        }
    }

    private static int SafeAdd(int value, int offset) =>
        (int)Math.Min(int.MaxValue, Math.Max(0L, (long)value + offset));

    public async Task<PostProcessingResult> PostProcessAsync(string text, string context, string systemPrompt = "")
    {
        EnsureWorker();

        if (_worker?.StandardInput is null)
        {
            throw new InvalidOperationException("Transcription worker did not start.");
        }

        var id = $"post_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _requestId)}";
        var request = new PostProcessingRequest(id, "postprocess", new PostProcessingPayload(
            text,
            context,
            systemPrompt,
            Environment.GetEnvironmentVariable("MUESLI_POST_PROCESSOR_MODEL") ?? "Qwen/Qwen2.5-3B-Instruct"));

        var completion = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        await _worker.StandardInput.FlushAsync();

        WorkerEnvelope envelope;
        try
        {
            envelope = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            StopWorker();
            throw new TimeoutException("Post-processing timed out after 10 minutes. The worker was restarted.");
        }
        if (!envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(envelope.Error ?? "Post-processing failed.");
        }

        var warnings = envelope.Result.Warnings ?? [];
        if (!string.IsNullOrWhiteSpace(_stderr))
        {
            warnings.Add($"Worker stderr: {_stderr.Trim()}");
            _stderr = "";
        }

        return new PostProcessingResult(
            envelope.Result.TranscriptText ?? text,
            string.Join(Environment.NewLine, warnings));
    }

    public async Task<PostProcessingResult> DownloadModelAsync(string kind, string model)
    {
        EnsureWorker();

        if (_worker?.StandardInput is null)
        {
            throw new InvalidOperationException("Transcription worker did not start.");
        }

        var id = $"download_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _requestId)}";
        var request = new ModelDownloadRequest(id, "download_model", new ModelDownloadPayload(kind, model));
        var completion = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        await _worker.StandardInput.FlushAsync();

        var envelope = await completion.Task.WaitAsync(TimeSpan.FromMinutes(30));
        if (!envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(envelope.Error ?? "Model download failed.");
        }

        var warnings = envelope.Result.Warnings ?? [];
        if (!string.IsNullOrWhiteSpace(_stderr))
        {
            warnings.Add($"Worker stderr: {_stderr.Trim()}");
            _stderr = "";
        }

        return new PostProcessingResult(
            envelope.Result.TranscriptText ?? "Model ready.",
            string.Join(Environment.NewLine, warnings));
    }

    public async Task<DiarizationResult> DiarizeFileAsync(string filePath)
    {
        EnsureWorker();

        if (_worker?.StandardInput is null)
        {
            throw new InvalidOperationException("Transcription worker did not start.");
        }

        var id = $"diarize_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _requestId)}";
        var request = new DiarizationRequest(id, "diarize", new DiarizationPayload(filePath));

        var completion = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        await _worker.StandardInput.FlushAsync();

        WorkerEnvelope envelope;
        try
        {
            envelope = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            StopWorker();
            throw new TimeoutException("Speaker diarization timed out after 10 minutes. The worker was restarted and the transcript will be saved without speaker labels.");
        }
        if (!envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(envelope.Error ?? "Diarization failed.");
        }

        var warnings = envelope.Result.Warnings ?? [];
        if (!string.IsNullOrWhiteSpace(_stderr))
        {
            warnings.Add($"Worker stderr: {_stderr.Trim()}");
            _stderr = "";
        }

        return new DiarizationResult(
            envelope.Result.TranscriptText ?? "",
            envelope.Result.DetectedLanguage ?? "en",
            envelope.Result.DurationMs,
            envelope.Result.Segments?.Select(s => new DiarizedSegment(s.Speaker, s.StartMs, s.EndMs)).ToList() ?? new List<DiarizedSegment>(),
            warnings);
    }

    private async Task<TranscriptionResult> RequestTranscriptionAsync(WorkerPayload payload)
    {
        EnsureWorker();

        if (_worker?.StandardInput is null)
        {
            throw new InvalidOperationException("Transcription worker did not start.");
        }

        var id = $"native_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Interlocked.Increment(ref _requestId)}";
        var request = new WorkerRequest(id, "transcribe", payload);

        var completion = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        await _worker.StandardInput.FlushAsync();

        WorkerEnvelope envelope;
        try
        {
            envelope = await completion.Task.WaitAsync(TimeSpan.FromMinutes(10));
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            StopWorker();
            throw new TimeoutException("Transcription timed out after 10 minutes. The worker was restarted so another recording can be processed.");
        }
        if (!envelope.Ok || envelope.Result is null)
        {
            throw new InvalidOperationException(envelope.Error ?? "Transcription failed.");
        }

        var warnings = envelope.Result.Warnings ?? [];
        if (!string.IsNullOrWhiteSpace(_stderr))
        {
            warnings.Add($"Worker stderr: {_stderr.Trim()}");
            _stderr = "";
        }

        return new TranscriptionResult(
            envelope.Result.TranscriptText ?? "",
            string.Join(Environment.NewLine, warnings),
            envelope.Result.DurationMs,
            envelope.Result.Segments ?? new List<TranscriptSegment>());
    }

    public void Dispose()
    {
        StopWorker();
    }

    private void StopWorker()
    {
        try
        {
            _worker?.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may already be gone during app shutdown.
        }

        _worker?.Dispose();
        _worker = null;
    }

    private void EnsureWorker()
    {
        if (_worker is { HasExited: false })
        {
            return;
        }

        var python = FindPythonExecutable();
        var script = FindWorkerScript();
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory
        };
        WorkerRuntimeLocator.ApplyWorkerEnv(startInfo, python);
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("server");

        _worker = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _worker.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                HandleWorkerLine(args.Data);
            }
        };
        _worker.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                _stderr += args.Data + Environment.NewLine;
            }
        };
        _worker.Exited += (_, _) =>
        {
            foreach (var request in _pending.Values)
            {
                request.TrySetException(new InvalidOperationException("Transcription worker exited."));
            }
            _pending.Clear();
        };

        if (!_worker.Start())
        {
            throw new InvalidOperationException("Could not start transcription worker.");
        }

        _worker.BeginOutputReadLine();
        _worker.BeginErrorReadLine();
    }

    private void HandleWorkerLine(string line)
    {
        WorkerEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WorkerEnvelope>(line, JsonOptions);
        }
        catch
        {
            return;
        }

        if (envelope is null || !_pending.TryRemove(envelope.Id, out var completion))
        {
            return;
        }

        completion.TrySetResult(envelope);
    }

    private static string FindPythonExecutable() => WorkerRuntimeLocator.FindPythonExecutable();
    private static string FindWorkerScript() => WorkerRuntimeLocator.FindWorkerScript();
}

public sealed record TranscriptionOptions(string AsrEngine, string ModelProfile, string LanguageHint = "en");

public sealed record WorkerRequest(
    string Id,
    string Command,
    WorkerPayload Payload);

public sealed record PostProcessingRequest(
    string Id,
    string Command,
    PostProcessingPayload Payload);

public sealed record ModelDownloadRequest(
    string Id,
    string Command,
    ModelDownloadPayload Payload);

public sealed record ModelDownloadPayload(
    string Kind,
    string Model);

public sealed record PostProcessingPayload(
    string Text,
    string Context,
    string SystemPrompt,
    string Model);

public sealed record WorkerPayload(
    string Title,
    string InputPath,
    string Source,
    string AsrEngine,
    string Model,
    string TranscriptionMode,
    string LanguageHint,
    string AudioBase64);

public sealed record WorkerEnvelope(
    string Id,
    bool Ok,
    WorkerTranscriptionResult? Result,
    string? Error);

public sealed record WorkerTranscriptionResult(
    [property: JsonPropertyName("transcriptText")]
    string? TranscriptText,
    [property: JsonPropertyName("detectedLanguage")]
    string? DetectedLanguage,
    [property: JsonPropertyName("durationMs")]
    int DurationMs,
    [property: JsonPropertyName("segments")]
    List<TranscriptSegment> Segments,
    [property: JsonPropertyName("warnings")]
    List<string> Warnings);

public sealed record TranscriptSegment(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("speaker")]
    string Speaker,
    [property: JsonPropertyName("startMs")]
    int StartMs,
    [property: JsonPropertyName("endMs")]
    int EndMs,
    [property: JsonPropertyName("text")]
    string Text);

public sealed record DiarizationRequest(
    string Id,
    string Command,
    DiarizationPayload Payload);

public sealed record DiarizationPayload(
    [property: JsonPropertyName("input_path")]
    string InputPath);

public sealed record DiarizationResult(
    [property: JsonPropertyName("transcriptText")]
    string? TranscriptText,
    [property: JsonPropertyName("detectedLanguage")]
    string? DetectedLanguage,
    [property: JsonPropertyName("durationMs")]
    int DurationMs,
    [property: JsonPropertyName("segments")]
    List<DiarizedSegment> Segments,
    [property: JsonPropertyName("warnings")]
    List<string> Warnings);

public sealed record DiarizedSegment(
    [property: JsonPropertyName("speakerId")]
    string SpeakerId,
    [property: JsonPropertyName("startMs")]
    int StartMs,
    [property: JsonPropertyName("endMs")]
    int EndMs);

public sealed record PostProcessingResult(string Text, string? Diagnostic = null);
