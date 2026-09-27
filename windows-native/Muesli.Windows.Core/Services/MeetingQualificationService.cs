using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Muesli.Windows.Services;

public sealed class MeetingQualificationService
{
    public async Task<MeetingQualificationReport> RunAsync(
        string? micAudioPath,
        string? systemAudioPath,
        int runs = 3,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        var micPath = ResolveOptionalPath(micAudioPath);
        var systemPath = ResolveOptionalPath(systemAudioPath);
        if (micPath is null && systemPath is null)
        {
            throw new ArgumentException("Meeting qualification requires mic audio, system audio, or both.");
        }

        runs = Math.Clamp(runs, 2, 20);
        var micDurationMs = micPath is null ? 0 : AudioDurationMs(micPath);
        var systemDurationMs = systemPath is null ? 0 : AudioDurationMs(systemPath);
        var meetingDurationMs = Math.Max(micDurationMs, systemDurationMs);
        var memoryBeforeInitialization = ProcessMemorySnapshot.Capture();
        using var transcriptionClient = new NativeTranscriptionClient(modelId ?? TranscriptionModelCatalog.DefaultModelId);
        await transcriptionClient.InitializeAsync();
        var memoryAfterInitialization = ProcessMemorySnapshot.Capture();
        using var diarizationClient = systemPath is null ? null : new NativeDiarizationClient();

        var measurements = new List<MeetingQualificationRun>(runs);
        for (var index = 0; index < runs; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var total = Stopwatch.StartNew();
            Task<(DiarizationResult Result, long WallMs)>? diarizationTask =
                systemPath is not null && NativeDiarizationClient.ShouldOverlapWithAsr
                    ? MeasureDiarizationAsync(diarizationClient!, systemPath)
                    : null;
            var micWall = Stopwatch.StartNew();
            var mic = micPath is null
                ? new TranscriptionResult("")
                : await transcriptionClient.TranscribeFileAsync("Meeting qualification mic", micPath);
            micWall.Stop();

            var systemWall = Stopwatch.StartNew();
            var system = systemPath is null
                ? new TranscriptionResult("")
                : await transcriptionClient.TranscribeFileAsync("Meeting qualification system", systemPath);
            systemWall.Stop();

            diarizationTask ??= systemPath is null
                ? Task.FromResult((new DiarizationResult("", "", 0, [], []), 0L))
                : MeasureDiarizationAsync(diarizationClient!, systemPath);
            var (diarization, diarizationWallMs) = await diarizationTask;

            var merge = Stopwatch.StartNew();
            var merged = TranscriptFormatter.Merge(
                mic.Segments ?? [],
                system.Segments ?? [],
                diarization.Segments ?? [],
                DateTime.UnixEpoch);
            merge.Stop();
            total.Stop();

            var asrMetrics = TranscriptionBenchmarkMetrics.FromDiagnostic(
                string.IsNullOrWhiteSpace(system.Diagnostic) ? mic.Diagnostic : system.Diagnostic);
            measurements.Add(new MeetingQualificationRun(
                mic,
                system,
                diarization,
                merged,
                micWall.ElapsedMilliseconds,
                systemWall.ElapsedMilliseconds,
                diarizationWallMs,
                merge.ElapsedMilliseconds,
                total.ElapsedMilliseconds,
                asrMetrics));
        }

        var final = measurements[^1];
        var asrHashes = measurements.Select(CombinedAsrHash).ToArray();
        var asrSegmentHashes = measurements.Select(CombinedAsrSegmentHash).ToArray();
        var diarizationHashes = measurements.Select(run => DiarizationHash(run.Diarization.Segments)).ToArray();
        var mergedHashes = measurements.Select(run => Sha256(Normalize(run.MergedTranscript))).ToArray();
        var speakerIds = final.Diarization.Segments
            .Select(segment => segment.SpeakerId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var coverage = systemDurationMs <= 0
            ? 0
            : Math.Min(1, CoveredDurationMs(final.Diarization.Segments) / (double)systemDurationMs);
        var warmTotalMs = final.TotalWallMs;
        var finalMemory = ProcessMemorySnapshot.Capture();
        var report = new MeetingQualificationResult
        {
            Engine = transcriptionClient.EngineId,
            Model = transcriptionClient.ModelId,
            MicAudioPath = micPath,
            SystemAudioPath = systemPath,
            MicDurationMs = micDurationMs,
            SystemDurationMs = systemDurationMs,
            MeetingDurationMs = meetingDurationMs,
            Runs = runs,
            FirstTotalWallMs = measurements[0].TotalWallMs,
            WarmTotalWallMs = warmTotalMs,
            TotalRealtimeFactor = meetingDurationMs <= 0 ? 0 : warmTotalMs / (double)meetingDurationMs,
            WarmMicAsrWallMs = final.MicAsrWallMs,
            WarmSystemAsrWallMs = final.SystemAsrWallMs,
            WarmDiarizationWallMs = final.DiarizationWallMs,
            WarmMergeWallMs = final.MergeWallMs,
            AsrProvider = final.AsrMetrics.Backend,
            AsrDevice = final.AsrMetrics.Device,
            AsrThreadCount = final.AsrMetrics.ThreadCount,
            AsrAudioPreprocessing = final.AsrMetrics.AudioPreprocessing,
            AsrAudioPreprocessingMs = final.AsrMetrics.AudioPreprocessingMs,
            AsrInferenceMs = final.AsrMetrics.InferenceMs,
            AsrModelReused = final.AsrMetrics.ModelInstanceReused,
            WorkingSetBeforeInitializationBytes = memoryBeforeInitialization.WorkingSetBytes,
            WorkingSetAfterInitializationBytes = memoryAfterInitialization.WorkingSetBytes,
            FinalWorkingSetBytes = finalMemory.WorkingSetBytes,
            PrivateMemoryBeforeInitializationBytes = memoryBeforeInitialization.PrivateMemoryBytes,
            PrivateMemoryAfterInitializationBytes = memoryAfterInitialization.PrivateMemoryBytes,
            FinalPrivateMemoryBytes = finalMemory.PrivateMemoryBytes,
            DiarizationProvider = final.Diarization.Provider,
            DiarizationModelLoadMs = final.Diarization.ModelLoadMs,
            DiarizationModelReused = final.Diarization.ModelReused,
            DiarizationProcessingMs = final.Diarization.ProcessingMs,
            DiarizationOverlappedWithAsr = systemPath is not null && NativeDiarizationClient.ShouldOverlapWithAsr,
            MicSegmentCount = final.Mic.Segments?.Count ?? 0,
            SystemSegmentCount = final.System.Segments?.Count ?? 0,
            DiarizationSegmentCount = final.Diarization.Segments.Count,
            SpeakerCount = speakerIds.Length,
            SpeakerCoverage = coverage,
            MergedCharacterCount = final.MergedTranscript.Length,
            MergedSpeakerLineCount = final.MergedTranscript
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.Contains(": ", StringComparison.Ordinal)),
            TranscriptSha256 = asrHashes[^1],
            SegmentLayoutSha256 = asrSegmentHashes[^1],
            DiarizationLayoutSha256 = diarizationHashes[^1],
            MergedTranscriptSha256 = mergedHashes[^1],
            DeterministicTranscript = asrHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            DeterministicSegments = asrSegmentHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            DeterministicDiarization = diarizationHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            DeterministicMerge = mergedHashes.Distinct(StringComparer.Ordinal).Count() == 1,
            DiarizationSegmentsChronological = AreChronological(final.Diarization.Segments),
            Warnings = final.Diarization.Warnings
        };

        return new MeetingQualificationReport(
            $"Meeting qualification · {report.AsrProvider.ToUpperInvariant()} ASR · {report.DiarizationProvider.ToUpperInvariant()} diarization · {report.WarmTotalWallMs} ms warm · RTF {report.TotalRealtimeFactor:0.000}",
            report);
    }

