using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Muesli.Windows.Services;

public sealed class NativeRuntimeQualificationService
{
    private static readonly string[] RequiredRuntimeFileNames =
    [
        "sherpa-onnx.dll",
        "sherpa-onnx-c-api.dll",
        "onnxruntime.dll"
    ];

    public async Task<NativeRuntimeQualificationReport> RunAsync(
        string? audioPath,
        int runs,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var selectedModel = TranscriptionModelCatalog.GetRequired(
            modelId ?? TranscriptionModelCatalog.DefaultModelId);
        var runtimeFiles = RequiredRuntimeFileNames
            .Select(InspectRuntimeFile)
            .ToList();
        var runtimeAvailable = NativeSherpaRuntime.IsAvailable;
        NativeStressQualification? stress = null;
        if (!string.IsNullOrWhiteSpace(audioPath))
        {
            stress = await RunStressAsync(
                Path.GetFullPath(audioPath),
                runs,
                modelId ?? TranscriptionModelCatalog.DefaultModelId,
                cancellationToken);
        }

        var failures = new List<string>();
        if (!runtimeAvailable)
        {
            failures.Add("The native Sherpa runtime could not be loaded.");
        }
        foreach (var missing in runtimeFiles.Where(file => !file.Exists))
        {
            failures.Add($"Required native runtime file is missing: {missing.FileName}.");
        }
        if (stress is { Passed: false })
        {
            failures.AddRange(stress.Failures);
        }

        return new NativeRuntimeQualificationReport
        {
            Passed = failures.Count == 0,
            Failures = failures,
            AppVersion = typeof(NativeRuntimeQualificationService).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            OsDescription = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ProcessorCount = Environment.ProcessorCount,
            RuntimeAvailable = runtimeAvailable,
            SelectedRuntime = NativeSherpaRuntime.SelectedRuntime,
            CudaCapable = NativeSherpaRuntime.IsCudaCapable,
            RuntimeDiagnostic = NativeSherpaRuntime.Diagnostic,
            TranscriptionModelId = selectedModel.Id,
            TranscriptionModelCached = selectedModel.IsCached,
            TranscriptionModelVerified = TranscriptionModelReadiness.IsVerified(selectedModel),
            DiarizationModelsCached = NativeDiarizationClient.IsReady,
            RuntimeFiles = runtimeFiles,
            Stress = stress
        };
    }

    private static async Task<NativeStressQualification> RunStressAsync(
        string audioPath,
        int runs,
        string modelId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException("Native qualification audio was not found.", audioPath);
        }

        runs = Math.Clamp(runs, 2, 50);
        using var client = new NativeTranscriptionClient(modelId);
        var initializationWall = Stopwatch.StartNew();
        var initialization = await client.InitializeAsync();
        initializationWall.Stop();
        var measurements = new List<NativeStressMeasurement>(runs);

        for (var index = 0; index < runs; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wall = Stopwatch.StartNew();
            var result = await client.TranscribeFileAsync("Native release qualification", audioPath);
            wall.Stop();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var metrics = TranscriptionBenchmarkMetrics.FromDiagnostic(result.Diagnostic);
            measurements.Add(new NativeStressMeasurement
            {
                Run = index + 1,
                WallMs = wall.ElapsedMilliseconds,
                InferenceMs = result.DurationMs,
                AudioDurationMs = metrics.AudioDurationMs,
                RealtimeFactor = metrics.AudioDurationMs <= 0
                    ? 0
                    : wall.ElapsedMilliseconds / (double)metrics.AudioDurationMs,
                Provider = metrics.Backend,
                Device = metrics.Device,
                ComputeType = metrics.ComputeType,
                ModelReused = metrics.ModelInstanceReused,
                TranscriptSha256 = Sha256(Normalize(result.Text)),
                SegmentLayoutSha256 = SegmentLayoutSha256(result.Segments ?? []),
                TranscriptCharacters = result.Text.Length,
                SegmentCount = result.Segments?.Count ?? 0,
                WorkingSetBytes = process.WorkingSet64,
                PrivateMemoryBytes = process.PrivateMemorySize64
            });
        }

