using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed class TranscriptionBenchmarkService
{
    private readonly AppLogService _logService;

    public TranscriptionBenchmarkService(AppLogService? logService = null)
    {
        _logService = logService ?? new AppLogService();
    }

    public async Task<TranscriptionBenchmarkReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var audioPath = FindBenchmarkAudioPath();
        if (audioPath is null)
        {
            return new TranscriptionBenchmarkReport(
                "No local benchmark audio found.",
                "Record one dictation first, then run the benchmark again.",
                []);
        }

        return await RunFileAsync(audioPath, 3, null, TranscriptionModelCatalog.DefaultModelId, cancellationToken);
    }

    public async Task<TranscriptionBenchmarkReport> RunFileAsync(
        string audioPath,
        int runs = 3,
        string? referenceText = null,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var fullAudioPath = Path.GetFullPath(audioPath);
        if (!File.Exists(fullAudioPath))
        {
            throw new FileNotFoundException("Benchmark audio was not found.", fullAudioPath);
        }

        runs = Math.Clamp(runs, 1, 20);
        var durationMs = TryGetAudioDurationMs(fullAudioPath);
        if (!NativeParakeetClient.IsRuntimeAvailable)
        {
            var unavailableModel = TranscriptionModelCatalog.GetRequired(modelId ?? TranscriptionModelCatalog.DefaultModelId);
            return BuildReport(fullAudioPath, unavailableModel, [Failed(unavailableModel, durationMs, NativeSherpaRuntime.Diagnostic)]);
        }

        var model = TranscriptionModelCatalog.GetRequired(modelId ?? TranscriptionModelCatalog.DefaultModelId);
        if (!TranscriptionModelReadiness.IsVerified(model))
        {
            return BuildReport(fullAudioPath, model, [Failed(model, durationMs, $"{model.DisplayName} is missing or unverified.")]);
        }

        using var client = new NativeTranscriptionClient(model.Id);
        var results = new List<TranscriptionResult>(runs);
        var wallTimes = new List<long>(runs);
        ModelOperationResult initialization;
        var initializationWall = Stopwatch.StartNew();
        try
        {
            initialization = await client.InitializeAsync();
            initializationWall.Stop();
            for (var index = 0; index < runs; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                results.Add(await client.TranscribeFileAsync("Native benchmark", fullAudioPath));
                stopwatch.Stop();
                wallTimes.Add(stopwatch.ElapsedMilliseconds);
            }
        }
        catch (Exception exception)
        {
            _logService.Error($"{model.DisplayName} benchmark failed.", exception);
            return BuildReport(fullAudioPath, model, [Failed(model, durationMs, exception.Message)]);
        }

        var initializationMetrics = ModelInitializationMetrics.FromDiagnostic(initialization.Diagnostic);
        var firstMetrics = TranscriptionBenchmarkMetrics.FromDiagnostic(results[0].Diagnostic);
        var warmMetrics = TranscriptionBenchmarkMetrics.FromDiagnostic(results[^1].Diagnostic);
        // Some Media Foundation container readers do not expose TotalTime even
        // though they successfully decode samples. Prefer the measured decoded
        // sample duration reported by the actual ASR path.
        if (warmMetrics.AudioDurationMs > 0)
        {
            durationMs = warmMetrics.AudioDurationMs;
        }
        // The final run is the steady-state measurement after provider/JIT warmup.
        // All runs still participate in transcript and segment determinism checks.
        var warmWallMs = (int)wallTimes[^1];
        var warmInferenceMs = results[^1].DurationMs;
        var segments = results[^1].Segments ?? [];
        var normalized = results.Select(result => NormalizeQualityText(result.Text)).ToList();
        var transcriptHashes = normalized.Select(Sha256).ToList();
        var segmentHashes = results.Select(SegmentLayoutSha256).ToList();
        var segmentText = NormalizeQualityText(string.Join(" ", segments.Select(segment => segment.Text)));
        var benchmarkResult = new TranscriptionBenchmarkResult(
            client.EngineId,
            client.ModelId,
            durationMs,
            checked((int)(initializationWall.ElapsedMilliseconds + wallTimes[0])),
            initializationMetrics.ModelLoadMs,
            warmInferenceMs,
            warmWallMs,
            RealtimeFactor(warmWallMs, durationMs),
            results[^1].Text.Length,
            segments.Count,
            HasUsefulTimestamps(segments),
            segments.Count == 0 ? "none" : $"{segments[0].StartMs}-{segments[^1].EndMs} ms",
            firstMetrics.Backend,
            warmMetrics.Backend,
            warmMetrics.ThreadCount,
            warmMetrics.AudioPreprocessing,
            warmMetrics.AudioPreprocessingMs,
            true,
            "")
        {
            RunCount = runs,
            TranscriptSha256 = transcriptHashes[^1],
            DistinctTranscriptCount = transcriptHashes.Distinct(StringComparer.Ordinal).Count(),
            DeterministicOutput = transcriptHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            SegmentLayoutSha256 = segmentHashes[^1],
            DistinctSegmentLayoutCount = segmentHashes.Distinct(StringComparer.Ordinal).Count(),
            DeterministicSegments = segmentHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            SegmentsChronological = AreSegmentsChronological(segments),
            SegmentTextMatchesTranscript = segmentText == normalized[^1],
            MaximumSegmentDurationMs = segments.Count == 0 ? 0 : segments.Max(segment => segment.EndMs - segment.StartMs),
            Device = warmMetrics.Device,
            ComputeType = warmMetrics.ComputeType,
            ModelInstanceReused = warmMetrics.ModelInstanceReused,
            ReportedTranscriptionWallMs = warmMetrics.TranscriptionWallMs,
            WordErrorRate = string.IsNullOrWhiteSpace(referenceText)
                ? null
                : ErrorRate(
                    NormalizeQualityText(referenceText).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    normalized[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            CharacterErrorRate = string.IsNullOrWhiteSpace(referenceText)
                ? null
                : ErrorRate(
                    NormalizeQualityText(referenceText).Select(character => character.ToString()).ToArray(),
                    normalized[^1].Select(character => character.ToString()).ToArray()),
            InitializationWallMs = initializationWall.ElapsedMilliseconds,
            ModelVerificationMs = initializationMetrics.VerificationMs,
            ModelVerificationSource = initializationMetrics.VerificationSource
        };

        var report = BuildReport(fullAudioPath, model, [benchmarkResult]);
        LogReport(report);
        return report;
    }

    private static TranscriptionBenchmarkReport BuildReport(
        string audioPath,
        TranscriptionModelDefinition model,
        List<TranscriptionBenchmarkResult> results)
    {
        var result = results[0];
        var summary = result.Success
            ? $"{model.DisplayName} · {result.Device.ToUpperInvariant()} · {result.WarmWallMs} ms warm wall · RTF {result.RealtimeFactor:0.000} · deterministic {(result.DeterministicOutput ? "yes" : "no")}"
            : $"{model.DisplayName} benchmark failed: {result.ErrorMessage}";
        var recommendation = string.Join(
            Environment.NewLine,
            $"Audio: {Path.GetFileName(audioPath)}",
            $"Backend: native-sherpa-onnx/{model.Kind.ToString().ToLowerInvariant()}",
            $"Model: {model.Id}",
            result.Success
                ? $"Provider: {result.WarmBackend}; device: {result.Device}; compute: {result.ComputeType}; model reused: {result.ModelInstanceReused}"
                : result.ErrorMessage);
        return new TranscriptionBenchmarkReport(summary, recommendation, results);
    }

    private void LogReport(TranscriptionBenchmarkReport report)
    {
        _logService.Info($"Transcription benchmark summary. {report.Summary.Replace(Environment.NewLine, " | ")}");
        foreach (var result in report.Results)
        {
            _logService.Info(
                $"Transcription benchmark result. engine={result.EngineId}; model={result.ModelName}; device={result.Device}; computeType={result.ComputeType}; audioDurationMs={result.AudioDurationMs}; firstRunWallMs={result.FirstRunWallMs}; firstRunModelInitMs={result.FirstRunModelInitMs}; warmInferenceMs={result.WarmInferenceMs}; warmWallMs={result.WarmWallMs}; reportedTranscriptionWallMs={result.ReportedTranscriptionWallMs}; realtimeFactor={result.RealtimeFactor:0.000}; provider={result.WarmBackend}; modelInstanceReused={result.ModelInstanceReused}; threads={result.ThreadCount}; audioPreprocessing={result.AudioPreprocessing}; audioPreprocessingMs={result.AudioPreprocessingMs}; runs={result.RunCount}; deterministic={result.DeterministicOutput}; transcriptSha256={result.TranscriptSha256}; deterministicSegments={result.DeterministicSegments}; segmentLayoutSha256={result.SegmentLayoutSha256}; wordErrorRate={FormatOptionalRate(result.WordErrorRate)}; characterErrorRate={FormatOptionalRate(result.CharacterErrorRate)}; success={result.Success}; error={result.ErrorMessage}");
        }
    }

    private static TranscriptionBenchmarkResult Failed(TranscriptionModelDefinition model, int audioDurationMs, string error)
    {
        return new TranscriptionBenchmarkResult(
            $"native-sherpa-onnx/{model.Kind.ToString().ToLowerInvariant()}",
            model.Id,
            audioDurationMs,
            0, 0, 0, 0, 0, 0, 0, false, "none", "", "", 0, "", 0, false, error);
    }

    private static string? FindBenchmarkAudioPath()
    {
        var captures = Path.Combine(
                             Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().RootDirectory,
            "captures");
        return new[]
            {
                Path.Combine(captures, "last-dictation.wav"),
                Path.Combine(captures, "last-meeting-system.wav")
            }
            .FirstOrDefault(path => File.Exists(path) && new FileInfo(path).Length > 44);
    }

    private static int TryGetAudioDurationMs(string audioPath)
    {
        try
        {
            using var reader = new AudioFileReader(audioPath);
            return (int)reader.TotalTime.TotalMilliseconds;
        }
        catch
        {
            return 0;
        }
    }

    private static bool HasUsefulTimestamps(IReadOnlyList<TranscriptSegment> segments) =>
        segments.Count > 0 && segments.Any(segment => segment.EndMs > segment.StartMs);

    private static bool AreSegmentsChronological(IReadOnlyList<TranscriptSegment> segments)
    {
        var previousEnd = 0;
        foreach (var segment in segments)
        {
            if (segment.StartMs < previousEnd || segment.EndMs < segment.StartMs)
            {
                return false;
            }
            previousEnd = segment.EndMs;
        }
        return true;
    }

    private static string SegmentLayoutSha256(TranscriptionResult result) => Sha256(string.Join(
        "\n",
        (result.Segments ?? []).Select(segment =>
            $"{segment.StartMs}:{segment.EndMs}:{NormalizeQualityText(segment.Text)}")));

    private static double RealtimeFactor(long wallMs, int audioDurationMs) =>
        audioDurationMs <= 0 ? 0 : wallMs / (audioDurationMs * 1.0);

    private static string NormalizeQualityText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = true;
        foreach (var character in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == '\'')
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static double ErrorRate(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        if (reference.Count == 0) return hypothesis.Count == 0 ? 0 : 1;
        var previous = Enumerable.Range(0, hypothesis.Count + 1).ToArray();
        for (var i = 1; i <= reference.Count; i++)
        {
            var current = new int[hypothesis.Count + 1];
            current[0] = i;
            for (var j = 1; j <= hypothesis.Count; j++)
            {
                var substitution = string.Equals(reference[i - 1], hypothesis[j - 1], StringComparison.Ordinal) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + substitution);
            }
            previous = current;
        }
        return previous[hypothesis.Count] / (double)reference.Count;
    }

    private static string FormatOptionalRate(double? rate) => rate?.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) ?? "not-measured";
}