    private static string? ResolveOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Meeting qualification audio was not found.", fullPath);
    }

    private static async Task<(DiarizationResult Result, long WallMs)> MeasureDiarizationAsync(
        NativeDiarizationClient client,
        string audioPath)
    {
        var wall = Stopwatch.StartNew();
        var result = await client.DiarizeFileAsync(audioPath);
        wall.Stop();
        return (result, wall.ElapsedMilliseconds);
    }

    private static int AudioDurationMs(string path)
    {
        using var reader = new AudioFileReader(path);
        return checked((int)reader.TotalTime.TotalMilliseconds);
    }

    private static string CombinedAsrHash(MeetingQualificationRun run) =>
        Sha256($"{Normalize(run.Mic.Text)}\n---system---\n{Normalize(run.System.Text)}");

    private static string CombinedAsrSegmentHash(MeetingQualificationRun run) => Sha256(JsonSerializer.Serialize(new
    {
        mic = (run.Mic.Segments ?? []).Select(SegmentLayout),
        system = (run.System.Segments ?? []).Select(SegmentLayout)
    }));

    private static object SegmentLayout(TranscriptSegment segment) => new
    {
        segment.Speaker,
        segment.StartMs,
        segment.EndMs,
        Text = Normalize(segment.Text)
    };

    private static string DiarizationHash(IEnumerable<DiarizedSegment> segments) =>
        Sha256(JsonSerializer.Serialize(segments.Select(segment => new
        {
            segment.SpeakerId,
            segment.StartMs,
            segment.EndMs
        })));

    private static bool AreChronological(IReadOnlyList<DiarizedSegment> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].StartMs < 0 || segments[index].EndMs < segments[index].StartMs)
            {
                return false;
            }
            if (index > 0 && segments[index].StartMs < segments[index - 1].StartMs)
            {
                return false;
            }
        }

        return true;
    }

    private static long CoveredDurationMs(IEnumerable<DiarizedSegment> segments)
    {
        var ordered = segments.OrderBy(segment => segment.StartMs).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        long covered = 0;
        var start = ordered[0].StartMs;
        var end = ordered[0].EndMs;
        foreach (var segment in ordered.Skip(1))
        {
            if (segment.StartMs <= end)
            {
                end = Math.Max(end, segment.EndMs);
                continue;
            }

            covered += Math.Max(0, end - start);
            start = segment.StartMs;
            end = segment.EndMs;
        }

        return covered + Math.Max(0, end - start);
    }

    private static string Normalize(string? value) =>
        string.Join(" ", (value ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ProcessMemorySnapshot(long WorkingSetBytes, long PrivateMemoryBytes)
    {
        public static ProcessMemorySnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new ProcessMemorySnapshot(process.WorkingSet64, process.PrivateMemorySize64);
        }
    }

    private sealed record MeetingQualificationRun(
        TranscriptionResult Mic,
        TranscriptionResult System,
        DiarizationResult Diarization,
        string MergedTranscript,
        long MicAsrWallMs,
        long SystemAsrWallMs,
        long DiarizationWallMs,
        long MergeWallMs,
        long TotalWallMs,
        TranscriptionBenchmarkMetrics AsrMetrics);
}

