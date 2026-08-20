using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Muesli.Windows.Features;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime : INotifyPropertyChanged
{
    private readonly IFeatureShellContext _shell;
    // Characterization boundary: model preparation remains inside the model feature runtime.
    // These callback names remain here as a stable contract for the preview/onboarding suite:
    // _streamingModelLifecycle.PrepareAsync and _modelLifecycle.PrepareAsync.
    // Search focus behavior is feature-owned by the search feature: MainSearchInput.Focus();
    // Keyboard.Focus(MainSearchInput);
    private readonly AppServices _appServices;
    private readonly FeatureServiceScope? _featureServices;
    private readonly NativeTranscriptionClient _dictationTranscriptionClient;
    private readonly DictationCoordinator _dictationCoordinator;
    private readonly GlobalHotkeyService _globalHotkeyService;
    private readonly SettingsStore _settingsStore;
    private readonly ILibraryHistoryAdapter _dataStore;
    private readonly TranscriptEditService _transcriptEditService;
    private readonly ToastNotificationService _toastNotificationService;
    private readonly ActiveAppPasteService _activeAppPasteService;
    private readonly NativeTranscriptionClient _meetingTranscriptionClient;
    private readonly MeetingRecordingCoordinator _meetingRecordingCoordinator;
    private readonly MeetingRecordingPlaybackService _meetingPlaybackService;
    private readonly TranscriptionModelLifecycleService _modelLifecycle;
    private readonly StreamingModelLifecycleService _streamingModelLifecycle;
    private readonly MeetingDetectionService _meetingDetectionService;
    private readonly MeetingPromptService _meetingPromptService;
    private readonly TrayIconService _trayIconService;
    private readonly OnboardingProgressStore _onboardingProgressStore;
    private readonly WindowsMicrophoneAccessService _microphoneAccessService;
    private readonly RuntimeDiagnosticsService _runtimeDiagnosticsService;
    private readonly PostMeetingAutomationService _postMeetingAutomationService;
    private readonly AppLogService _logService;
    private readonly CaptureStorageService _captureStorageService;
    private readonly TranscriptionBenchmarkService _transcriptionBenchmarkService;
    private readonly NativeTextCleanupService _textCleanupService;
    private readonly TranscriptionPipelineService _transcriptionPipelineService;
    private readonly DictationHotkeyStateMachine _dictationHotkeyState = new();
    private readonly Dictionary<string, DateTime> _ignoredMeetingPrompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _meetingAutoStopTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4)
    };
    private readonly System.Windows.Threading.DispatcherTimer _aliasSaveDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyReleaseTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(320)
    };
    private readonly System.Windows.Threading.DispatcherTimer _meetingPlaybackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private readonly SemaphoreSlim _summaryGate = new(1, 1);

    private string _dictationStatus = "Ready";
    private string? _selectedMicrophone;
    private string _selectedHotkey = "F8";
    private string _selectedPasteBehavior = "active-app";
    private TranscriptionModelDefinition _selectedTranscriptionModel = TranscriptionModelCatalog.Models[0];
    private TranscriptionModelDefinition _selectedFinalMeetingModel = TranscriptionModelCatalog.Models[0];
    private LiveModelChoice _selectedLiveMeetingModel = LiveModelChoice.Off;
    private string _selectedLiveTranscriptOwnership = "Preview-only";
    private bool _showLiveWaveformOnHover;
    private MeetingLiveTranscriptWindow? _liveTranscriptWindow;
    private string _selectedSummaryProvider = "local";
    private bool _isSummarizing;
    private bool _summaryRetryAvailable;
    private CancellationTokenSource? _summaryCancellation;
    private string _ollamaEndpoint = "http://localhost:11434";
    private string _ollamaModel = "llama3.1:8b";
    private string _selectedSummaryTemplate = "standard";
    private string _userName = "";
    private string _openAIApiKey = "";
    private string _openAIModel = "gpt-5.4-mini";
    private string _openRouterApiKey = "";
    private string _openRouterModel = "stepfun/step-3.5-flash:free";
    private bool _updatingSecretBoxes;
    private string _theme = "dark";
    private bool _enableDoubleTapDictation;
    private bool _removeFillerWords = true;
    private bool _enableLocalCleanup;
    private bool _startAtLogin;
    private bool _openDashboardOnLaunch = true;
    private bool _saveMeetingRecordings = true;
    private bool _postMeetingHookEnabled;
    private string _postMeetingHookExecutablePath = "";
    private string _selectedHookTranscriptPolicy = "Metadata only";
    private int _postMeetingHookTimeoutSeconds = 30;
    private int _postMeetingHookMaxAttempts = 2;
    private bool _autoExportMarkdownEnabled;
    private string _autoExportMarkdownDirectory = "";
    private string _selectedAutoExportContent = "Notes";
    private string _postMeetingAutomationStatus = "Automation is disabled.";
    private bool _computerUseEnabled;
    private string _selectedComputerUsePlannerProvider = "None";
    private string _computerUsePlannerModel = "";
    private int _computerUsePlannerTimeoutSeconds = 30;
    private int _computerUsePerActionTimeoutSeconds = 10;
    private int _computerUseMaximumActionCount = 5;
    private string _computerUseAllowedApplications = "";
    private string _computerUseAllowedBrowserDomains = "";
    private bool _computerUseIncludeWindowText;
    private bool _computerUseIncludeScreenshots;
    private bool _computerUseIncludeBrowserPageText;
    private string _selectedComputerUseBrowserInterface = "Disabled";
    private string _computerUseBrowserEndpoint = "http://127.0.0.1:9222";
    private string _computerUseStatus = "Computer Use is disabled.";
    private bool _computerUseVoiceCaptureActive;
    private bool _computerUseIsRunning;
    private ComputerUseActivationToken? _computerUseActivationToken;
    private ComputerUsePlannerService? _computerUsePlannerService;
    private ComputerUseWindowTarget? _computerUseApprovedTarget;
    private readonly HttpClient _computerUseHttpClient;
    private readonly ComputerUseTraceStore _computerUseTraceStore;
    private CancellationTokenSource? _computerUseCancellation;
    private readonly CancellationTokenSource _applicationShutdownCancellation = new();
    private bool _showFloatingIndicator = true;
    private bool _soundEnabled = true;
    private string _selectedIndicatorPosition = "Top Center";
    private bool _autoMeetingDetectionEnabled = true;
    private bool _onboardingCompleted;
    private int _lastCompletedFeatureTourVersion;
    private OnboardingWindow? _onboardingWindow;
    private MeetingDetectionScan? _lastMeetingDetectionScan;
    private IntPtr _pasteTargetWindow = IntPtr.Zero;
    private PasteTargetInfo _pasteTargetInfo = PasteTargetInfo.Unknown;
    private bool _shouldPasteToActiveApp;
    private bool _meetingsExpanded = true;
    private bool _meetingSortNewestFirst = true;
    private bool _isCapturingHotkey;
    private CancellationTokenSource? _dictationOperationCancellation;
    private bool _runtimeStarted;
    private readonly bool _isVisualPreview;
    private string _previewPageStateMessage = "";
    private bool _isCompactLayout;
    private int _selectedSettingsTabIndex;
    private string _dictationDateFilter = "all";
    private string _meetingDateFilter = "all";
    private string _searchQuery = "";
    private string? _selectedMeetingFolderId;
    private string _selectedMeetingTemplate = "Standard Meeting Notes";
    private MeetingItem? _selectedMeeting;
    private Dictionary<string, string> _activeSpeakerAliases = new();
    private bool _lastMeetingDetailShowTranscript = false;
    private bool _isMeetingRecording;
    private string _meetingSessionStatus = "Idle";
    private CancellationTokenSource? _meetingOperationCancellation;
    private MeetingAutoStopTracker? _meetingAutoStopTracker;
    private readonly List<RecoverableMeetingSession> _recoverableMeetingSessions = [];
    private MeetingPlaybackTrack? _selectedMeetingPlaybackTrack;
    private double _meetingPlaybackPosition;
    private double _meetingPlaybackDuration;
    private bool _updatingMeetingPlaybackPosition;
    private bool _lastSummaryUsedLocalFallback;
    private string? _currentMeetingTitle;
    private string? _persistenceWarning;
    private CancellationTokenSource? _importCancellation;
    private bool _isImportingMeeting;
    private double _importProgressPercent;
    private string _importProgressLabel = "";
    private bool _importProgressIsIndeterminate = true;
    private string _runtimeDiagnostics = "Not checked yet.";
    private string _modelCacheDirectory = "";
    private string _modelCacheSize = "0 B";
    private string _benchmarkSummary = "Benchmark not run yet.";
    private string _setupReadiness = "Setup not checked yet.";
    private string _nativeRuntimeStatus = "Not checked";
    private string _speakerDiarizationStatusLabel = "Not checked";
    private string _dictationModelRuntimeStatus = "Needs model";
    private string _qwenCleanupRuntimeStatus = "Disabled";
    private string _gpuRuntimeStatus = "Not checked";
    private string _diarizationDependencyStatus = "Not checked yet.";
    private string _diarizationTokenStatus = "Not checked yet.";
    private string _meetingDetectionStatus = "Meeting detection has not scanned yet.";
    private string _runtimeSetupStatus = "";
    private double? _indicatorLeft;
    private double? _indicatorTop;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set
        {
            if (!SetField(ref _isCompactLayout, value)) return;
            UpdateSidebarColumnWidth();
        }
    }

    private void UpdateSidebarColumnWidth()
    {
        _shell.SetCompactLayout(IsCompactLayout);
    }

    public ObservableCollection<DictationItem> Dictations { get; } = [];
    public ObservableCollection<MeetingItem> Meetings { get; } = [];
    public ObservableCollection<MeetingFolderItem> MeetingFolders { get; } = [];
    public ObservableCollection<MeetingTemplateItem> CustomMeetingTemplates { get; } = [];
    public ObservableCollection<DictionaryEntryItem> DictionaryEntries { get; } = [];
    public ObservableCollection<TranscriptionModelItem> TranscriptionModelItems { get; } = [];
    public ObservableCollection<StreamingModelItem> StreamingModelItems { get; } = [];
    public ObservableCollection<MeetingPlaybackTrack> MeetingPlaybackTracks { get; } = [];
    public ObservableCollection<string> MicrophoneDevices { get; } = ["System default microphone"];
    public ObservableCollection<string> HotkeyOptions { get; } =
    [
        "F6",
        "F7",
        "F8",
        "F9",
        "F10",
        "F11",
        "F12",
        "Ctrl+Shift+Space",
        "Ctrl+Alt+Space",
        "Ctrl+Shift+D",
        "Ctrl+Alt+D"
    ];
    public ObservableCollection<string> PasteBehaviors { get; } = ["active-app", "clipboard"];
    public IReadOnlyList<TranscriptionModelDefinition> TranscriptionModels { get; } = TranscriptionModelCatalog.Models;
    public IReadOnlyList<LiveModelChoice> LiveMeetingModels { get; } =
        [LiveModelChoice.Off, .. StreamingModelCatalog.Models.Select(model => new LiveModelChoice(model.Id, model.PickerLabel))];
    public IReadOnlyList<string> LiveTranscriptOwnershipModes { get; } = LiveTranscriptOwnershipDescriptor.DisplayNames;
    public ICollectionView FilteredDictations { get; }
    public ICollectionView FilteredMeetings { get; }
    public ICollectionView SearchDictationResults { get; }
    public ICollectionView SearchMeetingResults { get; }

    public string UserGreeting
    {
        get
        {
            var name = UserName.Trim();
            return string.IsNullOrWhiteSpace(name) ? "" : $"Hi, {name}";
        }
    }

    public string UserName
    {
        get => _userName;
        set
        {
            if (SetField(ref _userName, value?.Trim() ?? ""))
            {
                OnPropertyChanged(nameof(UserGreeting));
                SaveSettings();
            }
        }
    }

    public int DayStreak => ComputeDayStreak();
    public int WordsDictated => Dictations.Sum(item => item.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    public string WordsDictatedDisplay => WordsDictated >= 1000 ? $"{WordsDictated / 1000.0:0.0}k" : WordsDictated.ToString();
    public int AverageWpm
    {
        get
        {
            var totalMs = Dictations.Sum(item => Math.Max(item.DurationMs, 0));
            if (totalMs <= 0)
            {
                return 0;
            }

            var minutes = totalMs / 60000.0;
            return minutes <= 0 ? 0 : (int)Math.Round(WordsDictated / minutes);
        }
    }
    public string DictationHeaderLabel => "DICTATIONS";
    public string DictationFilterLabel => _dictationDateFilter == "all" ? "" : FilterLabel(_dictationDateFilter);
    public string MeetingFilterLabel => _meetingDateFilter == "all" ? "" : FilterLabel(_meetingDateFilter);
    public string SearchResultsTitle => string.IsNullOrWhiteSpace(SearchQuery) ? "Search" : $"Search results for \"{SearchQuery.Trim()}\"";
    public int SearchDictationCount => SearchDictationResults?.Cast<DictationItem>().Count() ?? 0;
    public int SearchMeetingCount => SearchMeetingResults?.Cast<MeetingItem>().Count() ?? 0;
    public string SearchResultsSummary => $"{SearchDictationCount} dictations · {SearchMeetingCount} meetings";
    public bool HasDictations => FilteredDictations?.Cast<DictationItem>().Any() ?? false;
    public bool HasMeetings => FilteredMeetings?.Cast<MeetingItem>().Any() ?? false;
    public bool HasSearchResults => SearchDictationCount > 0 || SearchMeetingCount > 0;
    public bool HasDictionaryEntries => DictionaryEntries.Count > 0;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value ?? ""))
            {
                FilteredDictations.Refresh();
                RefreshMeetingViews();
                SearchDictationResults.Refresh();
                SearchMeetingResults.Refresh();
                OnPropertyChanged(nameof(SearchResultsTitle));
                OnPropertyChanged(nameof(SearchDictationCount));
                OnPropertyChanged(nameof(SearchMeetingCount));
                OnPropertyChanged(nameof(SearchResultsSummary));
                OnPropertyChanged(nameof(HasDictations));
                OnPropertyChanged(nameof(HasMeetings));
                OnPropertyChanged(nameof(HasSearchResults));
                UpdateSearchPageVisibility();
            }
        }
    }
    public string MeetingRecordingButtonText => _meetingRecordingCoordinator.State switch
    {
        MeetingSessionState.Preparing => "Preparing…",
        MeetingSessionState.Recording or MeetingSessionState.DegradedRecording => "Stop recording",
        MeetingSessionState.Stopping => "Stopping…",
        MeetingSessionState.Finalizing => "Finalizing…",
        _ => "Record meeting"
    };
    public string MeetingSessionStatus
    {
        get => _meetingSessionStatus;
        private set => SetField(ref _meetingSessionStatus, value);
    }
    public int RecoverableMeetingCount => _recoverableMeetingSessions.Count;
    public bool HasRecoverableMeetings => RecoverableMeetingCount > 0;
    public string RecoverInterruptedButtonText => $"Recover interrupted ({RecoverableMeetingCount})";
    public bool HasMeetingPlayback => MeetingPlaybackTracks.Count > 0;
    public MeetingPlaybackTrack? SelectedMeetingPlaybackTrack
    {
        get => _selectedMeetingPlaybackTrack;
        set
        {
            if (!SetField(ref _selectedMeetingPlaybackTrack, value) || value is null)
            {
                return;
            }
            try
            {
                _meetingPlaybackService.Load(value);
                MeetingPlaybackPosition = 0;
                MeetingPlaybackDuration = _meetingPlaybackService.Duration.TotalSeconds;
                OnPropertyChanged(nameof(MeetingPlaybackButtonText));
                OnPropertyChanged(nameof(MeetingPlaybackTimeLabel));
            }
            catch (Exception exception)
            {
                DictationStatus = $"Recording playback failed: {exception.Message}";
                _toastNotificationService.Show("Playback failed", "The recording could not be opened", ToastState.Error, 3600);
            }
        }
    }
    public string MeetingPlaybackButtonText =>
        _meetingPlaybackService.State == MeetingPlaybackState.Playing ? "Pause" : "Play";
    public double MeetingPlaybackPosition
    {
        get => _meetingPlaybackPosition;
        set => SetField(ref _meetingPlaybackPosition, value);
    }
    public double MeetingPlaybackDuration
    {
        get => _meetingPlaybackDuration;
        private set => SetField(ref _meetingPlaybackDuration, value);
    }
    public string MeetingPlaybackTimeLabel =>
        $"{FormatPlaybackTime(TimeSpan.FromSeconds(Math.Max(0, MeetingPlaybackPosition)))} / {FormatPlaybackTime(TimeSpan.FromSeconds(Math.Max(0, MeetingPlaybackDuration)))}";
    public string DictationModelStatusLabel => DictationModelRuntimeStatus;
    public string QwenCleanupStatusLabel => QwenCleanupRuntimeStatus;
    public string ShortcutModeLabel => EnableDoubleTapDictation ? "Hold to talk, or double-tap to lock recording" : "Hold to record, release to transcribe";
    public bool SetupNeedsResume => _isVisualPreview || !_onboardingCompleted || _onboardingProgressStore.Load().Deferred;
    public string SetupResumeLabel => SetupNeedsResume ? "Setup is paused or incomplete" : "Setup complete";
    public string StartupRegistrationLabel => _isVisualPreview ? "Preview-only: startup registration was not inspected." : StartupRegistrationService.DescribeState();
    public string PreviewPageStateMessage => _previewPageStateMessage;
    private static string[] SettingsTabNames { get; } =
        ["General", "Dictation", "Meetings", "Computer Use", "Appearance"];

    public int SelectedSettingsTabIndex
    {
        get => _selectedSettingsTabIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, SettingsTabNames.Length - 1);
            if (!SetField(ref _selectedSettingsTabIndex, normalized))
            {
                return;
            }

            _appServices.Navigation.SetSettingsTab(SettingsTabNames[normalized]);
        }
    }

    public string CaptureHotkeyButtonText => _isCapturingHotkey ? "Press shortcut..." : "Record shortcut";
    public string ShortcutCaptureLabel => _isCapturingHotkey
        ? "Press a function key or a modifier shortcut such as Ctrl+Shift+Space. Press Esc to cancel."
        : "Choose a shortcut or record one that is free on this Windows laptop.";
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"v{version.ToString(3)}"
        : "Version unavailable";
    public string SelectedMeetingTranscript => ApplySpeakerAliases(_selectedMeeting?.Transcript ?? "", _activeSpeakerAliases);
    public string RuntimeDiagnostics
    {
        get => _runtimeDiagnostics;
        private set => SetField(ref _runtimeDiagnostics, value);
    }
    public string NativeRuntimeStatus
    {
        get => _nativeRuntimeStatus;
        private set => SetField(ref _nativeRuntimeStatus, value);
    }
    public string SpeakerDiarizationStatusLabel
    {
        get => _speakerDiarizationStatusLabel;
        private set => SetField(ref _speakerDiarizationStatusLabel, value);
    }
    public string DictationModelRuntimeStatus
    {
        get => _dictationModelRuntimeStatus;
        private set
        {
            if (SetField(ref _dictationModelRuntimeStatus, value))
            {
                OnPropertyChanged(nameof(DictationModelStatusLabel));
            }
        }
    }
    public string QwenCleanupRuntimeStatus
    {
        get => _qwenCleanupRuntimeStatus;
        private set
        {
            if (SetField(ref _qwenCleanupRuntimeStatus, value))
            {
                OnPropertyChanged(nameof(QwenCleanupStatusLabel));
            }
        }
    }
    public string GpuRuntimeStatus
    {
        get => _gpuRuntimeStatus;
        private set => SetField(ref _gpuRuntimeStatus, value);
    }
    public string ModelCacheDirectory
    {
        get => _modelCacheDirectory;
        private set => SetField(ref _modelCacheDirectory, value);
    }
    public string RuntimeSetupStatus
    {
        get => _runtimeSetupStatus;
        private set => SetField(ref _runtimeSetupStatus, value);
    }
    public string ModelCacheSize
    {
        get => _modelCacheSize;
        private set => SetField(ref _modelCacheSize, value);
    }
    public string BenchmarkSummary
    {
        get => _benchmarkSummary;
        private set => SetField(ref _benchmarkSummary, value);
    }
    public string DiarizationDependencyStatus
    {
        get => _diarizationDependencyStatus;
        private set => SetField(ref _diarizationDependencyStatus, value);
    }
    public string DiarizationTokenStatus
    {
        get => _diarizationTokenStatus;
        private set => SetField(ref _diarizationTokenStatus, value);
    }

    public string MeetingDetectionStatus
    {
        get => _meetingDetectionStatus;
        private set => SetField(ref _meetingDetectionStatus, value);
    }

    public string DictationStatus
    {
        get => _dictationStatus;
        private set => SetField(ref _dictationStatus, value);
    }

    public string SelectedTheme
    {
        get => _theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        set
        {
            var next = value.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
            if (_theme.Equals(next, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetTheme(next);
            OnPropertyChanged();
        }
    }

    public string? SelectedMicrophone
    {
        get => _selectedMicrophone;
        set
        {
            if (SetField(ref _selectedMicrophone, value))
            {
                SaveSettings();
            }
        }
    }

    public string SelectedHotkey
    {
        get => _selectedHotkey;
        set
        {
            var nextHotkey = NormalizeHotkey(value, allowCustom: true);
            if (_isVisualPreview)
            {
                if (SetField(ref _selectedHotkey, nextHotkey))
                    DictationStatus = "Preview-only: shortcut changes are disabled; no hook was registered.";
                return;
            }
            var previousHotkey = _selectedHotkey;
            if (!SetField(ref _selectedHotkey, nextHotkey))
            {
                return;
            }

            AddHotkeyOptionIfMissing(nextHotkey);
            if (!RegisterGlobalHotkey())
            {
                _selectedHotkey = previousHotkey;
                OnPropertyChanged(nameof(SelectedHotkey));
                RegisterGlobalHotkey();
                return;
            }

            DictationStatus = EnableDoubleTapDictation
                ? $"Hold {_selectedHotkey} to dictate, or double-tap for hands-free"
                : $"Hold {_selectedHotkey} to dictate";
            SaveSettings();
            OnPropertyChanged(nameof(ShortcutModeLabel));
            _toastNotificationService.ShowIdle(_selectedHotkey);
        }
    }

    public string SelectedPasteBehavior
    {
        get => _selectedPasteBehavior;
        set
        {
            if (SetField(ref _selectedPasteBehavior, value))
            {
                SaveSettings();
            }
        }
    }

    public bool EnableDoubleTapDictation
    {
        get => _enableDoubleTapDictation;
        set
        {
            if (SetField(ref _enableDoubleTapDictation, value))
            {
                ResetHotkeyDictationState();
                SaveSettings();
                OnPropertyChanged(nameof(ShortcutModeLabel));
                DictationStatus = value
                    ? $"Hold {SelectedHotkey} to dictate, or double-tap for hands-free"
                    : $"Hold {SelectedHotkey} to dictate";
            }
        }
    }

    public bool RemoveFillerWords
    {
        get => _removeFillerWords;
        set
        {
            if (SetField(ref _removeFillerWords, value))
            {
                SaveSettings();
            }
        }
    }

    public bool EnableLocalCleanup
    {
        get => _enableLocalCleanup;
        set
        {
            if (SetField(ref _enableLocalCleanup, value))
            {
                SaveSettings();
                QwenCleanupRuntimeStatus = NativeTextCleanupService.Status(_enableLocalCleanup);
                OnPropertyChanged(nameof(QwenCleanupStatusLabel));
                _ = RefreshRuntimeDiagnosticsAsync();
            }
        }
    }

    public bool StartAtLogin
    {
        get => _startAtLogin;
        set
        {
            if (!SetField(ref _startAtLogin, value))
            {
                return;
            }

            if (_isVisualPreview)
            {
                DictationStatus = "Preview-only: startup registration is disabled; no registry state was changed.";
                return;
            }

            try
            {
                StartupRegistrationService.SetEnabled(value);
                SaveSettings();
                DictationStatus = value ? "Muesli will start at login" : "Start at login disabled";
            }
            catch (Exception exception)
            {
                _startAtLogin = !value;
                OnPropertyChanged(nameof(StartAtLogin));
                DictationStatus = $"Could not update startup setting: {exception.Message}";
                _toastNotificationService.Show("Startup setting failed", exception.Message, ToastState.Error, 4200);
            }
        }
    }

    public FeatureRuntime(IFeatureShellContext shell, AppServices appServices)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));
        _isVisualPreview = false;
        _featureServices = _appServices.CreateFeatureScope(new FeatureServiceCallbacks(
            () => new HashSet<string>(
                [SelectedTranscriptionModel.Id, SelectedFinalMeetingModel.Id],
                StringComparer.OrdinalIgnoreCase),
            ReleaseTranscriptionModelAsync,
            () => SelectedLiveMeetingModel.Id,
            _ => Task.CompletedTask));
        _globalHotkeyService = _featureServices.GlobalHotkeyService;
        _settingsStore = _featureServices.SettingsStore;
        _dataStore = _featureServices.HistoryAdapter;
        _transcriptEditService = _featureServices.TranscriptEditService;
        _toastNotificationService = _featureServices.ToastNotificationService;
        _activeAppPasteService = _featureServices.ActiveAppPasteService;
        _meetingTranscriptionClient = _featureServices.MeetingTranscriptionClient;
        _meetingPlaybackService = _featureServices.MeetingPlaybackService;
        _meetingDetectionService = _featureServices.MeetingDetectionService;
        _meetingPromptService = _featureServices.MeetingPromptService;
        _trayIconService = _featureServices.TrayIconService;
        _onboardingProgressStore = _featureServices.OnboardingProgressStore;
        _microphoneAccessService = _featureServices.MicrophoneAccessService;
        _runtimeDiagnosticsService = _featureServices.RuntimeDiagnosticsService;
        _postMeetingAutomationService = _featureServices.PostMeetingAutomationService;
        _logService = _featureServices.LogService;
        _captureStorageService = _featureServices.CaptureStorageService;
        _computerUseHttpClient = _featureServices.ComputerUseHttpClient;
        _computerUseTraceStore = _featureServices.ComputerUseTraceStore;
        _dictationTranscriptionClient = _featureServices.DictationTranscriptionClient;
        _dictationCoordinator = _featureServices.DictationCoordinator;
        if (_dictationCoordinator.StartupCaptureCleanup.DeletedCount > 0)
        {
            _logService.Info($"Removed interrupted dictation temporary audio at startup. count={_dictationCoordinator.StartupCaptureCleanup.DeletedCount}");
        }
        if (_dictationCoordinator.StartupCaptureCleanup.FailedPaths.Count > 0)
        {
            _logService.Info($"Interrupted dictation temporary audio needs cleanup. failedCount={_dictationCoordinator.StartupCaptureCleanup.FailedPaths.Count}; pathsLogged=false");
        }
        _meetingRecordingCoordinator = _featureServices.MeetingRecordingCoordinator;
        _meetingRecordingCoordinator.StateChanged += OnMeetingSessionStateChanged;
        _meetingRecordingCoordinator.HealthChanged += OnMeetingAudioHealthChanged;
        _meetingRecordingCoordinator.LevelChanged += OnMeetingRecordingLevelChanged;
        _meetingRecordingCoordinator.LiveTranscriptChanged += OnLiveTranscriptChanged;
        _meetingRecordingCoordinator.LiveTranscriptionFailed += OnLiveTranscriptionFailed;
        _meetingPlaybackService.StateChanged += OnMeetingPlaybackStateChanged;
        _modelLifecycle = _featureServices.ModelLifecycle;
        _modelLifecycle.ModelChanged += OnTranscriptionModelChanged;
        _streamingModelLifecycle = _featureServices.StreamingModelLifecycle;
        _streamingModelLifecycle.ModelChanged += OnStreamingModelChanged;
        foreach (var model in TranscriptionModels)
        {
            TranscriptionModelItems.Add(new TranscriptionModelItem(_modelLifecycle.Snapshot(model.Id)));
        }
        foreach (var model in StreamingModelCatalog.Models)
        {
            StreamingModelItems.Add(new StreamingModelItem(_streamingModelLifecycle.Snapshot(model.Id)));
        }
        _transcriptionBenchmarkService = _featureServices.TranscriptionBenchmarkService;
        _textCleanupService = _featureServices.TextCleanupService;
        _transcriptionPipelineService = _featureServices.TranscriptionPipelineService;
        IsCompactLayout = _shell.Window.ActualWidth < 900;
        FilteredDictations = CollectionViewSource.GetDefaultView(Dictations);
        FilteredDictations.Filter = item => PassesDateFilter(item, _dictationDateFilter) && PassesSearch(item);
        if (FilteredDictations is ListCollectionView dictationView)
        {
            dictationView.SortDescriptions.Clear();
            dictationView.SortDescriptions.Add(new SortDescription(nameof(DictationItem.Timestamp), ListSortDirection.Descending));
            dictationView.GroupDescriptions.Clear();
            dictationView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DictationItem.DateGroupLabel)));
        }
        FilteredMeetings = CollectionViewSource.GetDefaultView(Meetings);
        FilteredMeetings.Filter = item => PassesDateFilter(item, _meetingDateFilter) && PassesMeetingFolder(item) && PassesSearch(item);
        SearchDictationResults = new ListCollectionView(Dictations);
        SearchDictationResults.Filter = item => PassesSearch(item) && !string.IsNullOrWhiteSpace(_searchQuery);
        SearchMeetingResults = new ListCollectionView(Meetings);
        SearchMeetingResults.Filter = item => PassesSearch(item) && !string.IsNullOrWhiteSpace(_searchQuery);
        _hotkeyReleaseTimer.Tick += HotkeyReleaseTimer_Tick;

        var settings = _settingsStore.Load();
        LoadPersistedData();
        var recoveredCandidates = _transcriptEditService.RecoverAbandonedFlights();
        if (recoveredCandidates.Any(result => result.Outcome == RetranscriptionOutcome.CandidateReady))
        {
            _logService.Info("Recovered ready retranscription candidate(s) at startup; no transcript was changed.");
        }
        _persistenceWarning = _dataStore.LastWarning ?? _settingsStore.LastWarning;
        if (!string.IsNullOrWhiteSpace(_persistenceWarning))
        {
            _dictationStatus = _persistenceWarning;
            _logService.Info($"Persistence recovery notice: {_persistenceWarning}");
        }
        foreach (var microphone in _dictationCoordinator.ListMicrophones())
        {
            if (!MicrophoneDevices.Contains(microphone))
            {
                MicrophoneDevices.Add(microphone);
            }
        }
        var savedMicrophone = settings.MicrophoneName;
        _selectedMicrophone = ShouldUseSavedMicrophone(savedMicrophone) && savedMicrophone is not null && MicrophoneDevices.Contains(savedMicrophone)
            ? savedMicrophone
            : _dictationCoordinator.PickPreferredMicrophone();
        _selectedHotkey = NormalizeHotkey(settings.Hotkey, allowCustom: true);
        AddHotkeyOptionIfMissing(_selectedHotkey);
        _selectedPasteBehavior = PasteBehaviors.Contains(settings.PasteBehavior) ? settings.PasteBehavior : "active-app";
        _selectedTranscriptionModel = TranscriptionModelCatalog.Get(settings.DictationModelId);
        _selectedFinalMeetingModel = TranscriptionModelCatalog.Get(settings.FinalMeetingModelId);
        _selectedLiveMeetingModel = LiveMeetingModels.FirstOrDefault(choice => choice.Id == settings.LiveMeetingModelId) ?? LiveModelChoice.Off;
        _selectedLiveTranscriptOwnership = LiveTranscriptOwnershipDescriptor.DisplayNameFor(
            LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(settings.LiveTranscriptOwnership));
        _showLiveWaveformOnHover = settings.ShowLiveWaveformOnHover;
        _dictationTranscriptionClient.SwitchModelAsync(_selectedTranscriptionModel.Id).GetAwaiter().GetResult();
        _meetingTranscriptionClient.SwitchModelAsync(_selectedFinalMeetingModel.Id).GetAwaiter().GetResult();
        _userName = string.IsNullOrWhiteSpace(settings.UserName) ? Environment.UserName.Trim() : settings.UserName.Trim();
        _onboardingCompleted = settings.OnboardingCompleted;
        _lastCompletedFeatureTourVersion = settings.LastCompletedFeatureTourVersion;
        _selectedSummaryProvider = SummaryProviders.Contains(settings.MeetingSummaryProvider) ? settings.MeetingSummaryProvider : "local";
        _ollamaEndpoint = settings.OllamaEndpoint;
        _ollamaModel = settings.OllamaModel;
        _selectedSummaryTemplate = NormalizeSummaryTemplateName(settings.MeetingSummaryTemplate);
        _openAIApiKey = settings.ResolvedOpenAIApiKey;
        _openAIModel = string.IsNullOrWhiteSpace(settings.OpenAIModel) ? "gpt-5.4-mini" : settings.OpenAIModel;
        _openRouterApiKey = settings.ResolvedOpenRouterApiKey;
        _openRouterModel = string.IsNullOrWhiteSpace(settings.OpenRouterModel) ? "stepfun/step-3.5-flash:free" : settings.OpenRouterModel;
        _theme = settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _enableDoubleTapDictation = settings.EnableDoubleTapDictation;
        _removeFillerWords = settings.RemoveFillerWords;
        _enableLocalCleanup = settings.EnableLocalCleanup;
        _qwenCleanupRuntimeStatus = NativeTextCleanupService.Status(_enableLocalCleanup);
        _startAtLogin = settings.StartAtLogin && StartupRegistrationService.IsEnabled();
        _openDashboardOnLaunch = settings.OpenDashboardOnLaunch;
        _saveMeetingRecordings = settings.SaveMeetingRecordings;
        _postMeetingHookExecutablePath = settings.PostMeetingHookExecutablePath;
        _selectedHookTranscriptPolicy = HookTranscriptPolicyDisplay(settings.PostMeetingHookTranscriptPolicy);
        _postMeetingHookTimeoutSeconds = settings.PostMeetingHookTimeoutSeconds;
        _postMeetingHookMaxAttempts = settings.PostMeetingHookMaxAttempts;
        _postMeetingHookEnabled = settings.PostMeetingHookEnabled && IsValidHookExecutable(_postMeetingHookExecutablePath);
        _autoExportMarkdownDirectory = settings.AutoExportMarkdownDirectory;
        _selectedAutoExportContent = AutoExportContentDisplay(settings.AutoExportMarkdownContent);
        _autoExportMarkdownEnabled = settings.AutoExportMarkdownEnabled && IsValidAutoExportDirectory(_autoExportMarkdownDirectory);
        _postMeetingAutomationStatus = settings.PostMeetingHookEnabled && !_postMeetingHookEnabled
            ? "The saved hook was disabled because its executable is unavailable."
            : settings.AutoExportMarkdownEnabled && !_autoExportMarkdownEnabled
                ? "Automatic Markdown export was disabled because its saved destination is invalid."
                : _postMeetingHookEnabled || _autoExportMarkdownEnabled
                    ? "Automation is ready and runs only after a newly completed meeting is saved."
                    : "Automation is disabled.";
        _selectedComputerUsePlannerProvider = ComputerUseProviderDisplay(settings.ComputerUsePlannerProvider);
        _computerUsePlannerModel = settings.ComputerUsePlannerModel;
        _computerUsePlannerTimeoutSeconds = settings.ComputerUsePlannerTimeoutSeconds;
        _computerUsePerActionTimeoutSeconds = settings.ComputerUsePerActionTimeoutSeconds;
        _computerUseMaximumActionCount = settings.ComputerUseMaximumActionCount;
        _computerUseAllowedApplications = settings.ComputerUseAllowedApplications;
        _computerUseAllowedBrowserDomains = settings.ComputerUseAllowedBrowserDomains;
        _computerUseIncludeWindowText = settings.ComputerUseIncludeWindowText;
        _computerUseIncludeScreenshots = settings.ComputerUseIncludeScreenshots;
        _computerUseIncludeBrowserPageText = settings.ComputerUseIncludeBrowserPageText;
        _selectedComputerUseBrowserInterface = ComputerUseBrowserInterfaceDisplay(settings.ComputerUseBrowserInterface);
        _computerUseBrowserEndpoint = settings.ComputerUseBrowserEndpoint;
        _computerUseEnabled = settings.ComputerUseEnabled && ComputerUseConfigurationIsReady(out _);
        _computerUseStatus = settings.ComputerUseEnabled && !_computerUseEnabled
            ? "Computer Use was disabled because its saved provider, model, key, or application allowlist is unavailable."
            : _computerUseEnabled
                ? "Computer Use is ready for an explicit planner voice session."
                : "Computer Use is disabled.";
        _showFloatingIndicator = settings.ShowFloatingIndicator;
        _soundEnabled = settings.SoundEnabled;
        _dictationCoordinator.SoundFeedback.Enabled = _soundEnabled;
        _selectedIndicatorPosition = IndicatorPositions.Contains(settings.IndicatorAnchor) ? settings.IndicatorAnchor : "Top Center";
        _autoMeetingDetectionEnabled = settings.AutoMeetingDetectionEnabled;
        _indicatorLeft = settings.IndicatorLeft;
        _indicatorTop = settings.IndicatorTop;
        _toastNotificationService.SetSavedPosition(_indicatorLeft, _indicatorTop);
        _toastNotificationService.SetIndicatorAnchor(_selectedIndicatorPosition, clearCustomPosition: false);
        _toastNotificationService.SetIdleIndicatorVisible(_showFloatingIndicator, showNow: false);
        _toastNotificationService.ConfigureActions(StopActiveRecordingFromIndicatorAsync, CancelActiveRecordingFromIndicatorAsync);
        _toastNotificationService.PositionChanged += OnIndicatorPositionChanged;
        _dictationCoordinator.DeviceListChanged += OnDictationDeviceListChanged;
        _dictationCoordinator.RouteChanged += OnDictationRouteChanged;
        _dictationCoordinator.LevelChanged += OnDictationLevelChanged;
        _meetingDetectionService.ScanCompleted += OnMeetingDetectionScanCompleted;
        _meetingAutoStopTimer.Tick += MeetingAutoStopTimer_Tick;
        _meetingPlaybackTimer.Tick += MeetingPlaybackTimer_Tick;
        _aliasSaveDebounceTimer.Tick += AliasSaveDebounceTimer_Tick;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        RefreshRecoverableMeetingSessions();
        _meetingPromptService.Reset();
