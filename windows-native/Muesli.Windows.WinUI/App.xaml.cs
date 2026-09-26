using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;
using Windows.UI.ViewManagement;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.WinUI.Services;
using Microsoft.Windows.AppLifecycle;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Text;

namespace Muesli.Windows.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>The local history boundary used by WinUI pages and view-models.</summary>
    public static WinUiLibraryContext Library { get; private set; } = null!;

    public static IAppDialogService Dialogs { get; private set; } = null!;

    public static IClipboardService Clipboard { get; private set; } = null!;

    public static IFilePickerService FilePickers { get; private set; } = null!;

    public static IShareService Share { get; private set; } = null!;

    public static IUiDispatcher UiDispatcher { get; private set; } = null!;

    public static WinUiMeetingContext Meetings { get; private set; } = null!;

    public static WinUiModelsContext Models { get; private set; } = null!;

    public static WinUiSettingsContext Settings { get; private set; } = null!;

    public static WinUiDictationContext Dictation { get; private set; } = null!;

    public static WinUiComputerUseContext ComputerUse { get; private set; } = null!;

    public static WinUiMeetingDetectionService MeetingDetection { get; private set; } = null!;

    public static IStartupRegistrationService Startup { get; private set; } = null!;

    public static WindowsTrayIconService Tray { get; private set; } = null!;

    public static DictationIndicatorWindow? DictationIndicator { get; private set; }

    /// <summary>
    /// Hosts the WPF indicator companion when the WPF renderer is active. Null when the WinUI
    /// indicator is rendering in-process (the diagnostic fallback).
    /// </summary>
    public static WinUiIndicatorHost? IndicatorHost { get; private set; }

    public static MeetingLiveTranscriptWindow LiveTranscript { get; private set; } = null!;

    private AppInstance? _mainInstance;
    private SingleInstanceCoordinator? _desktopInstance;
    private MeetingAutoStopTracker? _autoStop;
    private bool _exiting;
    private readonly AppLogService _log = new();
    private UiExceptionPolicy _uiExceptionPolicy = null!;
    private OnboardingWindow? _onboarding;
    private ComputerUseConfirmationWindow? _computerUseConfirmation;
    private WinUiMeetingNotificationService? _meetingNotifications;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        _uiExceptionPolicy = new UiExceptionPolicy(
            logRecoverable: _log.Info,
            logFatal: message => _log.Error(message),
            requestShutdown: RequestFatalShutdown);
        UnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Documented exception-containment policy. Known transient failures (cancellation, timeout,
    /// permission denial, clipboard/shell/device COM failures) are contained so the dashboard stays
    /// usable; every other exception is fatal, logged once with a redacted diagnostic, and ends the
    /// process deliberately. Nothing is blanket-marked as handled.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs eventArgs)
    {
        var disposition = _uiExceptionPolicy.Handle(eventArgs.Exception);
        eventArgs.Handled = disposition == UiExceptionDisposition.Recoverable;
    }

    private static void RequestFatalShutdown()
    {
        try
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                try { (Window as MainWindow)?.CloseForExit(); }
                catch { /* shutdown must not throw */ }
                Environment.Exit(1);
            });
        }
        catch
        {
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var currentInstance = AppInstance.GetCurrent();
        _mainInstance = AppInstance.FindOrRegisterForKey("Muesli.WinUI.Primary");
        if (!_mainInstance.IsCurrent)
        {
            // Unpackaged hosts do not raise AppInstance.Activated reliably, so the primary shell
            // would stay on whatever page it had open. Signal the desktop activation pipe as well,
            // then redirect as before; a duplicate request is harmless because it only re-shows the
            // dashboard.
            using var secondary = SingleInstanceCoordinator.AcquireForCurrentUser();
            if (!secondary.IsPrimary)
            {
                await secondary.SignalActivationAsync();
            }

            await _mainInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
            Exit();
            return;
        }
        _mainInstance.Activated += (_, _) => UiDispatcher?.TryEnqueue(() =>
        {
            if (Window is MainWindow mainWindow) mainWindow.ShowDashboard();
        });

        _desktopInstance = SingleInstanceCoordinator.AcquireForCurrentUser();
        if (!_desktopInstance.IsPrimary)
        {
            await _desktopInstance.SignalActivationAsync();
            _desktopInstance.Dispose();
            Exit();
            return;
        }

        // Register the shared Swift text processor before any dictation can complete. Loads only from
        // the application directory; falls back to the parity-tested managed implementation.
        TranscriptTextProcessingBootstrap.Initialize(_log.Info);

        Library = new WinUiLibraryContext(MuesliProfilePaths.Current());
        Settings = new WinUiSettingsContext(Library);
        // Execution-provider and cleanup-model choices are fixed before the first native recognizer
        // is created: the packaged CPU and the optional CUDA sherpa builds cannot be mixed inside
        // one process, so a provider change is applied on the next launch.
        var startupSettings = Settings.Load();
        ExecutionProviderService.Configure(startupSettings.ExecutionProvider);
        NativeTextCleanupService.Configure(startupSettings.CleanupModelId);
        Startup = new WinUiStartupRegistrationService();
        Window = new MainWindow();
        TrackTheme(Window);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        UiDispatcher = new WinUiDispatcher(DispatcherQueue);
        _desktopInstance.StartListening(() => UiDispatcher.TryEnqueue(() => ((MainWindow)Window).ShowDashboard()));
        Meetings = new WinUiMeetingContext(Library, Settings, UiDispatcher);
        Models = new WinUiModelsContext(Library, Settings);
        Dictation = new WinUiDictationContext(Library, Settings, UiDispatcher);
        ComputerUse = new WinUiComputerUseContext(Library, Settings, Dictation);
        ComputerUse.ConfirmActionAsync = ConfirmComputerUseActionAsync;
        ComputerUse.CaptureStarted = () => ((MainWindow)Window).HideDashboard();
        ComputerUse.RegisterVoiceShortcut(UiDispatcher);
        MeetingDetection = new WinUiMeetingDetectionService();
        _meetingNotifications = new WinUiMeetingNotificationService(
            UiDispatcher,
            _log,
            () => Settings.Load().AutoMeetingDetectionEnabled,
            () => Meetings.IsRecording || Meetings.IsPaused,
            () => Meetings.IsBusy);
        Dialogs = new WinUiDialogService(() => Window.Content as FrameworkElement);
        Clipboard = new WinUiClipboardService();
        FilePickers = new WinUiFilePickerService(() => WindowHandle);
        Share = new WinUiShareService(() => WindowHandle);
        var renderer = IndicatorRendererKind.For(Environment.GetCommandLineArgs());
        if (renderer == IndicatorRendererKind.WinUi)
        {
            DictationIndicator = new DictationIndicatorWindow(Dictation, UiDispatcher);
            TrackTheme(DictationIndicator);
        }
        else
        {
            // WPF is the production-default renderer. The host launches the companion, and falls
            // back to the in-process WinUI pill (creating it on demand) if the companion can't run.
            IndicatorHost = new WinUiIndicatorHost(Dictation, Meetings, ComputerUse, UiDispatcher);
            IndicatorHost.Start();
        }
        LiveTranscript = new MeetingLiveTranscriptWindow(Meetings, UiDispatcher, Clipboard);
        TrackTheme(LiveTranscript);
        var mainWindow = (MainWindow)Window;
        Tray = new WindowsTrayIconService(
            UiDispatcher,
            () => Dictation.IsRecording,
            () => Meetings.IsRecording,
            () => Dictation.IsRecording ? Dictation.StopFromDashboardAsync() : Dictation.StartFromDashboardAsync(),
            async () =>
            {
                if (Meetings.IsRecording || Meetings.IsPaused) await Meetings.StopAndSaveAsync();
                else await Meetings.StartQuickNoteAsync();
            },
            mainWindow.ShowDashboard,
            () => _ = RequestExitAsync(),
            Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            () => Meetings.IsPaused,
            Meetings.PauseAsync,
            Meetings.ResumeAsync,
            () => ShowOnboarding(explicitResume: true),
            ShowFeatureTour);
        Dictation.Changed += (_, _) => Tray.Refresh();
        Meetings.Changed += (_, _) =>
        {
            Tray.Refresh();
            if (Meetings.IsRecording || Meetings.IsPaused) LiveTranscript.ShowForActiveMeeting();
        };
        ComputerUse.Changed += (_, _) => Tray.Refresh();
        ComputerUse.ConfirmationRequested += (_, request) => UiDispatcher.TryEnqueue(() =>
            ShowComputerUseConfirmation(request.Preview, request.Notice));
        MeetingDetection.MeetingDetected += (_, meeting) =>
            UiDispatcher.TryEnqueue(() => PresentDetectedMeeting(meeting));
        MeetingDetection.MeetingEnded += (_, key) =>
            UiDispatcher.TryEnqueue(() => _meetingNotifications?.Forget(key));
        MeetingDetection.ScanCompleted += (_, scan) => UiDispatcher.TryEnqueue(() =>
        {
            if (Meetings.IsRecording && !Meetings.IsBusy &&
                _autoStop?.Observe(scan.DetectedMeeting?.Key, DateTimeOffset.UtcNow) == true)
            {
                _autoStop = null;
                _ = StopDetectedMeetingAsync();
            }
        });
        Window.Closed += (_, _) =>
        {
                _onboarding?.Close();
                _computerUseConfirmation?.Close();
                _meetingNotifications?.Dispose();
                Tray.Dispose();
                DictationIndicator?.Dispose();
                IndicatorHost?.Dispose();
                LiveTranscript.Dispose();
                MeetingDetection.Dispose();
                ComputerUse.Dispose();
                Meetings.Dispose();
                Dictation.Dispose();
                Models.Dispose();
                Library.Dispose();
                _desktopInstance?.Dispose();
        };
        Window.Activate();
        _log.Info($"Muesli WinUI starting. AppVersion={typeof(App).Assembly.GetName().Version}. Profile={Library.Profile.RootDirectory}.");
        Dictation.RegisterHotkey();
        ApplyMeetingDetection(Settings.Load().AutoMeetingDetectionEnabled);
        StartFirstRunFlows();
        if (Settings.Load().OnboardingCompleted &&
            (currentInstance.GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask ||
             Environment.GetCommandLineArgs().Contains("--background") ||
             !Settings.Load().OpenDashboardOnLaunch))
            mainWindow.HideDashboard();

        // Development-only deterministic preview route for visual qualification. It never persists a
        // fake meeting and never starts capture.
        if (TryGetMeetingNotificationPreview(out var previewState))
        {
            UiDispatcher.TryEnqueue(() => ShowMeetingNotificationPreview(previewState));
        }
    }

    private async Task StopDetectedMeetingAsync()
    {
        try { await Meetings.StopAndSaveAsync(); }
        catch (Exception exception) { _log.Error("Detected meeting finalization failed; captured audio remains recoverable.", exception); }
    }

    private async Task RequestExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (Meetings.IsBusy)
            {
                ((MainWindow)Window).ShowDashboard("meetings");
                await Dialogs.ShowInfoAsync("Wait for the current meeting operation to finish before quitting.", "Meeting in progress");
                return;
            }
            if (Dictation.IsRecording || Dictation.IsBusy) await Dictation.CancelAsync();
            await Meetings.PreserveForShutdownAsync();
            ((MainWindow)Window).CloseForExit();
        }
        catch (Exception exception)
        {
            _log.Error("Muesli could not finish shutdown.", exception);
        }
        finally { _exiting = false; }
    }

    /// <summary>
    /// Maps the saved theme name onto a XAML theme. An unrecognised or empty value follows the
    /// Windows setting rather than silently forcing one of the two explicit themes.
    /// </summary>
    public static ElementTheme ResolveTheme(string? theme) =>
        theme?.Trim().ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    /// <summary>
    /// Applies the saved theme to a window's root element and keeps it in sync while the window
    /// lives. The root is used rather than an inner page so the custom title bar and every
    /// secondary window follow the same setting instead of staying on the system theme.
    /// </summary>
    public static void TrackTheme(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        void Apply(string? theme)
        {
            if (window.Content is FrameworkElement root)
            {
                root.RequestedTheme = ResolveTheme(theme);
            }
        }

        void OnSettingsChanged(object? sender, Muesli.Windows.Services.MuesliSettings settings) =>
            window.DispatcherQueue.TryEnqueue(() => Apply(settings.Theme));

        Apply(Settings.Load().Theme);
        Settings.Changed += OnSettingsChanged;
        window.Closed += (_, _) => Settings.Changed -= OnSettingsChanged;
    }

    /// <summary>
    /// Paints the OS caption buttons so they stay legible over an app-themed title bar. Shared by
    /// every window that sets <c>ExtendsContentIntoTitleBar</c>: the dashboard (P1-02) and, since
    /// P8-03, the setup window. High Contrast defers to the Windows colours rather than the app's.
    /// </summary>
    public static void ApplyCaptionButtonColors(AppWindow appWindow, ElementTheme actualTheme)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        var titleBar = appWindow.TitleBar;
        var uiSettings = new UISettings();
        var highContrast = new AccessibilitySettings().HighContrast;
        var isDark = actualTheme == ElementTheme.Dark;

        var foreground = highContrast
            ? uiSettings.GetColorValue(UIColorType.Foreground)
            : isDark ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE6, 0x00, 0x00, 0x00);
        var hover = highContrast
            ? uiSettings.GetColorValue(UIColorType.Accent)
            : isDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        var pressed = highContrast
            ? uiSettings.GetColorValue(UIColorType.Accent)
            : isDark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2A, 0x00, 0x00, 0x00);

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.InactiveForegroundColor = highContrast
            ? uiSettings.GetColorValue(UIColorType.Foreground)
            : isDark ? Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveForegroundColor = titleBar.InactiveForegroundColor;
    }

    public static void ApplyMeetingDetection(bool enabled)
    {
        if (enabled) MeetingDetection.Start();
        else MeetingDetection.Stop();
    }

    public static void ShowOnboarding(bool explicitResume = false)
    {
        if (Current is not App app) return;
        app.ShowOnboardingCore(explicitResume);
    }

    public static void ShowFeatureTour()
    {
        if (Window is MainWindow mainWindow)
        {
            mainWindow.ShowDashboard();
            mainWindow.StartFeatureTour();
        }
    }

    public static void ShowLiveTranscript() => LiveTranscript.ShowForActiveMeeting();

    public static void ShowComputerUseConfirmation(string preview, string notice)
    {
        if (Current is not App app) return;
        app._computerUseConfirmation?.Close();
        var window = new ComputerUseConfirmationWindow(preview, notice);
        TrackTheme(window);
        app._computerUseConfirmation = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(app._computerUseConfirmation, window))
            {
                app._computerUseConfirmation = null;
            }
        };
        window.Activate();
    }

    private async Task<bool> ConfirmComputerUseActionAsync(ComputerUseAction action, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await UiDispatcher.EnqueueAsync(() =>
        {
            _computerUseConfirmation?.Close();
            var window = new ComputerUseConfirmationWindow(ComputerUseConfirmationPreview.Build(action),
                "Only this action in the approved application will be allowed.", action.Risk);
            TrackTheme(window);
            _computerUseConfirmation = window;
            window.Closed += (_, _) =>
            {
                result.TrySetResult(window.Allowed);
                if (ReferenceEquals(_computerUseConfirmation, window)) _computerUseConfirmation = null;
            };
            window.Activate();
        }, token);
        using var registration = token.Register(() => UiDispatcher.TryEnqueue(() => _computerUseConfirmation?.Close()));
        return await result.Task.WaitAsync(token);
    }

    private void StartFirstRunFlows()
    {
        var settings = Settings.Load();
        if (!settings.OnboardingCompleted)
        {
            var progress = new OnboardingProgressStore(Library.Profile.OnboardingProgressPath).Load();
            if (!progress.Deferred)
            {
                ShowOnboardingCore(explicitResume: false);
            }
            return;
        }

        if (settings.LastCompletedFeatureTourVersion < FeatureTourCatalog.CurrentVersion)
        {
            ShowFeatureTour();
        }
    }

    private void ShowOnboardingCore(bool explicitResume)
    {
        if (Settings.Load().OnboardingCompleted && !explicitResume)
        {
            return;
        }

        if (_onboarding is not null)
        {
            _onboarding.Activate();
            return;
        }

        var store = new OnboardingProgressStore(Library.Profile.OnboardingProgressPath);
        var progress = store.Load();
        if (progress.Deferred && !explicitResume)
        {
            return;
        }

        if (explicitResume && progress.Deferred)
        {
            store.Save(progress with
            {
                Deferred = false,
                LastStatus = "Setup resumed. Continue from the saved step."
            });
        }

        _onboarding = new OnboardingWindow();
        TrackTheme(_onboarding);
        _onboarding.Closed += (_, _) =>
        {
            var finished = _onboarding?.Completed == true;
            _onboarding = null;
            if (finished) ShowFeatureTour();
        };
        _onboarding.Activate();
    }

    /// <summary>
    /// Presents the detected-meeting notification. The dedicated window owns its own surface, so a
    /// hidden or occluded dashboard no longer suppresses the prompt. For an already-joined meeting the
    /// only action is "Start Transcribing"; it never opens the meeting URL.
    /// </summary>
    private void PresentDetectedMeeting(WinUiDetectedMeeting detected)
    {
        if (detected.Meeting is not { } meeting || _meetingNotifications is null) return;
        var request = MeetingNotificationRequestFactory.FromDetectedMeeting(meeting);
        var callbacks = new MeetingNotificationCallbacks(
            OnAction: action => _ = HandleMeetingNotificationActionAsync(meeting, action),
            OnDismiss: () => { },
            OnAutoDismiss: () => { });
        _meetingNotifications.Present(request, callbacks);
    }

    private async Task HandleMeetingNotificationActionAsync(DetectedMeeting meeting, MeetingNotificationAction action)
    {
        try
        {
            if (action is MeetingNotificationAction.JoinAndRecord or MeetingNotificationAction.JoinOnly)
            {
                await LaunchMeetingUrlAsync(meeting);
            }

            // Join-only opens the meeting and never records or transcribes.
            if (action == MeetingNotificationAction.JoinOnly)
            {
                return;
            }

            await StartDetectedMeetingCaptureAsync(meeting);
        }
        catch (Exception exception)
        {
            _log.Error("Meeting notification action failed.", exception);
        }
    }

    private static async Task LaunchMeetingUrlAsync(DetectedMeeting meeting)
    {
        if (MeetingUrlParser.TryParse(meeting.BrowserUrl) is { } url)
        {
            await global::Windows.System.Launcher.LaunchUriAsync(new Uri(url.JoinUrl));
        }
    }

    private async Task StartDetectedMeetingCaptureAsync(DetectedMeeting meeting)
    {
        await Meetings.StartMeetingAsync(meeting.Title, meeting.ProcessId);
        _autoStop = new MeetingAutoStopTracker(MeetingRecordingStartOrigin.DetectedMeeting, meeting.Key);
        _autoStop.Observe(meeting.Key, DateTimeOffset.UtcNow);
        ShowLiveTranscript();
    }

    private static bool TryGetMeetingNotificationPreview(out string state)
    {
        state = "active";
        var args = Environment.GetCommandLineArgs();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--preview-meeting-notification", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = argument.IndexOf('=');
            if (separator >= 0 && separator < argument.Length - 1)
            {
                state = argument[(separator + 1)..];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                state = args[index + 1];
            }

            return true;
        }

        return false;
    }

    private void ShowMeetingNotificationPreview(string state)
    {
        if (_meetingNotifications is null) return;
        var requests = MeetingNotificationRequestFactory.Preview(state);
        _log.Info($"Meeting notification preview requested. state={state}; count={requests.Count}");
        var callbacks = new MeetingNotificationCallbacks(
            OnAction: action => _log.Info($"Meeting notification preview action. state={state}; action={action}"),
            OnDismiss: () => { },
            OnAutoDismiss: () => { });
        _meetingNotifications.Present(requests[0], callbacks, bypassSuppression: true);
    }
}
