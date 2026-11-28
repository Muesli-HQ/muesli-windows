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
using Muesli.Windows.Services.Persistence;
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

    /// <summary>Hosts the WPF indicator companion, the floating pill's only renderer.</summary>
    public static WinUiIndicatorHost? IndicatorHost { get; private set; }

    public static MeetingLiveTranscriptWindow LiveTranscript { get; private set; } = null!;

    internal static TelemetryDeckClient? Telemetry { get; private set; }
    private static readonly System.Net.Http.HttpClient TelemetryHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private AppInstance? _mainInstance;
    private SingleInstanceCoordinator? _desktopInstance;
    private MeetingAutoStopTracker? _autoStop;
    private string _lastMeetingTranscript = "";
    private string? _signalLossPromptId;
    private bool _meetingWasLive;
    private bool _ownAudioActive;
    private readonly HashSet<string> _repromptedMeetings = new(StringComparer.OrdinalIgnoreCase);
    private PersistedMeeting? _pendingCompletedMeeting;
    private DispatcherTimer? _completionRetry;
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

    /// <summary>
    /// Opens the library, or tells the user why it cannot be opened and returns null. Nothing else
    /// in the app exists yet, so the explanation is a plain system dialog.
    /// </summary>
    private WinUiLibraryContext? OpenLibraryOrExplain(IMuesliProfilePaths profile)
    {
        try
        {
            return new WinUiLibraryContext(profile);
        }
        catch (LibraryUnavailableException unavailable)
        {
            _log.Error($"Library unavailable at startup. outcome={unavailable.Result.Outcome}");
            const uint ok = 0x0, warning = 0x30, setForeground = 0x10000, topMost = 0x40000;
            var title = MuesliDevSlot.Current is { } slot ? $"Muesli ({slot})" : "Muesli";
            MessageBoxW(IntPtr.Zero, unavailable.Result.Message, title, ok | warning | setForeground | topMost);
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

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
        _mainInstance = AppInstance.FindOrRegisterForKey(
            MuesliDevSlot.Current is { } slot ? $"Muesli.WinUI.Primary.{slot}" : "Muesli.WinUI.Primary");
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

        if (OpenLibraryOrExplain(MuesliProfilePaths.Current()) is not { } library)
        {
            _desktopInstance.Dispose();
            Exit();
            return;
        }
        Library = library;
        Settings = new WinUiSettingsContext(Library);
        // ChatGPT sign-in for meeting notes keeps its tokens in the Windows credential store.
        ChatGptAuthManager.Shared = new ChatGptAuthManager(new WindowsCredentialSecretStore(Library.Profile.RootDirectory));
        // Execution-provider and cleanup-model choices are fixed before the first native recognizer
        // is created: the packaged CPU and the optional CUDA sherpa builds cannot be mixed inside
        // one process, so a provider change is applied on the next launch.
        var startupSettings = Settings.Load();
        // The channel is stamped at build time by the release packager. A build without a stamp,
        // which is every local, development-slot and CI build, sends no analytics at all.
        var debugBuild = false;
#if DEBUG
        debugBuild = true;
#endif
        var stampedChannel = typeof(App).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "MuesliTelemetryChannel")?.Value;
        var productionProfile = string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Library.Profile.RootDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(MuesliProfilePaths.Production().RootDirectory)),
            StringComparison.OrdinalIgnoreCase);
        var telemetry = new TelemetryDeckClient(
            Library.Profile.RootDirectory,
            typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            TelemetryChannelResolver.Resolve(stampedChannel, debugBuild, MuesliDevSlot.Current != null, productionProfile),
            () => Settings.Load().ShareUsageAnalytics,
            TelemetryHttp);
        Telemetry = telemetry;
        ExecutionProviderService.Configure(startupSettings.ExecutionProvider);
        NativeTextCleanupService.Configure(startupSettings.CleanupModelId);
        Themes.AccentPalette.Apply(startupSettings.RecordingColorHex);
        Startup = new WinUiStartupRegistrationService();
        Window = new MainWindow();
        TrackTheme(Window);
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        UiDispatcher = new WinUiDispatcher(DispatcherQueue);
        Settings.Changed += (_, settings) => DispatcherQueue.TryEnqueue(() => Themes.AccentPalette.Apply(settings.RecordingColorHex));
        _desktopInstance.StartListening(() => UiDispatcher.TryEnqueue(() => ((MainWindow)Window).ShowDashboard()));
        Meetings = new WinUiMeetingContext(Library, Settings, UiDispatcher);
        Models = new WinUiModelsContext(Library, Settings);
        Dictation = new WinUiDictationContext(Library, Settings, UiDispatcher);
        ComputerUse = new WinUiComputerUseContext(Library, Settings, Dictation);
        ComputerUse.ConfirmActionAsync = ConfirmComputerUseActionAsync;
        ComputerUse.CaptureStarted = () => ((MainWindow)Window).HideDashboard();
        // Computer Use is hidden for v1, so Ctrl+Shift+F8 is left free:
        // ComputerUse.RegisterVoiceShortcut(UiDispatcher);
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
        IndicatorHost = new WinUiIndicatorHost(Dictation, Meetings, ComputerUse, UiDispatcher);
        IndicatorHost.Start();
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
                else
                {
                    await Meetings.StartQuickNoteAsync();
                    mainWindow.ShowDashboard(MainPage.RecordingRoute);
                }
            },
            mainWindow.ShowDashboard,
            () => _ = RequestExitAsync(),
            Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"),
            () => Meetings.IsPaused,
            Meetings.PauseAsync,
            Meetings.ResumeAsync,
            () => ShowOnboarding(explicitResume: true),
            ShowFeatureTour,
            Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIconLightTaskbar.ico"));
        Dictation.Changed += (_, _) =>
        {
            Tray.Refresh();
            // Dictation must never look like a call starting: detection is held while Muesli itself
            // uses the microphone and for the cooldown after (macOS suppressWhileActive).
            var ownAudio = Dictation.IsRecording || Dictation.IsBusy;
            if (ownAudio && !_ownAudioActive) MeetingDetection.SuppressForOwnAudio();
            else if (!ownAudio && _ownAudioActive) MeetingDetection.ResumeAfterOwnAudio();
            _ownAudioActive = ownAudio;
        };
        Meetings.MeetingCompleted += (_, meeting) => PresentMeetingCompleted(meeting);
        Meetings.AskToKeepRecordingAsync = AskToKeepRecordingAsync;
        Meetings.Changed += (_, _) =>
        {
            Tray.Refresh();
            // macOS opens the meeting document when a recording starts. The shell moves to the
            // meeting's own page as capture goes live; it does not raise or focus the window, so a
            // recording started from a detection prompt stays in the background with the pill.
            var live = Meetings.IsRecording || Meetings.IsPaused;
            if (live && !_meetingWasLive)
            {
                _meetingWasLive = true;
                mainWindow.ShellPage?.NavigateTo(MainPage.RecordingRoute);
            }
            // Resuming from a pause passes through a busy, not-live moment; that is not a new start.
            else if (!live && !Meetings.IsBusy) _meetingWasLive = false;
            if (Meetings.ActiveMeetingId is null && _signalLossPromptId is not null)
            {
                _meetingNotifications?.Close();
                _signalLossPromptId = null;
            }
            if (!string.Equals(_lastMeetingTranscript, Meetings.LiveTranscript, StringComparison.Ordinal))
            {
                _lastMeetingTranscript = Meetings.LiveTranscript;
                if (!string.IsNullOrWhiteSpace(_lastMeetingTranscript))
                {
                    _autoStop?.NoteTranscriptActivity(DateTimeOffset.UtcNow);
                    if (Meetings.IsSourceMissing)
                    {
                        _autoStop?.SuppressWarning();
                        _meetingNotifications?.Close();
                        Meetings.SetSourceMissing(false);
                    }
                }
            }
            if (!Meetings.IsRecording && !Meetings.IsPaused && !Meetings.IsBusy)
            {
                _autoStop = null;
                if (Meetings.IsSourceMissing) _meetingNotifications?.Close();
                Meetings.SetSourceMissing(false);
            }
        };
        Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatusChanged += (_, _) =>
        {
            // Raised off the UI thread. Windows allows only a moment before sleeping, so checkpoint
            // synchronously here instead of queueing behind UI work.
            try
            {
                var status = Microsoft.Windows.System.Power.PowerManager.SystemSuspendStatus;
                if (status == Microsoft.Windows.System.Power.SystemSuspendStatus.Entering)
                    Meetings.SuspendForPowerAsync().Wait(TimeSpan.FromSeconds(5));
                else if (status is Microsoft.Windows.System.Power.SystemSuspendStatus.AutoResume
                    or Microsoft.Windows.System.Power.SystemSuspendStatus.ManualResume)
                    UiDispatcher.TryEnqueue(async () =>
                    {
                        try { await Meetings.ResumeAfterPowerAsync(); }
                        catch (Exception exception) { _log.Error("Meeting resume after sleep failed.", exception); }
                    });
            }
            catch (Exception exception) { _log.Error("Meeting sleep checkpoint failed; audio remains recoverable.", exception); }
        };
        Meetings.SourceWarningDismissed += (_, _) =>
        {
            _autoStop?.DismissWarning();
            _meetingNotifications?.Close();
        };
        ComputerUse.Changed += (_, _) => Tray.Refresh();
        ComputerUse.ConfirmationRequested += (_, request) => UiDispatcher.TryEnqueue(() =>
            ShowComputerUseConfirmation(request.Preview, request.Notice));
        MeetingDetection.MeetingDetected += (_, meeting) =>
            UiDispatcher.TryEnqueue(() => PresentDetectedMeeting(meeting));
        MeetingDetection.MeetingEnded += (_, key) =>
            UiDispatcher.TryEnqueue(() => { _meetingNotifications?.Forget(key); _repromptedMeetings.Remove(key); });
        MeetingDetection.ScanCompleted += (_, scan) => UiDispatcher.TryEnqueue(() =>
        {
            if (Meetings.IsRecording && !Meetings.IsBusy && _autoStop is { } tracker)
            {
                var missing = tracker.Observe(scan.DetectedMeeting?.Key, DateTimeOffset.UtcNow);
                if (missing && !Meetings.IsSourceMissing) PresentMeetingSignalLost();
                else if (!missing && Meetings.IsSourceMissing)
                {
                    _meetingNotifications?.Close();
                    Meetings.SetSourceMissing(false);
                }
            }
        });
        Window.Closed += (_, _) =>
        {
                _onboarding?.Close();
                _computerUseConfirmation?.Close();
                _meetingNotifications?.Dispose();
                Tray.Dispose();
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
        _ = telemetry.SessionStarted();
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
        // fake meeting and never starts capture. Compiled out of Release so shipped builds can never
        // show a made-up meeting.
#if DEBUG
        if (TryGetMeetingNotificationPreview(out var previewState))
        {
            UiDispatcher.TryEnqueue(() => ShowMeetingNotificationPreview(previewState));
        }
#endif
    }

    /// <summary>
    /// macOS shows "Transcription complete" with the meeting title and a "View Notes" button in the
    /// meeting panel once a meeting's notes are saved. A meeting-detected or signal-lost prompt
    /// already on screen is not replaced; the notice waits for it (macOS queues it the same way).
    /// </summary>
    private void PresentMeetingCompleted(PersistedMeeting meeting)
    {
        if (_meetingNotifications is null) return;
        if (_meetingNotifications.IsVisible)
        {
            _pendingCompletedMeeting = meeting;
            _completionRetry ??= CreateCompletionRetryTimer();
            _completionRetry.Start();
            return;
        }

        _pendingCompletedMeeting = null;
        _signalLossPromptId = null;
        var title = string.IsNullOrWhiteSpace(meeting.Title) ? "Meeting" : meeting.Title.Trim();
        var request = new MeetingNotificationRequest($"meeting-completed:{meeting.Id}", MeetingNotificationKind.MeetingCompleted,
            "Transcription complete", title, "Muesli", "", "#34C759", "", "View Notes", null);
        _meetingNotifications.Forget(request.PromptId);
        _meetingNotifications.Present(request, new MeetingNotificationCallbacks(
            OnAction: action =>
            {
                if (action != MeetingNotificationAction.ViewNotes) return;
                var mainWindow = (MainWindow)Window;
                mainWindow.ShowDashboard("meetings");
                mainWindow.ShellPage?.OpenMeeting(meeting.Id);
            },
            OnDismiss: () => { },
            OnAutoDismiss: () => { }), bypassSuppression: true);
    }

    /// <summary>
    /// macOS promptToSaveMeetingRecording, worded the same. The window is shown so the dialog is
    /// visible; if it cannot be shown the audio is kept, as on macOS.
    /// </summary>
    private Task<bool> AskToKeepRecordingAsync(string title)
    {
        var answer = new TaskCompletionSource<bool>();
        if (!UiDispatcher.TryEnqueue(async () =>
            {
                try
                {
                    if (Window is MainWindow main) { main.AppWindow.Show(true); main.Activate(); }
                    var name = string.IsNullOrWhiteSpace(title) ? "this meeting" : $"“{title.Trim()}”";
                    var choice = await Dialogs.ConfirmAsync(
                        $"Save an audio file for {name} for playback and re-transcription. Your notes and transcript are saved either way.",
                        "Keep meeting audio?", "Keep Audio", "Don't Keep Audio");
                    answer.TrySetResult(choice == AppDialogChoice.Primary);
                }
                catch (Exception exception)
                {
                    _log.Error("The keep-audio question could not be shown; the audio is kept.", exception);
                    answer.TrySetResult(true);
                }
            }))
        {
            answer.TrySetResult(true);
        }
        return answer.Task;
    }

    private DispatcherTimer CreateCompletionRetryTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (_pendingCompletedMeeting is not { } pending) { timer.Stop(); return; }
            if (_meetingNotifications?.IsVisible == true) return;
            timer.Stop();
            PresentMeetingCompleted(pending);
        };
        return timer;
    }

    private async Task StopDetectedMeetingAsync()
    {
        try { await Meetings.StopAndSaveAsync(); }
        catch (Exception exception) { _log.Error("Detected meeting finalization failed; captured audio remains recoverable.", exception); }
    }

    private void PresentMeetingSignalLost()
    {
        if (Meetings.ActiveMeetingId is not { } id || _meetingNotifications is null) return;
        Meetings.SetSourceMissing(true);
        var promptId = $"meeting-signal-lost:{id}";
        _signalLossPromptId = promptId;
        _meetingNotifications.Forget(promptId);
        _meetingNotifications.Present(new MeetingNotificationRequest(promptId, MeetingNotificationKind.SignalLost,
            "Meeting signal lost", "Recording continues. Stop if the meeting ended.", "Meeting", "\uE7BA",
            "#3380FF", "M", "Stop Transcribing", null, DismissAfterSeconds: 30), new MeetingNotificationCallbacks(
            OnAction: action =>
            {
                if (Meetings.ActiveMeetingId == id && action == MeetingNotificationAction.StopTranscribing)
                {
                    Meetings.KeepRecording();
                    _ = StopDetectedMeetingAsync();
                }
            },
            OnDismiss: () => { if (Meetings.ActiveMeetingId == id) Meetings.KeepRecording(); },
            OnAutoDismiss: () =>
            {
                if (Meetings.ActiveMeetingId != id) return;
                _autoStop?.SuppressWarning();
                Meetings.SetSourceMissing(false);
            }));
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
                LastStatus = ""
            });
        }

        // Setup is always dark, as on macOS (OnboardingWindow sets RequestedTheme on its root), so it
        // does not follow the app theme.
        _onboarding = new OnboardingWindow();
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
        if (MeetingDetectionApps.IsMuted(Settings.Load().MutedMeetingDetectionApps, meeting.ProcessName))
        {
            _log.Info($"Meeting detection muted for this app. app={MeetingDetectionApps.ForProcess(meeting.ProcessName)?.Id}");
            return;
        }
        var request = MeetingNotificationRequestFactory.FromDetectedMeeting(meeting);
        var callbacks = new MeetingNotificationCallbacks(
            OnAction: action => _ = HandleMeetingNotificationActionAsync(meeting, action),
            OnDismiss: () => { },
            OnAutoDismiss: () => _ = RepromptBrowserMeetingLaterAsync(meeting));
        _meetingNotifications.Present(request, callbacks);
    }

    /// <summary>
    /// A browser meeting whose prompt timed out unanswered is offered once more after the macOS
    /// cooldown, if it is still going on. Desktop meeting apps and dismissed prompts stay quiet.
    /// </summary>
    private async Task RepromptBrowserMeetingLaterAsync(DetectedMeeting meeting)
    {
        // Once per meeting: a prompt the user ignored twice is an answer.
        if (string.IsNullOrWhiteSpace(meeting.BrowserUrl) || !_repromptedMeetings.Add(meeting.Key)) return;
        await Task.Delay(MeetingDetectionSuppression.BrowserRepromptDelay);
        UiDispatcher.TryEnqueue(() =>
        {
            if (Meetings.IsRecording || Meetings.IsPaused || Meetings.IsBusy) return;
            _meetingNotifications?.Forget(meeting.Key);
            MeetingDetection.Reprompt(meeting.Key);
        });
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
        // macOS starts a detected meeting in the background (.backgroundPill): only the floating
        // pill appears. The live transcript is one hover or click away, not pushed over the call.
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