public sealed record TranscriptionBenchmarkReport(
    string Summary,
    string LaunchRecommendation,
    List<TranscriptionBenchmarkResult> Results);

public sealed record TranscriptionBenchmarkResult(
    string EngineId,
    string ModelName,
    int AudioDurationMs,
    int FirstRunWallMs,
    long FirstRunModelInitMs,
    int WarmInferenceMs,
    int WarmWallMs,
    double RealtimeFactor,
    int TranscriptCharacterCount,
    int SegmentCount,
    bool HasUsefulTimestamps,
    string TimestampRange,
    string FirstRunBackend,
    string WarmBackend,
    int ThreadCount,
    string AudioPreprocessing,
    long AudioPreprocessingMs,
    bool Success,
    string ErrorMessage)
{
    public int RunCount { get; init; }
    public string TranscriptSha256 { get; init; } = "";
    public int DistinctTranscriptCount { get; init; }
    public bool DeterministicOutput { get; init; }
    public string SegmentLayoutSha256 { get; init; } = "";
    public int DistinctSegmentLayoutCount { get; init; }
    public bool DeterministicSegments { get; init; }
    public bool SegmentsChronological { get; init; }
    public bool SegmentTextMatchesTranscript { get; init; }
    public int MaximumSegmentDurationMs { get; init; }
    public string Device { get; init; } = "";
    public string ComputeType { get; init; } = "";
    public bool ModelInstanceReused { get; init; }
    public long ReportedTranscriptionWallMs { get; init; }
    public double? WordErrorRate { get; init; }
    public double? CharacterErrorRate { get; init; }
    public long InitializationWallMs { get; init; }
    public long ModelVerificationMs { get; init; }
    public string ModelVerificationSource { get; init; } = "";
}