OnPropertyChanged(nameof(SelectedMicrophone));
    OnPropertyChanged(nameof(SelectedHotkey));
    OnPropertyChanged(nameof(SelectedPasteBehavior));
    OnPropertyChanged(nameof(SelectedTranscriptionModel));
    OnPropertyChanged(nameof(SelectedFinalMeetingModel));
    OnPropertyChanged(nameof(FinalMeetingModelStatus));
    OnPropertyChanged(nameof(LiveMeetingModelStatus));
    OnPropertyChanged(nameof(SelectedLiveMeetingModel));
    OnPropertyChanged(nameof(SelectedLiveTranscriptOwnership));
    OnPropertyChanged(nameof(ShowLiveWaveformOnHover));
    OnPropertyChanged(nameof(LivePreviewOwnerLabel));
    OnPropertyChanged(nameof(FinalTranscriptOwnerLabel));
    OnPropertyChanged(nameof(GapRecoveryOwnerLabel));
    OnPropertyChanged(nameof(ActiveModelLabel));
    OnPropertyChanged(nameof(SelectedModelDescription));
    OnPropertyChanged(nameof(SelectedModelLanguages));
    OnPropertyChanged(nameof(SelectedModelDownloadSize));
    OnPropertyChanged(nameof(SelectedModelCacheStatus));
    OnPropertyChanged(nameof(UserName));
    OnPropertyChanged(nameof(UserGreeting));
    OnPropertyChanged(nameof(SelectedSummaryProvider));
    OnPropertyChanged(nameof(SelectedSummaryTemplate));
    OnPropertyChanged(nameof(OpenAIApiKey));
    OnPropertyChanged(nameof(OpenAIApiKeyStatus));
    OnPropertyChanged(nameof(OpenAIModel));
    OnPropertyChanged(nameof(OpenRouterApiKey));
    OnPropertyChanged(nameof(OpenRouterApiKeyStatus));
    OnPropertyChanged(nameof(OpenRouterModel));
    OnPropertyChanged(nameof(EnableDoubleTapDictation));
    OnPropertyChanged(nameof(RemoveFillerWords));
    OnPropertyChanged(nameof(EnableLocalCleanup));
    OnPropertyChanged(nameof(SelectedTheme));
    OnPropertyChanged(nameof(StartAtLogin));
    OnPropertyChanged(nameof(OpenDashboardOnLaunch));
    OnPropertyChanged(nameof(SaveMeetingRecordings));
    OnPropertyChanged(nameof(PostMeetingHookEnabled));
    OnPropertyChanged(nameof(PostMeetingHookExecutablePath));
    OnPropertyChanged(nameof(SelectedHookTranscriptPolicy));
    OnPropertyChanged(nameof(PostMeetingHookTimeoutSeconds));
    OnPropertyChanged(nameof(PostMeetingHookMaxAttempts));
    OnPropertyChanged(nameof(AutoExportMarkdownEnabled));
    OnPropertyChanged(nameof(AutoExportMarkdownDirectory));
    OnPropertyChanged(nameof(SelectedAutoExportContent));
    OnPropertyChanged(nameof(PostMeetingAutomationStatusText));
    OnPropertyChanged(nameof(ComputerUseEnabled));
    OnPropertyChanged(nameof(SelectedComputerUsePlannerProvider));
    OnPropertyChanged(nameof(ComputerUsePlannerModel));
    OnPropertyChanged(nameof(ComputerUsePlannerTimeoutSeconds));
    OnPropertyChanged(nameof(ComputerUsePerActionTimeoutSeconds));
    OnPropertyChanged(nameof(ComputerUseMaximumActionCount));
    OnPropertyChanged(nameof(ComputerUseAllowedApplications));
    OnPropertyChanged(nameof(ComputerUseAllowedBrowserDomains));
    OnPropertyChanged(nameof(ComputerUseIncludeWindowText));
    OnPropertyChanged(nameof(ComputerUseIncludeScreenshots));
    OnPropertyChanged(nameof(ComputerUseIncludeBrowserPageText));
    OnPropertyChanged(nameof(SelectedComputerUseBrowserInterface));
    OnPropertyChanged(nameof(ComputerUseBrowserEndpoint));
    OnPropertyChanged(nameof(ComputerUseStatusText));
    OnPropertyChanged(nameof(ComputerUseVoiceButtonText));
    OnPropertyChanged(nameof(ComputerUseStopEnabled));
    if (settings.PostMeetingHookEnabled != _postMeetingHookEnabled ||
        settings.AutoExportMarkdownEnabled != _autoExportMarkdownEnabled ||
        settings.ComputerUseEnabled != _computerUseEnabled)
    {
        SaveSettings();
    }
    OnPropertyChanged(nameof(ShowFloatingIndicator));
    OnPropertyChanged(nameof(SoundEnabled));
    OnPropertyChanged(nameof(SelectedIndicatorPosition));
    OnPropertyChanged(nameof(AutoMeetingDetectionEnabled));
    OnPropertyChanged(nameof(MeetingDetectionStatus));
    ApplyTheme(_theme);
    ShowPage(DictationsPage, DictationsNav);
    AttachFeatureViews();
    ShowPage(AppPage.Dashboard);
}

