using System.Security.Cryptography;
using System.Text.Json;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using NAudio.Wave;

return await MuesliCommandHost.RunAsync(args);

internal static class MuesliCommandHost
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AppLogService Log = new();

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // The execution provider must be chosen before the first native recognizer exists.
            // --provider wins so a CPU/GPU benchmark pair can be produced from one machine; when it
            // is absent the persisted product setting is used, matching the WinUI app.
            ConfigureExecutionProvider(args);
            if (Has(args, "--prepare-model") || Has(args, "--verify-model"))
                return await RunModelPreparationAsync(args);
            if (Has(args, "--diagnose-native"))
                return await RunNativeRuntimeQualificationAsync(args);
            if (Has(args, "--benchmark-meeting"))
                return await RunMeetingQualificationAsync(args);
            if (Has(args, "--benchmark-native"))
                return await RunNativeBenchmarkAsync(args);
            if (Has(args, "--prepare-streaming-model"))
                return await RunStreamingModelPreparationAsync(args);
            if (Has(args, "--benchmark-live"))
                return await RunLiveTranscriptionBenchmarkAsync(args);
            if (Has(args, "--acceleration-status"))
                return await PrintAccelerationStatusAsync(args);
            if (Has(args, "--prepare-cuda-pack"))
                return await RunCudaPackAsync(args, CudaPackOperation.Prepare);
            if (Has(args, "--verify-cuda-pack"))
                return await RunCudaPackAsync(args, CudaPackOperation.Verify);
            if (Has(args, "--delete-cuda-pack"))
                return await RunCudaPackAsync(args, CudaPackOperation.Delete);

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

    private static void ConfigureExecutionProvider(IReadOnlyList<string> args)
    {
        var explicitProvider = Option(args, "--provider");
        if (!string.IsNullOrWhiteSpace(explicitProvider))
        {
            ExecutionProviderService.Configure(explicitProvider);
            return;
        }

        try
        {
            var settings = new SettingsStore(
                Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().SettingsPath,
                new NullSecretStore()).Load();
            ExecutionProviderService.Configure(settings.ExecutionProvider);
            NativeTextCleanupService.Configure(settings.CleanupModelId);
        }
        catch
        {
            ExecutionProviderService.Configure(ExecutionProviderPreference.Auto);
        }
    }

    private static async Task<int> PrintAccelerationStatusAsync(IReadOnlyList<string> args)
    {
        var status = ExecutionProviderService.Inspect();
        var payload = new
        {
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            status.Requested,
            status.RequestedLabel,
            status.LoadedRuntime,
            status.LoadedRuntimeLabel,
            status.CudaPackInstalled,
            status.CudaPackVerified,
            status.CudaPackDirectory,
            status.NvidiaRuntimeComplete,
            status.MissingNvidiaFiles,
            status.NvidiaAdapterDetected,
            status.CudaDriverPresent,
            status.DirectMlAvailable,
            status.DirectMlReason,
            status.RestartRequired,
            Adapters = status.Adapters.Select(adapter => adapter.ToString()).ToArray(),
            Telemetry = ExecutionProviderService.Snapshot()
        };
        await WriteOptionalPayloadAsync(Option(args, "--output"), payload);
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return status.LoadedRuntime is "cuda" or "cpu" ? 0 : 2;
    }

    private enum CudaPackOperation
    {
        Prepare,
        Verify,
        Delete
    }

    private static async Task<int> RunStreamingModelPreparationAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output");
        var modelId = Option(args, "--model") ?? StreamingModelCatalog.Nemotron35Id;
        var model = StreamingModelCatalog.Get(modelId)
            ?? throw new InvalidOperationException($"Unknown streaming model id '{modelId}'.");
        var progress = new Progress<ModelDownloadProgress>(value => Console.WriteLine($"{model.DisplayName}: {value.DisplayText}"));
        await new StreamingModelInstaller(model).PrepareAsync(progress, CancellationToken.None);
        await WriteOptionalPayloadAsync(outputPath, new
        {
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Model = model.Id,
            model.DisplayName,
            model.ArchiveUrl,
            model.ArchiveSha256,
            Installed = new StreamingModelInstaller(model).IsVerified,
            DiskSizeBytes = new StreamingModelInstaller(model).DiskSizeBytes()
        });
        Console.WriteLine($"{model.DisplayName}: verified={new StreamingModelInstaller(model).IsVerified}");
        return new StreamingModelInstaller(model).IsVerified ? 0 : 2;
    }

    /// <summary>
    /// Real live-path qualification: the shipping MeetingLiveTranscriptionSession consumes a real
    /// 16 kHz recording in capture-sized packets and reports committed text, measured gaps, and
    /// throughput. Nothing is simulated; gaps are reported honestly if the queue cannot keep up.
    /// </summary>
    private static async Task<int> RunLiveTranscriptionBenchmarkAsync(IReadOnlyList<string> args)
    {
        var outputPath = Option(args, "--output");
        try
        {
            var audioPath = Path.GetFullPath(RequiredOption(args, "--audio"));
            var modelId = Option(args, "--model") ?? StreamingModelCatalog.Nemotron35Id;
            var model = StreamingModelCatalog.Get(modelId)
                ?? throw new InvalidOperationException($"Unknown streaming model id '{modelId}'.");
            var installer = new StreamingModelInstaller(model);
            if (!installer.IsVerified)
            {
                throw new InvalidOperationException($"{model.DisplayName} is not downloaded and verified.");
            }

            var samples = ReadMono16kSamples(audioPath);
            var durationMs = (int)Math.Round(samples.Length * 1000.0 / 16000);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await using var session = new MeetingLiveTranscriptionSession(
                model,
                LiveTranscriptOwnershipMode.UnifiedLiveAndFinal,
                capacity: 64);
            const int packetSamples = 1600; // 100 ms, the shipping capture packet size.
            for (var offset = 0; offset < samples.Length; offset += packetSamples)
            {
                var length = Math.Min(packetSamples, samples.Length - offset);
                var packet = samples.AsSpan(offset, length).ToArray();
                while (!session.TryEnqueue(new LivePcmSamplesEventArgs(
                           LiveTranscriptChannel.Microphone,
                           packet,
                           offset / packetSamples)))
                {
                    // Backpressure is expected on a slow provider; let the worker drain.
                    await Task.Delay(2);
                }
            }

            var result = await session.FinishAsync();
            started.Stop();

            var payload = new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AudioPath = audioPath,
                Model = model.Id,
                Provider = ExecutionProviderService.ActiveProvider(ExecutionProviderRole.LiveTranscription)
                    ?? ExecutionProviderService.Inspect().LoadedRuntime,
                AudioDurationMs = durationMs,
                ProcessingMs = started.ElapsedMilliseconds,
                RealtimeFactor = durationMs > 0 ? started.ElapsedMilliseconds / (double)durationMs : (double?)null,
                CommittedSegments = result.Committed.Count,
                CommittedCharacters = result.Committed.Sum(segment => segment.Text.Length),
                MeasuredGaps = result.Gaps.Count,
                DroppedPackets = result.DroppedPackets,
                Transcript = string.Join(
                    Environment.NewLine,
                    result.Committed
                        .OrderBy(segment => segment.StartSample)
                        .Select(segment => $"{(segment.Channel == LiveTranscriptChannel.Microphone ? "You" : "Others")}: {segment.Text}")),
                Telemetry = ExecutionProviderService.Snapshot()
            };
            await WriteOptionalPayloadAsync(outputPath, payload);
            Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return result.DroppedPackets == 0 ? 0 : 2;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static float[] ReadMono16kSamples(string path)
    {
        using var reader = new NAudio.Wave.AudioFileReader(path);
        using var resampler = new NAudio.Wave.MediaFoundationResampler(
            reader.ToMono().ToWaveProvider16(),
            new NAudio.Wave.WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        var buffer = new byte[16000 * 2];
        var samples = new List<float>(1024 * 1024);
        while (true)
        {
            var read = resampler.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var index = 0; index + 1 < read; index += 2)
            {
                samples.Add(BitConverter.ToInt16(buffer, index) / 32768f);
            }
        }

        return samples.ToArray();
    }

    private static async Task<int> RunCudaPackAsync(IReadOnlyList<string> args, CudaPackOperation operation)
    {
        var outputPath = Option(args, "--output");
        try
        {
            var installer = new CudaAccelerationPackInstaller();
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                var message = $"CUDA pack: {value.DisplayText}";
                Log.Info(message);
                Console.WriteLine(message);
            });
            switch (operation)
            {
                case CudaPackOperation.Prepare:
                    await installer.PrepareAsync(progress, default, Option(args, "--archive"));
                    break;
                case CudaPackOperation.Verify:
                    var verification = await installer.VerifyAsync();
                    await WriteOptionalPayloadAsync(outputPath, new
                    {
                        SchemaVersion = 1,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        Operation = "verify",
                        CudaAccelerationPack.PackVersion,
                        CudaAccelerationPack.ArchiveUrl,
                        CudaAccelerationPack.ArchiveSha256,
                        CudaAccelerationPack.SherpaRuntimeVersion,
                        CudaAccelerationPack.OnnxRuntimeVersion,
                        CudaAccelerationPack.CudaVersion,
                        CudaAccelerationPack.CudnnVersion,
                        Installed = CudaAccelerationPack.IsInstalled,
                        InstallDirectory = CudaAccelerationPack.InstallDirectory,
                        FixedSizeBytes = CudaAccelerationPack.InstalledSizeBytes,
                        DiskSizeBytes = Directory.Exists(CudaAccelerationPack.InstallDirectory)
                            ? Directory.EnumerateFiles(CudaAccelerationPack.InstallDirectory, "*", SearchOption.AllDirectories)
                                .Sum(path => new FileInfo(path).Length)
                            : 0,
                        MissingNvidiaFiles = CudaInstallerMissingNvidia().ToArray(),
                        Verification = new { verification.IsValid, verification.Diagnostic }
                    });
                    Console.WriteLine(verification.Diagnostic);
                    return verification.IsValid ? 0 : 2;
                default:
                    await installer.DeleteAsync();
                    await WriteOptionalPayloadAsync(outputPath, new
                    {
                        SchemaVersion = 1,
                        CreatedAtUtc = DateTimeOffset.UtcNow,
                        Operation = "delete",
                        Installed = CudaAccelerationPack.IsInstalled
                    });
                    Console.WriteLine("CUDA acceleration pack deleted.");
                    return 0;
            }

            var status = ExecutionProviderService.Inspect();
            await WriteOptionalPayloadAsync(outputPath, new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Operation = "prepare",
                CudaAccelerationPack.PackVersion,
                CudaAccelerationPack.ArchiveUrl,
                CudaAccelerationPack.ArchiveSha256,
                CudaAccelerationPack.SherpaRuntimeVersion,
                CudaAccelerationPack.OnnxRuntimeVersion,
                CudaAccelerationPack.CudaVersion,
                CudaAccelerationPack.CudnnVersion,
                Installed = CudaAccelerationPack.IsInstalled,
                InstallDirectory = CudaAccelerationPack.InstallDirectory,
                status.NvidiaRuntimeComplete,
                MissingNvidiaFiles = status.MissingNvidiaFiles
            });
            Console.WriteLine($"{CudaAccelerationPack.DisplayName}: installed={CudaAccelerationPack.IsInstalled}; nvidiaComplete={status.NvidiaRuntimeComplete}");
            return CudaAccelerationPack.IsInstalled ? 0 : 2;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(outputPath, exception);
            throw;
        }
    }

    private static IReadOnlyList<string> CudaInstallerMissingNvidia() =>
        NativeSherpaRuntime.ResolveMissingNvidiaDependencies(CudaAccelerationPack.InstallDirectory);

    /// <summary>Secret store used only to read non-secret settings during a CLI run.</summary>
    private sealed class NullSecretStore : ISecretStore
    {
        public string? Read(string key) => null;
        public void Write(string key, string value) { }
        public void Delete(string key) { }
        public bool IsConfigured(string key) => false;
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
                Provider = new
                {
                    Requested = ExecutionProviderService.ToSettingValue(ExecutionProviderService.Preference),
                    ExecutionProviderService.Inspect().LoadedRuntime,
                    ExecutionProviderService.Inspect().CudaPackInstalled,
                    ExecutionProviderService.Inspect().CudaPackVerified,
                    ExecutionProviderService.Inspect().NvidiaRuntimeComplete,
                    ExecutionProviderService.Inspect().DirectMlAvailable,
                    Telemetry = ExecutionProviderService.Snapshot()
                },
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
            --acceleration-status [--output <json>]
            --diagnose-native [--audio <wav>] [--model <id>] [--runs <n>] [--output <json>]
            --benchmark-native --audio <wav> [--reference <txt>] [--model <id>] [--runs <n>] [--output <json>]
            --benchmark-meeting [--mic-audio <path>] [--system-audio <path>] [--model <id>] [--runs <n>] [--output <json>]

          Every command accepts --provider auto|cpu|cuda to pin the execution provider for a run.
          """;
}