internal sealed record ModelInitializationMetrics(
    long ModelLoadMs,
    long VerificationMs,
    string VerificationSource)
{
    public static ModelInitializationMetrics FromDiagnostic(string? diagnostic)
    {
        var values = DiagnosticValues.Parse(diagnostic);
        return new ModelInitializationMetrics(
            DiagnosticValues.GetLong(values, "Initial model load ms"),
            DiagnosticValues.GetLong(values, "Model verification ms"),
            DiagnosticValues.Get(values, "Model verification"));
    }
}

internal sealed record TranscriptionBenchmarkMetrics(
    string Backend,
    long ModelLoadInitMs,
    int ThreadCount,
    string AudioPreprocessing,
    long AudioPreprocessingMs,
    long InferenceMs,
    int AudioDurationMs,
    string Device,
    string ComputeType,
    bool ModelInstanceReused,
    long TranscriptionWallMs)
{
    public static TranscriptionBenchmarkMetrics FromDiagnostic(string? diagnostic)
    {
        var values = DiagnosticValues.Parse(diagnostic);
        return new TranscriptionBenchmarkMetrics(
            DiagnosticValues.Get(values, "Backend"),
            DiagnosticValues.GetLong(values, "Model load/init duration ms"),
            DiagnosticValues.GetInt(values, "Threads"),
            DiagnosticValues.Get(values, "Audio preprocessing"),
            DiagnosticValues.GetLong(values, "Audio preprocessing duration ms"),
            DiagnosticValues.GetLong(values, "Inference duration ms"),
            DiagnosticValues.GetInt(values, "Audio duration ms"),
            DiagnosticValues.Get(values, "Device"),
            DiagnosticValues.Get(values, "Compute type"),
            DiagnosticValues.GetBool(values, "Model instance reused"),
            DiagnosticValues.GetLong(values, "Transcription wall duration ms"));
    }
}

internal static class DiagnosticValues
{
    public static Dictionary<string, string> Parse(string? diagnostic)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (diagnostic ?? "").Split(
                     [Environment.NewLine, "\n"],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    public static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : "";

    public static int GetInt(IReadOnlyDictionary<string, string> values, string key) =>
        int.TryParse(Get(values, key), out var value) ? value : 0;

    public static long GetLong(IReadOnlyDictionary<string, string> values, string key) =>
        long.TryParse(Get(values, key), out var value) ? value : 0;

    public static bool GetBool(IReadOnlyDictionary<string, string> values, string key) =>
        bool.TryParse(Get(values, key), out var value) && value;
}