public void Dispose()
{
        if (_isVisualPreview)
        {
            DetachFeatureViews();
            return;
        }
        _logService.Info("Feature runtime closing.");
        _aliasSaveDebounceTimer.Stop();
        SaveActiveSpeakerAliases();
        _meetingAutoStopTimer.Stop();
        _meetingPlaybackTimer.Stop();
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        _applicationShutdownCancellation.Cancel();
        _computerUseCancellation?.Cancel();
        _meetingOperationCancellation?.Cancel();
        if (_meetingRecordingCoordinator.IsRecording)
        {
            _meetingRecordingCoordinator.PreserveForShutdownAsync().GetAwaiter().GetResult();
        }
        _meetingDetectionService.ScanCompleted -= OnMeetingDetectionScanCompleted;
        _dictationOperationCancellation?.Cancel();
        _dictationCoordinator.DeviceListChanged -= OnDictationDeviceListChanged;
        _dictationCoordinator.RouteChanged -= OnDictationRouteChanged;
        _dictationCoordinator.LevelChanged -= OnDictationLevelChanged;
        _meetingPromptService.Close();
        _modelLifecycle.ModelChanged -= OnTranscriptionModelChanged;
        _streamingModelLifecycle.ModelChanged -= OnStreamingModelChanged;
        _dictationOperationCancellation?.Dispose();
        _dictationOperationCancellation = null;
        _meetingRecordingCoordinator.StateChanged -= OnMeetingSessionStateChanged;
        _meetingRecordingCoordinator.HealthChanged -= OnMeetingAudioHealthChanged;
        _meetingRecordingCoordinator.LevelChanged -= OnMeetingRecordingLevelChanged;
        _meetingRecordingCoordinator.LiveTranscriptChanged -= OnLiveTranscriptChanged;
        _meetingRecordingCoordinator.LiveTranscriptionFailed -= OnLiveTranscriptionFailed;
        _liveTranscriptWindow?.Close();
        _liveTranscriptWindow = null;
        _meetingPlaybackService.StateChanged -= OnMeetingPlaybackStateChanged;
        _meetingOperationCancellation?.Dispose();
        _meetingOperationCancellation = null;
        DetachFeatureViews();
        _applicationShutdownCancellation.Dispose();
        // AppServices owns the production feature scope and the shell disposes it after this
        // method has detached event handlers and preserved any active meeting session.
}

    /// <summary>
    /// Creates the feature runtime for a presentation-only Phase 12 check.
    /// This constructor deliberately assigns no-op null sentinels instead of constructing
    /// production services.  It must remain free of stores, logs, native clients, tray,
    /// hooks, registry, device, cache, network, and SystemEvents subscriptions.
    /// </summary>
    internal static FeatureRuntime CreateVisualPreview(IFeatureShellContext shell, Phase12PreviewMode mode)
        => new(shell, AppServices.ForPreview(), mode);

    private FeatureRuntime(IFeatureShellContext shell, AppServices appServices, Phase12PreviewMode mode)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _appServices = appServices ?? throw new ArgumentNullException(nameof(appServices));
        _isVisualPreview = true;
        _featureServices = null;
        _dictationTranscriptionClient = null!;
        _dictationCoordinator = null!;
        _globalHotkeyService = null!;
        _settingsStore = null!;
        _dataStore = null!;
        _transcriptEditService = null!;
        _toastNotificationService = null!;
        _activeAppPasteService = null!;
        _meetingTranscriptionClient = null!;
        _meetingRecordingCoordinator = null!;
        _meetingPlaybackService = null!;
        _modelLifecycle = null!;
        _streamingModelLifecycle = null!;
        _meetingDetectionService = null!;
        _meetingPromptService = null!;
        _trayIconService = null!;
        _onboardingProgressStore = null!;
        _microphoneAccessService = null!;
        _runtimeDiagnosticsService = null!;
        _postMeetingAutomationService = null!;
        _logService = null!;
        _captureStorageService = null!;
        _transcriptionBenchmarkService = null!;
        _textCleanupService = null!;
        _transcriptionPipelineService = null!;
        _computerUseHttpClient = null!;
        _computerUseTraceStore = null!;

        _theme = mode.Theme;
        _previewPageStateMessage = mode.PageStateMessage;
        _userName = "Visual verification";
        _dictationStatus = mode.PresentationStatus;
        _runtimeDiagnostics = "Preview-only diagnostics. No production diagnostic service was created.";
        _setupReadiness = mode.PresentationStatus;
        _runtimeSetupStatus = mode.PresentationStatus;
        _modelCacheDirectory = "Preview isolation: no model cache inspected.";
        _modelCacheSize = "0 B";
        _meetingDetectionStatus = "Preview isolation: meeting detection is not running.";
        _selectedHotkey = mode.Page == "shortcuts" ? "Ctrl+Shift+Space" : "F8";
        if (mode.Page == "shortcuts" && mode.Case == "conflict")
            _dictationStatus = "Preview-only shortcut conflict: Ctrl+Shift+Space is unavailable. Choose another shortcut; preview cannot register or test it.";
        if (mode.Page == "settings" && mode.Case == "startup-unavailable")
            _dictationStatus = "Preview-only startup guidance: Windows registration was not inspected or changed in this isolated process.";
        if ((mode.Page == "dashboard" || mode.Page == "meetings") && mode.Case == "long-text")
            _dictationStatus = "Preview-only wrapping verification: this deliberately long message is not a dictation, meeting, transcript, insight, statistic, or persisted user activity. Resize the real page to review readable wrapping while all history remains empty.";
        _openDashboardOnLaunch = true;

        FilteredDictations = CollectionViewSource.GetDefaultView(Dictations);
        FilteredMeetings = CollectionViewSource.GetDefaultView(Meetings);
        SearchDictationResults = new ListCollectionView(Dictations);
        SearchMeetingResults = new ListCollectionView(Meetings);
        PopulatePreviewModels(mode);
        ApplyTheme(_theme);
        _shell.Views.VisualVerificationBannerText.Text = mode.Banner;
        _shell.Views.VisualVerificationBanner.Visibility = Visibility.Visible;
        IsCompactLayout = mode.Size == "narrow";
        ShowPreviewPage(mode.Page);
        DisableVisualPreviewActions();
        AttachFeatureViews();
    }

    private void PopulatePreviewModels(Phase12PreviewMode mode)
    {
        if (mode.Page != "models") return;
        var model = TranscriptionModels[0];
        var snapshot = mode.Case switch
        {
            "ready" => new TranscriptionModelSnapshot(model, TranscriptionModelStatus.Ready, "Preview-only ready; no cache was inspected.", 0, "0 B", "No production model service was created.", false, false, false, false, false, false),
            "downloading" => new TranscriptionModelSnapshot(model, TranscriptionModelStatus.Downloading, "Preview-only downloading: 42% (no download started).", 0, "0 B", "Cancel is visibly disabled in preview.", true, false, false, false, false, false),
            "failure" => new TranscriptionModelSnapshot(model, TranscriptionModelStatus.Failed, "Preview-only failure: retry and diagnostics are guidance only.", 0, "0 B", "No retry, log, or network action is available.", false, false, false, false, false, false),
            _ => new TranscriptionModelSnapshot(model, TranscriptionModelStatus.RuntimeUnavailable, "Preview-only offline/unavailable: connect before preparing.", 0, "0 B", "No network or model cache was inspected.", false, false, false, false, false, false)
        };
        TranscriptionModelItems.Add(new TranscriptionModelItem(snapshot));
        _selectedTranscriptionModel = model;
        _selectedFinalMeetingModel = model;
        _dictationModelRuntimeStatus = snapshot.StatusText;
    }

    private void ShowPreviewPage(string page)
    {
        switch (page)
        {
            case "dashboard": ShowPage(DictationsPage, DictationsNav); break;
            case "meetings": ShowPage(MeetingsPage, MeetingsNav); break;
            case "search": ShowPage(SearchPage, DictationsNav); break;
            case "dictionary": ShowPage(DictionaryPage, DictionaryNav); break;
            case "models": ShowPage(ModelsPage, ModelsNav); break;
            case "shortcuts": ShowPage(ShortcutsPage, ShortcutsNav); break;
            case "settings": ShowPage(SettingsPage, SettingsNav); break;
            case "about": ShowPage(AboutPage, AboutNav); break;
            default: ShowPage(DictationsPage, DictationsNav); break;
        }
    }

    private void DisableVisualPreviewActions()
    {
        // Preview windows use the real XAML pages, but every interactive control is inert.
        // This is defence in depth alongside individual side-effect guards.
        DisablePreviewControls(_shell.Window);
    }

    private static void DisablePreviewControls(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is System.Windows.Controls.Control control)
                control.IsEnabled = false;
            DisablePreviewControls(child);
        }
    }
