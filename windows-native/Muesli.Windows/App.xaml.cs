using System.Reflection;
using Velopack;

namespace Muesli.Windows;

public partial class App : System.Windows.Application
{
    private readonly Services.AppLogService _logService = new();
    private IDisposable? _sentryDisposable;
    private static Services.SingleInstanceCoordinator? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        _singleInstance = Services.SingleInstanceCoordinator.AcquireForCurrentUser();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalActivationAsync().GetAwaiter().GetResult();
            _singleInstance.Dispose();
            return;
        }
        using (var conflictingProcess = Services.SingleInstanceCoordinator.FindConflictingLegacyProcess())
        {
            if (conflictingProcess is not null)
            {
                System.Windows.MessageBox.Show(
                    "Another Muesli build is already running. Close its tray icon or process before starting this build so two recorders cannot capture the same meeting.",
                    "Muesli is already running",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                _singleInstance.Dispose();
                _singleInstance = null;
                return;
            }
        }
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
        base.OnStartup(e);
        var assembly = Assembly.GetExecutingAssembly();
        var buildVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        _logService.Info($"Muesli starting. Background={StartedInBackground}. Build={buildVersion}. Runtime={Environment.Version}.");
        var pythonPath = Services.WorkerRuntimeLocator.FindPythonExecutable();
        _logService.Info($"Worker python resolved via '{Services.WorkerRuntimeLocator.LastResolutionSource}': {pythonPath}");

        var startupSettings = new Services.SettingsStore().Load();
        if (!startupSettings.StartAtLogin && Services.StartupRegistrationService.IsEnabled())
        {
            try
            {
                Services.StartupRegistrationService.SetEnabled(false);
                _logService.Info("Removed a stale startup registration because Start at login is disabled.");
            }
            catch (Exception exception)
            {
                _logService.Error("Could not remove stale startup registration.", exception);
            }
        }
        else if (startupSettings.StartAtLogin &&
                 (!Services.StartupRegistrationService.IsEnabled() ||
                  !Services.StartupRegistrationService.IsRegisteredForBackgroundLaunch()))
        {
            try
            {
                Services.StartupRegistrationService.SetEnabled(true);
                _logService.Info("Repaired startup registration to use --background.");
            }
            catch (Exception exception)
            {
                _logService.Error("Could not repair startup registration.", exception);
            }
        }

        TryInitializeSentry();

        DispatcherUnhandledException += (_, args) =>
        {
            _logService.Error("Unhandled UI exception.", args.Exception);
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
                _logService.Error("Unhandled app-domain exception.", exception);
                Sentry.SentrySdk.CaptureException(exception);
            }
            else
            {
                _logService.Error($"Unhandled app-domain exception object: {args.ExceptionObject}");
            }
            Sentry.SentrySdk.Flush(TimeSpan.FromSeconds(2));
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logService.Error("Unobserved task exception.", args.Exception);
            Sentry.SentrySdk.CaptureException(args.Exception);
            args.SetObserved();
        };

        Exit += (_, _) =>
        {
            _sentryDisposable?.Dispose();
            _singleInstance?.Dispose();
        };

        var window = new MainWindow();
        MainWindow = window;
        _singleInstance?.StartListening(() => Dispatcher.BeginInvoke(window.ShowDashboardFromBackground));

        if (StartedInBackground)
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
            var settings = new Services.SettingsStore().Load();
            if (!settings.CrashReportingEnabled)
            {
                return;
            }

            var dsn = ResolveSentryDsn();
            if (string.IsNullOrWhiteSpace(dsn))
            {
                _logService.Info("Crash reporting enabled but no Sentry DSN resolved; skipping Sentry init.");
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
            _logService.Info("Sentry crash reporting initialized.");
        }
        catch (Exception exception)
        {
            _logService.Error("Sentry initialization failed.", exception);
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
}
