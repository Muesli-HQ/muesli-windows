using System.Security.Cryptography;
using System.Text.Json;
using Muesli.Windows.Services;

return await MuesliCommandHost.RunAsync(args);

internal static class MuesliCommandHost
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AppLogService Log = new();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (Has(args, "--prepare-model") || Has(args, "--verify-model"))
                return await RunModelPreparationAsync(args);
            if (Has(args, "--diagnose-native"))
                return await RunNativeRuntimeQualificationAsync(args);
            if (Has(args, "--benchmark-meeting"))
                return await RunMeetingQualificationAsync(args);
            if (Has(args, "--benchmark-native"))
                return await RunNativeBenchmarkAsync(args);

            Console.Error.WriteLine(Usage);
            return 64;
        }
        catch (Exception exception)
        {
            Log.Error("CommandHost failed.", exception);
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunModelPreparationAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output");
        try
        {
            var modelId = RequiredOption(args, "--model");
            var verifyOnly = Has(args, "--verify-model");
            using var lifecycle = new TranscriptionModelLifecycleService();
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                var message = $"Model {modelId}: {value.DisplayText}";
                Log.Info(message);
                Console.WriteLine(message);
            });
            if (verifyOnly)
                await lifecycle.VerifyAsync(modelId, progress);
            else
                await lifecycle.PrepareAsync(modelId, progress);

            var snapshot = lifecycle.Snapshot(modelId);
            var payload = new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Model = modelId,
                snapshot.Status,
                snapshot.StatusText,
                snapshot.DiskSizeBytes,
                ExplicitNetworkBackedAction = !verifyOnly,
                VerifyOnly = verifyOnly,
                DownloadActivatedRecognizer = false
            };
            await WriteOptionalPayloadAsync(outputPath, payload);
            Console.WriteLine($"{modelId}: {snapshot.StatusText}");
            return snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected ? 0 : 2;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static async Task<int> RunMeetingQualificationAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output") ?? DefaultOutput("benchmarks", "meeting-qualification");
        try
        {
            var microphone = Option(args, "--mic-audio");
            var system = Option(args, "--system-audio");
            if (string.IsNullOrWhiteSpace(microphone) && string.IsNullOrWhiteSpace(system))
                throw new ArgumentException("Meeting qualification requires --mic-audio <path>, --system-audio <path>, or both.");

            var runs = ParseRuns(args, 3, 1, 20);
            var modelId = Option(args, "--model") ?? TranscriptionModelCatalog.DefaultModelId;
            var report = await new MeetingQualificationService().RunAsync(microphone, system, runs, modelId);
            await WritePayloadAsync(outputPath, new { SchemaVersion = 1, CreatedAtUtc = DateTimeOffset.UtcNow, Report = report });
            Console.WriteLine(report.Summary);
            return 0;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static async Task<int> RunNativeRuntimeQualificationAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output") ?? DefaultOutput("diagnostics", "native-runtime");
        try
        {
            var report = await new NativeRuntimeQualificationService().RunAsync(
                Option(args, "--audio"),
                ParseRuns(args, 10, 2, 50),
                Option(args, "--model") ?? TranscriptionModelCatalog.DefaultModelId);
            await WritePayloadAsync(outputPath, new { SchemaVersion = 1, CreatedAtUtc = DateTimeOffset.UtcNow, Report = report });
            Console.WriteLine($"Runtime: {report.SelectedRuntime}; passed={report.Passed}");
            return report.Passed ? 0 : 2;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static async Task<int> RunNativeBenchmarkAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output") ?? DefaultOutput("benchmarks", "native-transcription");
        try
        {
            var audioPath = Path.GetFullPath(RequiredOption(args, "--audio"));
            var referencePath = Option(args, "--reference");
            var referenceText = string.IsNullOrWhiteSpace(referencePath)
                ? null
                : await File.ReadAllTextAsync(Path.GetFullPath(referencePath));
            var runs = ParseRuns(args, 3, 1, 20);
            var modelId = Option(args, "--model") ?? TranscriptionModelCatalog.DefaultModelId;
            var report = await new TranscriptionBenchmarkService(Log).RunFileAsync(
                audioPath,
                runs,
                referenceText,
                modelId);
            await WritePayloadAsync(outputPath, new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AudioPath = audioPath,
                AudioSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(audioPath))).ToLowerInvariant(),
                ReferencePath = string.IsNullOrWhiteSpace(referencePath) ? null : Path.GetFullPath(referencePath),
                Engine = report.Results.FirstOrDefault()?.EngineId,
                Model = report.Results.FirstOrDefault()?.ModelName ?? modelId,
                Runs = runs,
                SherpaOnnxRuntime = new
                {
                    NativeSherpaRuntime.IsAvailable,
                    NativeSherpaRuntime.IsCudaCapable,
                    NativeSherpaRuntime.SelectedRuntime,
                    RuntimeDirectory = NativeSherpaRuntime.CudaRuntimeDirectory,
                    NativeSherpaRuntime.GpuDependencyCacheDirectory,
                    NativeSherpaRuntime.Diagnostic
                },
                Report = report
            });
            return report.Results.Any(result => result.Success) ? 0 : 2;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static int ParseRuns(IReadOnlyList<string> args, int fallback, int minimum, int maximum) =>
        int.TryParse(Option(args, "--runs"), out var configured)
            ? Math.Clamp(configured, minimum, maximum)
            : fallback;

    private static string DefaultOutput(string subdirectory, string stem) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "muesli",
        subdirectory,
        $"{stem}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");

    private static async Task WriteOptionalPayloadAsync(string? path, object payload)
    {
        if (!string.IsNullOrWhiteSpace(path))
            await WritePayloadAsync(path, payload);
    }

    private static async Task WritePayloadAsync(string path, object payload)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(payload, JsonOptions));
        Log.Info($"CommandHost report written. path={fullPath}");
    }

    private static async Task WriteFailureAsync(string? path, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            await WritePayloadAsync(path, new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Error = exception.ToString()
            });
        }
        catch
        {
            // The primary exception remains on stderr and in the application log.
        }
    }

    private static bool Has(IReadOnlyList<string> args, string name) =>
        args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string RequiredOption(IReadOnlyList<string> args, string name) =>
        Option(args, name) ?? throw new ArgumentException($"Command requires {name} <value>.");

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private const string Usage = """
        Muesli.Windows.CommandHost
          --prepare-model --model <id> [--output <json>]
          --verify-model --model <id> [--output <json>]
          --diagnose-native [--audio <wav>] [--model <id>] [--runs <n>] [--output <json>]
          --benchmark-native --audio <wav> [--reference <txt>] [--model <id>] [--runs <n>] [--output <json>]
          --benchmark-meeting [--mic-audio <path>] [--system-audio <path>] [--model <id>] [--runs <n>] [--output <json>]
        """;
}