public void StartRuntime(bool showOnboarding)
{
    if (_runtimeStarted)
    {
        if (showOnboarding)
        {
            ShowOnboardingIfNeeded();
        }
        _meetingPromptService.Reset();
        return;
    }
    _runtimeStarted = true;
    _logService.Info("Main window runtime starting.");
    _trayIconService.Initialize(_shell.Window, CreateProductExperienceState, NavigateFromTray);
    _meetingDetectionService.MeetingDetected += OnMeetingDetected;
    if (AutoMeetingDetectionEnabled)
    {
        _meetingDetectionService.Start();
    }
    if (RegisterGlobalHotkey())
    {
        _toastNotificationService.ShowIdle(SelectedHotkey);
    }
    if (showOnboarding)
    {
        ShowOnboardingIfNeeded();
    }
    _ = RefreshRuntimeDiagnosticsAsync();
    _ = EnsureTranscriptionReadyAsync();
    if (!string.IsNullOrWhiteSpace(_persistenceWarning))
    {
        _appServices.Dialogs.ShowWarning(_persistenceWarning, "Muesli data recovery");
    }
}
public void SetBackgroundStatus()
{
    DictationStatus = "Running in background";
}
public void ParkForBackgroundLaunch()
{
    _shell.Window.ShowInTaskbar = false;
    _shell.Window.WindowStartupLocation = WindowStartupLocation.Manual;
    _shell.Window.WindowState = WindowState.Normal;
    _shell.Window.Opacity = 0;
    _shell.Window.Left = -32000;
    _shell.Window.Top = -32000;
}
public void ShowDashboardFromBackground()
{
    _shell.Window.Opacity = 1;
    _shell.Window.ShowInTaskbar = true;
    _shell.Window.Show();
    // Show first so placement has a real HWND/PresentationSource instead of NaN or the
    // parked (-32000) coordinates. This also keeps per-monitor DPI conversion truthful.
    FitDashboardToWorkArea();
    _shell.Window.WindowState = WindowState.Normal;
    _shell.Window.Activate();
    ShowOnboardingIfNeeded();
}
private void FitDashboardToWorkArea()
{
    WindowPlacementService.FitToWorkArea(_shell.Window);
}



