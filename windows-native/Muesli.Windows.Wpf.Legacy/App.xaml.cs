using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Velopack;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Platform.Profiles;

namespace Muesli.Windows;

public partial class App : System.Windows.Application
{
    // Preview startup must not construct the log service: its constructor resolves the user log path.
    private Services.AppLogService? _logService;
    private Services.AppLogService LogService => _logService ??= new Services.AppLogService();
    private IDisposable? _sentryDisposable;
    private Services.SingleInstanceCoordinator? _singleInstance;
    private IMuesliProfileLease? _profileLease;
    private bool _activationPending;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    public static bool StartedInBackground
    {
        get
        {
            var hasBackgroundArg = Environment.GetCommandLineArgs().Any(arg =>
            arg.Equals("--background", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            if (hasBackgroundArg)
            {
                return true;
            }

            return LooksLikeLoginStartupWithoutBackgroundArg();
        }
    }

    private static bool LooksLikeLoginStartupWithoutBackgroundArg()
    {
        if (!Services.StartupRegistrationService.IsEnabled())
        {
            return false;
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return uptime < TimeSpan.FromMinutes(5);
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Preview is deliberately parsed before logging, single-instance, registry repair, or MainWindow composition.
        if (Services.Phase12PreviewMode.TryParse(e.Args, out var preview))
        {
            base.OnStartup(e);
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            var previewWindow = preview.CreateWindow();
            MainWindow = previewWindow;
            previewWindow.Show();
            return;
        }
        base.OnStartup(e);
        var appVersion = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        LogService.Info(
            $"Muesli starting. Background={StartedInBackground}. AppVersion={appVersion}. RuntimeVersion={Environment.Version}.");
        try
        {
            Services.AppShellIcon.RegisterForApplication();
        }
        catch (Exception exception)
        {
            LogService.Error("Shell icon registration failed; continuing without a custom window icon.", exception);
        }
        var commandLineArgs = Environment.GetCommandLineArgs();
        if (commandLineArgs.Any(arg =>
                arg.Equals("--prepare-model", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--verify-model", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunModelPreparationAsync(commandLineArgs);
            return;
        }

        if (commandLineArgs.Any(arg =>
                arg.Equals("--diagnose-native", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunNativeRuntimeQualificationAsync(commandLineArgs);
            return;
        }

        if (commandLineArgs.Any(arg =>
                arg.Equals("--benchmark-meeting", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunMeetingQualificationAsync(commandLineArgs);
            return;
        }

        if (commandLineArgs.Any(arg =>
                arg.Equals("--benchmark-native", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunNativeBenchmarkAsync(commandLineArgs);
            return;
        }

        _singleInstance = Services.SingleInstanceCoordinator.AcquireForCurrentUser();
        if (!_singleInstance.IsPrimary)
        {
            var signaled = _singleInstance.SignalActivationAsync().GetAwaiter().GetResult();
            LogService.Info(signaled
                ? "Existing Muesli instance was asked to show its dashboard; this process is exiting."
                : "Another Muesli UI instance owns the per-user mutex, but activation IPC was unavailable; this process is exiting safely.");
            Shutdown(0);
            return;
        }
        // The lease must cover the same profile selected by the composition root. Preview and UI
        // qualification runs set MUESLI_PROFILE_ROOT, while an unset variable resolves to the
        // production %APPDATA%\muesli location for the WPF fallback.
        _profileLease = WindowsProfileLeaseFactory.Instance.TryAcquire(
            MuesliProfilePaths.Current(),
            TimeSpan.Zero);
        if (_profileLease is null)
        {
            LogService.Info("Another Muesli edition is using this profile; startup stopped before opening data.");
            System.Windows.MessageBox.Show(
                "Another Muesli window is already using your profile. Close it before opening this edition.",
                "Muesli",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown(2);
            return;
        }
        _singleInstance.StartListening(() => Dispatcher.BeginInvoke(ActivatePrimaryDashboard));

        if (Services.StartupRegistrationService.IsEnabled() &&
            !Services.StartupRegistrationService.IsRegisteredForBackgroundLaunch())
        {
            try
            {
                Services.StartupRegistrationService.SetEnabled(true);
                LogService.Info("Repaired startup registration to use --background.");
            }
            catch (Exception exception)
            {
                LogService.Error("Could not repair startup registration.", exception);
            }
        }

        TryInitializeSentry();

        DispatcherUnhandledException += (_, args) =>
        {
            LogService.Error("Unhandled UI exception.", args.Exception);
            Sentry.SentrySdk.CaptureException(args.Exception);
            args.Handled = true;
            System.Windows.MessageBox.Show(
                "Muesli hit an unexpected error. The details were saved to the logs folder.",
                "Muesli",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                LogService.Error("Unhandled app-domain exception.", exception);
                Sentry.SentrySdk.CaptureException(exception);
            }
            else
            {
                LogService.Error($"Unhandled app-domain exception object: {args.ExceptionObject}");
            }
            Sentry.SentrySdk.Flush(TimeSpan.FromSeconds(2));
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Error("Unobserved task exception.", args.Exception);
            Sentry.SentrySdk.CaptureException(args.Exception);
            args.SetObserved();
        };

        Exit += (_, _) =>
        {
            _sentryDisposable?.Dispose();
        };

        var window = new MainWindow();
        MainWindow = window;
        if (_activationPending)
        {
            Dispatcher.BeginInvoke(ActivatePrimaryDashboard);
        }

        if (!window.OpenDashboardOnLaunch)
        {
            window.ParkForBackgroundLaunch();
            window.StartRuntime(showOnboarding: false);
            window.SetBackgroundStatus();
            return;
        }

        window.Show();
    }

    private void TryInitializeSentry()
    {
        try
        {
            var settings = new Services.SettingsStore(
                secretStore: new Services.WindowsCredentialSecretStore()).Load();
            if (!settings.CrashReportingEnabled)
            {
                return;
            }

            var dsn = ResolveSentryDsn();
            if (string.IsNullOrWhiteSpace(dsn))
            {
                LogService.Info("Crash reporting enabled but no Sentry DSN resolved; skipping Sentry init.");
                return;
            }

            var release = $"muesli-windows@{Assembly.GetExecutingAssembly().GetName().Version}";
            _sentryDisposable = Sentry.SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.AutoSessionTracking = true;
                options.SendDefaultPii = false;
                options.Release = release;
                options.Environment =
#if DEBUG
                    "dev";
#else
                    "prod";
#endif
                options.MaxBreadcrumbs = 50;
                options.SetBeforeSend(Services.SentryScrubber.Scrub);
                options.SetBeforeBreadcrumb(Services.SentryScrubber.ScrubBreadcrumb);
            });
            LogService.Info("Sentry crash reporting initialized.");
        }
        catch (Exception exception)
        {
            LogService.Error("Sentry initialization failed.", exception);
        }
    }

    private static string? ResolveSentryDsn()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MUESLI_SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var embedded = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "SentryDsn", StringComparison.Ordinal))
            ?.Value;
        return string.IsNullOrWhiteSpace(embedded) ? null : embedded;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _profileLease?.Dispose();
        _profileLease = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }

    private void ActivatePrimaryDashboard()
    {
        if (this.MainWindow is not Muesli.Windows.MainWindow window)
        {
            _activationPending = true;
            return;
        }

        _activationPending = false;
        window.ShowDashboardFromBackground();
    }

    private async Task RunMeetingQualificationAsync(string[] args)
    {
        string? outputPath = null;
        try
        {
            var micAudioPath = Option(args, "--mic-audio");
            var systemAudioPath = Option(args, "--system-audio");
            if (string.IsNullOrWhiteSpace(micAudioPath) && string.IsNullOrWhiteSpace(systemAudioPath))
            {
                throw new ArgumentException(
                    "Meeting qualification requires --mic-audio <path>, --system-audio <path>, or both.");
            }

            outputPath = Option(args, "--output") ??
                         Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "muesli",
                             "benchmarks",
                             $"meeting-qualification-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            var runs = int.TryParse(Option(args, "--runs"), out var configuredRuns)
                ? Math.Clamp(configuredRuns, 1, 20)
                : 3;
            var modelId = Option(args, "--model") ?? Services.TranscriptionModelCatalog.DefaultModelId;
            var service = new Services.MeetingQualificationService();
            var report = await service.RunAsync(
                micAudioPath,
                systemAudioPath,
                runs,
                modelId);
            var payload = new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Report = report
            };

            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            LogService.Info($"Meeting qualification report written. path={outputPath}; {report.Summary}");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            LogService.Error("Meeting qualification failed.", exception);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                    await File.WriteAllTextAsync(
                        fullOutputPath,
                        JsonSerializer.Serialize(new
                        {
                            SchemaVersion = 1,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            Error = exception.ToString()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // The app log retains the qualification failure.
                }
            }

            Shutdown(1);
        }
    }

    private async Task RunModelPreparationAsync(string[] args)
    {
        string? outputPath = null;
        try
        {
            var modelId = RequiredOption(args, "--model");
            var verifyOnly = args.Any(arg => arg.Equals("--verify-model", StringComparison.OrdinalIgnoreCase));
            outputPath = Option(args, "--output");
            using var lifecycle = new Services.TranscriptionModelLifecycleService();
            var progress = new Progress<Services.ModelDownloadProgress>(value =>
                LogService.Info($"Model preparation progress. model={modelId}; {value.DisplayText}"));
            if (verifyOnly)
            {
                await lifecycle.VerifyAsync(modelId, progress);
            }
            else
            {
                await lifecycle.PrepareAsync(modelId, progress);
            }
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
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(
                    outputPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            LogService.Info($"Explicit model {(verifyOnly ? "verification" : "preparation")} completed. model={modelId}; status={snapshot.Status}; recognizerActivated=false");
            Shutdown(snapshot.Status is Services.TranscriptionModelStatus.Ready or Services.TranscriptionModelStatus.Selected ? 0 : 2);
        }
        catch (Exception exception)
        {
            LogService.Error("Explicit model preparation failed.", exception);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                    await File.WriteAllTextAsync(
                        fullOutputPath,
                        JsonSerializer.Serialize(new
                        {
                            SchemaVersion = 1,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            Error = exception.ToString()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // The app log retains the preparation failure.
                }
            }
            Shutdown(1);
        }
    }

    private async Task RunNativeRuntimeQualificationAsync(string[] args)
    {
        string? outputPath = null;
        try
        {
            outputPath = Option(args, "--output") ??
                         Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "muesli",
                             "diagnostics",
                             $"native-runtime-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            var audioPath = Option(args, "--audio");
            var runs = int.TryParse(Option(args, "--runs"), out var configuredRuns)
                ? Math.Clamp(configuredRuns, 2, 50)
                : 10;
            var service = new Services.NativeRuntimeQualificationService();
            var report = await service.RunAsync(
                audioPath,
                runs,
                Option(args, "--model") ?? Services.TranscriptionModelCatalog.DefaultModelId);
            var payload = new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Report = report
            };

            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            LogService.Info(
                $"Native runtime qualification written. path={outputPath}; passed={report.Passed}; runtime={report.SelectedRuntime}; stressRuns={report.Stress?.Runs ?? 0}");
            Shutdown(report.Passed ? 0 : 2);
        }
        catch (Exception exception)
        {
            LogService.Error("Native runtime qualification failed.", exception);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                    await File.WriteAllTextAsync(
                        fullOutputPath,
                        JsonSerializer.Serialize(new
                        {
                            SchemaVersion = 1,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            Error = exception.ToString()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // The app log retains the qualification failure.
                }
            }

            Shutdown(1);
        }
    }

    private async Task RunNativeBenchmarkAsync(string[] args)
    {
        string? outputPath = null;
        try
        {
            var audioPath = RequiredOption(args, "--audio");
            outputPath = Option(args, "--output") ??
                         Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "muesli",
                             "benchmarks",
                             $"native-transcription-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            var referencePath = Option(args, "--reference");
            var referenceText = string.IsNullOrWhiteSpace(referencePath)
                ? null
                : await File.ReadAllTextAsync(Path.GetFullPath(referencePath));
            var runs = int.TryParse(Option(args, "--runs"), out var configuredRuns)
                ? Math.Clamp(configuredRuns, 1, 20)
                : 3;
            var modelId = Option(args, "--model") ?? Services.TranscriptionModelCatalog.DefaultModelId;

            var service = new Services.TranscriptionBenchmarkService(LogService);
            var report = await service.RunFileAsync(
                audioPath,
                runs,
                referenceText,
                modelId);
            var fullAudioPath = Path.GetFullPath(audioPath);
            var payload = new
            {
                SchemaVersion = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                AudioPath = fullAudioPath,
                AudioSha256 = Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(fullAudioPath))).ToLowerInvariant(),
                ReferencePath = string.IsNullOrWhiteSpace(referencePath)
                    ? null
                    : Path.GetFullPath(referencePath),
                Engine = report.Results.FirstOrDefault()?.EngineId,
                Model = report.Results.FirstOrDefault()?.ModelName ?? modelId,
                Runs = runs,
                SherpaOnnxRuntime = new
                {
                    Services.NativeSherpaRuntime.IsAvailable,
                    Services.NativeSherpaRuntime.IsCudaCapable,
                    Services.NativeSherpaRuntime.SelectedRuntime,
                    RuntimeDirectory = Services.NativeSherpaRuntime.CudaRuntimeDirectory,
                    Services.NativeSherpaRuntime.GpuDependencyCacheDirectory,
                    Services.NativeSherpaRuntime.Diagnostic
                },
                Report = report
            };

            outputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            LogService.Info($"Native transcription benchmark report written. path={outputPath}");
            Shutdown(report.Results.Any(result => result.Success) ? 0 : 2);
        }
        catch (Exception exception)
        {
            LogService.Error("Native transcription benchmark failed.", exception);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var fullOutputPath = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
                    await File.WriteAllTextAsync(
                        fullOutputPath,
                        JsonSerializer.Serialize(new
                        {
                            SchemaVersion = 1,
                            CreatedAtUtc = DateTimeOffset.UtcNow,
                            Error = exception.ToString()
                        }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch
                {
                    // The app log retains the benchmark failure if the report path is unwritable.
                }
            }

            Shutdown(1);
        }
    }

    private static string RequiredOption(IReadOnlyList<string> args, string name)
    {
        return Option(args, name) ??
               throw new ArgumentException($"Native benchmark requires {name} <value>.");
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