        var transcriptHashes = measurements.Select(item => item.TranscriptSha256).Distinct(StringComparer.Ordinal).ToArray();
        var segmentHashes = measurements.Select(item => item.SegmentLayoutSha256).Distinct(StringComparer.Ordinal).ToArray();
        var providers = measurements.Select(item => item.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var failures = new List<string>();
        if (measurements.Any(item => item.AudioDurationMs <= 0)) failures.Add("Decoded audio duration was zero.");
        if (measurements.Any(item => !item.ModelReused)) failures.Add("The selected transcription model was not reused for every measured run.");
        if (transcriptHashes.Length != 1) failures.Add("Transcript output changed across repeated runs.");
        if (segmentHashes.Length != 1) failures.Add("Timestamp segment layout changed across repeated runs.");
        if (providers.Length != 1) failures.Add("The selected execution provider changed during the stress run.");

        var firstWorkingSet = measurements[0].WorkingSetBytes;
        var finalWorkingSet = measurements[^1].WorkingSetBytes;
        var firstPrivateMemory = measurements[0].PrivateMemoryBytes;
        var finalPrivateMemory = measurements[^1].PrivateMemoryBytes;
        var steadyStateBaseline = measurements[Math.Min(1, measurements.Count - 1)];
        return new NativeStressQualification
        {
            Passed = failures.Count == 0,
            Failures = failures,
            AudioPath = audioPath,
            AudioSha256 = FileSha256(audioPath),
            Runs = runs,
            InitializationWallMs = initializationWall.ElapsedMilliseconds,
            InitializationDiagnostic = initialization.Diagnostic ?? "",
            Provider = providers.FirstOrDefault() ?? "",
            Device = measurements[^1].Device,
            ComputeType = measurements[^1].ComputeType,
            DeterministicTranscript = transcriptHashes.Length == 1,
            DeterministicSegments = segmentHashes.Length == 1,
            ModelReusedEveryRun = measurements.All(item => item.ModelReused),
            TranscriptSha256 = measurements[^1].TranscriptSha256,
            SegmentLayoutSha256 = measurements[^1].SegmentLayoutSha256,
            WarmWallMs = measurements[^1].WallMs,
            WarmRealtimeFactor = measurements[^1].RealtimeFactor,
            PeakWorkingSetBytes = measurements.Max(item => item.WorkingSetBytes),
            WorkingSetGrowthBytes = finalWorkingSet - firstWorkingSet,
            PrivateMemoryGrowthBytes = finalPrivateMemory - firstPrivateMemory,
            SteadyStateWorkingSetGrowthBytes = finalWorkingSet - steadyStateBaseline.WorkingSetBytes,
            SteadyStatePrivateMemoryGrowthBytes = finalPrivateMemory - steadyStateBaseline.PrivateMemoryBytes,
            Measurements = measurements
        };
    }

    private static NativeRuntimeFileEvidence InspectRuntimeFile(string fileName)
    {
        var path = Directory.EnumerateFiles(AppContext.BaseDirectory, fileName, SearchOption.AllDirectories)
            .OrderBy(candidate => candidate.Length)
            .FirstOrDefault();
        return path is null
            ? new NativeRuntimeFileEvidence(fileName, false, "", 0, "")
            : new NativeRuntimeFileEvidence(
                fileName,
                true,
                Path.GetRelativePath(AppContext.BaseDirectory, path),
                new FileInfo(path).Length,
                FileSha256(path));
    }

    private static string SegmentLayoutSha256(IEnumerable<TranscriptSegment> segments) =>
        Sha256(JsonSerializer.Serialize(segments.Select(segment => new
        {
            segment.Speaker,
            segment.StartMs,
            segment.EndMs,
            Text = Normalize(segment.Text)
        })));

    private static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Normalize(string? value) =>
        string.Join(" ", (value ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed record NativeRuntimeQualificationReport
{
    public bool Passed { get; init; }
    public List<string> Failures { get; init; } = [];
    public string AppVersion { get; init; } = "";
    public string Framework { get; init; } = "";
    public string OsDescription { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public int ProcessorCount { get; init; }
    public bool RuntimeAvailable { get; init; }
    public string SelectedRuntime { get; init; } = "";
    public bool CudaCapable { get; init; }
    public string RuntimeDiagnostic { get; init; } = "";
    public string TranscriptionModelId { get; init; } = "";
    public bool TranscriptionModelCached { get; init; }
    public bool TranscriptionModelVerified { get; init; }
    public bool DiarizationModelsCached { get; init; }
    public List<NativeRuntimeFileEvidence> RuntimeFiles { get; init; } = [];
    public NativeStressQualification? Stress { get; init; }
}

public sealed record NativeRuntimeFileEvidence(
    string FileName,
    bool Exists,
    string RelativePath,
    long Bytes,
    string Sha256);

public sealed record NativeStressQualification
{
    public bool Passed { get; init; }
    public List<string> Failures { get; init; } = [];
    public string AudioPath { get; init; } = "";
    public string AudioSha256 { get; init; } = "";
    public int Runs { get; init; }
    public long InitializationWallMs { get; init; }
    public string InitializationDiagnostic { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Device { get; init; } = "";
    public string ComputeType { get; init; } = "";
    public bool DeterministicTranscript { get; init; }
    public bool DeterministicSegments { get; init; }
    public bool ModelReusedEveryRun { get; init; }
    public string TranscriptSha256 { get; init; } = "";
    public string SegmentLayoutSha256 { get; init; } = "";
    public long WarmWallMs { get; init; }
    public double WarmRealtimeFactor { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public long WorkingSetGrowthBytes { get; init; }
    public long PrivateMemoryGrowthBytes { get; init; }
    public long SteadyStateWorkingSetGrowthBytes { get; init; }
    public long SteadyStatePrivateMemoryGrowthBytes { get; init; }
    public List<NativeStressMeasurement> Measurements { get; init; } = [];
}

public sealed record NativeStressMeasurement
{
    public int Run { get; init; }
    public long WallMs { get; init; }
    public int InferenceMs { get; init; }
    public int AudioDurationMs { get; init; }
    public double RealtimeFactor { get; init; }
    public string Provider { get; init; } = "";
    public string Device { get; init; } = "";
    public string ComputeType { get; init; } = "";
    public bool ModelReused { get; init; }
    public string TranscriptSha256 { get; init; } = "";
    public string SegmentLayoutSha256 { get; init; } = "";
    public int TranscriptCharacters { get; init; }
    public int SegmentCount { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateMemoryBytes { get; init; }
}