public string SetupReadiness
{
    get => _setupReadiness;
    private set => SetField(ref _setupReadiness, value);
}
private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
{
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
private static string FirstDiagnosticLine(string? diagnostic)
{
    if (string.IsNullOrWhiteSpace(diagnostic))
    {
        return "";
    }
    var lines = diagnostic
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.StartsWith("Native input RMS", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("Native input peak", StringComparison.OrdinalIgnoreCase) ||
                       line.StartsWith("Captured audio bytes", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (lines.Count == 0)
    {
        return diagnostic.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
    }
    return string.Join(" | ", lines);
}

private static string TraceValue(string? value)
{
    return (value ?? "")
        .Replace(';', ',')
        .Replace('\r', ' ')
        .Replace('\n', ' ')
        .Trim();
}
private static string ConciseUiError(Exception exception)
{
    var message = exception.Message
        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault() ?? "Something went wrong.";
    return message.Length <= 180 ? message : $"{message[..177]}...";
}
private static bool ShouldUseSavedMicrophone(string? microphoneName)
{
    return !string.IsNullOrWhiteSpace(microphoneName);
}

private static bool IsValidHookExecutable(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
    return string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}

private static bool IsValidAutoExportDirectory(string? path) =>
    !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

private static string HookTranscriptPolicyDisplay(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "inline" => "Inline transcript",
    "auto-export-path" => "Auto-export path",
    _ => "Metadata only"
};

private static string HookTranscriptPolicySetting(string? value) => value switch
{
    "Inline transcript" => "inline",
    "Auto-export path" => "auto-export-path",
    _ => "metadata-only"
};

private static string AutoExportContentDisplay(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "transcript" => "Transcript",
    "full-meeting" => "Full meeting",
    _ => "Notes"
};

private static string AutoExportContentSetting(string? value) => value switch
{
    "Transcript" => "transcript",
    "Full meeting" => "full-meeting",
    _ => "notes"
};



    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void SaveSettings()
    {
        if (_isVisualPreview) return;
        _settingsStore.Save(CurrentSettingsSnapshot());
    }

    private void LoadPersistedData()
    {
        foreach (var folder in _dataStore.LoadMeetingFolders())
        {
            MeetingFolders.Add(new MeetingFolderItem(folder.Id, folder.Name));
        }

        foreach (var dictation in _dataStore.LoadDictations().OrderByDescending(item => item.Timestamp))
        {
            Dictations.Add(new DictationItem(
                dictation.Id,
                dictation.Timestamp,
                dictation.Timestamp.ToString("hh:mm tt"),
                dictation.Text,
                dictation.ModelProfile,
                dictation.DurationMs));
        }

        foreach (var meeting in _dataStore.LoadMeetings().OrderByDescending(item => item.CreatedAt))
        {
            Meetings.Add(new MeetingItem(
                meeting.Id,
                meeting.Title,
                meeting.CreatedAt,
                meeting.Transcript,
                meeting.Summary,
                meeting.SourcePath,
                meeting.ModelProfile,
                meeting.DurationMs,
                meeting.FolderId,
                meeting.WordCount,
                meeting.TemplateName,
                meeting.SpeakerAliases,
                MeetingRecordingCoordinator.CleanupHealthWarnings(meeting.HealthWarnings, meeting.Transcript),
                meeting.SessionState,
                meeting.MicrophoneAudioPath,
                meeting.SystemAudioPath,
                meeting.SystemCaptureMode,
                meeting.RecoveredFromInterruption,
                meeting.LivePreviewModelId,
                meeting.LiveTranscriptOwnership,
                meeting.FinalTranscriptOwnerModelId,
                meeting.GapRecoveryModelId,
                meeting.ManualNotes,
                meeting.TitleIsManual,
                meeting.AutomationResult));
        }

        foreach (var entry in _dataStore.LoadDictionary())
        {
            DictionaryEntries.Add(new DictionaryEntryItem(entry));
        }
        OnPropertyChanged(nameof(HasDictionaryEntries));

        foreach (var template in _dataStore.LoadMeetingTemplates())
        {
            CustomMeetingTemplates.Add(new MeetingTemplateItem(template));
            if (!SummaryTemplates.Contains(template.Name))
            {
                SummaryTemplates.Add(template.Name);
            }
        }

        UpdateMeetingFolderCounts();
        OnPropertyChanged(nameof(DayStreak));
    }

    private void SaveDictations(bool afterExplicitDeletion = false)
    {
        var dictations = Dictations.Select(item => new PersistedDictation(
            item.Id,
            item.Timestamp,
            item.Text,
            item.DurationMs,
            item.ModelProfile));
        if (afterExplicitDeletion)
        {
            _dataStore.SaveDictations(dictations, afterExplicitDeletion: true);
        }
        else
        {
            _dataStore.SaveDictations(dictations);
        }
    }

    private void SaveMeetings(bool afterExplicitDeletion = false)
    {
        var meetings = Meetings.Select(item => new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = item.Id,
            Title = item.Title,
            CreatedAt = item.CreatedAt,
            DurationMs = item.DurationMs,
            Transcript = item.Transcript,
            Summary = item.Summary,
            SourcePath = item.SourcePath,
            ModelProfile = item.ModelProfile,
            FolderId = item.FolderId,
            WordCount = item.WordCount,
            TemplateName = item.TemplateName,
            SpeakerAliases = item.SpeakerAliases ?? new Dictionary<string, string>(),
            HealthWarnings = MeetingRecordingCoordinator.CleanupHealthWarnings(item.HealthWarnings, item.Transcript),
            SessionState = item.SessionState,
            MicrophoneAudioPath = item.MicrophoneAudioPath,
            SystemAudioPath = item.SystemAudioPath,
            SystemCaptureMode = item.SystemCaptureMode,
            RecoveredFromInterruption = item.RecoveredFromInterruption,
            LivePreviewModelId = item.LivePreviewModelId,
            LiveTranscriptOwnership = item.LiveTranscriptOwnership,
            FinalTranscriptOwnerModelId = item.FinalTranscriptOwnerModelId,
            GapRecoveryModelId = item.GapRecoveryModelId,
            ManualNotes = item.ManualNotes,
            TitleIsManual = item.TitleIsManual,
            AutomationResult = item.AutomationResult
        });
        if (afterExplicitDeletion)
        {
            _dataStore.SaveMeetings(meetings, afterExplicitDeletion: true);
        }
        else
        {
            _dataStore.SaveMeetings(meetings);
        }
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private int ComputeDayStreak()
    {
        if (Dictations.Count == 0)
            return 0;

        var dates = Dictations
            .Select(d => d.Timestamp.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var today = DateTime.Today;
        var anchor = today;
        var lastDate = dates.Last();

        if (lastDate == today)
        {
            anchor = today;
        }
        else if (lastDate == today.AddDays(-1))
        {
            anchor = today.AddDays(-1);
        }
        else
        {
            return 0;
        }

        var dateSet = new HashSet<DateTime>(dates);
        var streak = 0;
        var cursor = anchor;
        while (dateSet.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return streak;
    }

    private static string ApplySpeakerAliases(string transcript, Dictionary<string, string> aliases)
    {
        return SpeakerAliasService.Apply(transcript, aliases);
    }

    private static string ApplySpeakerAliasesToNotes(string notes, Dictionary<string, string> aliases)
    {
        return SpeakerAliasService.Apply(notes, aliases);
    }

    private static List<string> DetectSpeakerLabels(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new List<string>();

        var labels = new System.Collections.Generic.HashSet<string>();
        var pattern = new System.Text.RegularExpressions.Regex(@"\bSpeaker \d+\b");
        foreach (System.Text.RegularExpressions.Match match in pattern.Matches(transcript))
        {
            labels.Add(match.Value);
        }

        return labels.OrderBy(l => l).ToList();
    }

    private void SaveMeetingFolders()
    {
        _dataStore.SaveMeetingFolders(MeetingFolders.Select(folder => new PersistedMeetingFolder(folder.Id, folder.Name)));
    }

    private void SaveDictionary()
    {
        _dataStore.SaveDictionary(DictionaryEntries.Select(item => item.Record));
    }

    private void SaveMeetingTemplates()
    {
        _dataStore.SaveMeetingTemplates(CustomMeetingTemplates.Select(item => item.Record));
        foreach (var template in CustomMeetingTemplates)
        {
            if (!SummaryTemplates.Contains(template.Name))
            {
                SummaryTemplates.Add(template.Name);
            }
        }
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                return typed;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }
}