public sealed record MeetingQualificationReport(string Summary, MeetingQualificationResult Result);

public sealed record MeetingQualificationResult
{
    public string Engine { get; init; } = "";
    public string Model { get; init; } = "";
    public string? MicAudioPath { get; init; }
    public string? SystemAudioPath { get; init; }
    public int MicDurationMs { get; init; }
    public int SystemDurationMs { get; init; }
    public int MeetingDurationMs { get; init; }
    public int Runs { get; init; }
    public long FirstTotalWallMs { get; init; }
    public long WarmTotalWallMs { get; init; }
    public double TotalRealtimeFactor { get; init; }
    public long WarmMicAsrWallMs { get; init; }
    public long WarmSystemAsrWallMs { get; init; }
    public long WarmDiarizationWallMs { get; init; }
    public long WarmMergeWallMs { get; init; }
    public string AsrProvider { get; init; } = "";
    public string AsrDevice { get; init; } = "";
    public int AsrThreadCount { get; init; }
    public string AsrAudioPreprocessing { get; init; } = "";
    public long AsrAudioPreprocessingMs { get; init; }
    public long AsrInferenceMs { get; init; }
    public bool AsrModelReused { get; init; }
    public long WorkingSetBeforeInitializationBytes { get; init; }
    public long WorkingSetAfterInitializationBytes { get; init; }
    public long FinalWorkingSetBytes { get; init; }
    public long PrivateMemoryBeforeInitializationBytes { get; init; }
    public long PrivateMemoryAfterInitializationBytes { get; init; }
    public long FinalPrivateMemoryBytes { get; init; }
    public string DiarizationProvider { get; init; } = "";
    public long DiarizationModelLoadMs { get; init; }
    public bool DiarizationModelReused { get; init; }
    public long DiarizationProcessingMs { get; init; }
    public bool DiarizationOverlappedWithAsr { get; init; }
    public int MicSegmentCount { get; init; }
    public int SystemSegmentCount { get; init; }
    public int DiarizationSegmentCount { get; init; }
    public int SpeakerCount { get; init; }
    public double SpeakerCoverage { get; init; }
    public int MergedCharacterCount { get; init; }
    public int MergedSpeakerLineCount { get; init; }
    public string TranscriptSha256 { get; init; } = "";
    public string SegmentLayoutSha256 { get; init; } = "";
    public string DiarizationLayoutSha256 { get; init; } = "";
    public string MergedTranscriptSha256 { get; init; } = "";
    public bool DeterministicTranscript { get; init; }
    public bool DeterministicSegments { get; init; }
    public bool DeterministicDiarization { get; init; }
    public bool DeterministicMerge { get; init; }
    public bool DiarizationSegmentsChronological { get; init; }
    public List<string> Warnings { get; init; } = [];
}
