using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Muesli.Windows.Services;
using Velopack;
using Velopack.Sources;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DictationCoordinator _dictationCoordinator = new();
    private readonly GlobalHotkeyService _globalHotkeyService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly AppDataStore _dataStore = new();
    private readonly ToastNotificationService _toastNotificationService = new();
    private readonly ActiveAppPasteService _activeAppPasteService = new();
    private readonly TranscriptionWorkerClient _meetingTranscriptionClient = new();
    private readonly MeetingRecordingCoordinator _meetingRecordingCoordinator;
    private readonly MeetingSessionJournalStore _meetingSessionJournal = new();
    private readonly MeetingDetectionService _meetingDetectionService = new();
    private readonly MeetingPromptService _meetingPromptService = new();
    private readonly TrayIconService _trayIconService = new();
    private readonly RuntimeDiagnosticsService _runtimeDiagnosticsService = new();
    private readonly AppLogService _logService = new();
    private readonly MeetingRecordingPlaybackService _meetingPlayback = new();
    private readonly System.Windows.Threading.DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
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

    private string _dictationStatus = "Ready";
    private string? _selectedMicrophone;
    private string _selectedAsrEngine = "whisper";
    private string _selectedModelProfile = "base";
    private string _selectedDictationLanguage = "en";
    private string _selectedMeetingAsrEngine = "whisper";
    private string _selectedMeetingModelProfile = "base";
    private string? _selectedMeetingMicrophone;
    private string _selectedHotkey = "F8";
    private string _selectedPasteBehavior = "active-app";
    private string _selectedSummaryProvider = "local";
    private string _selectedSummaryTemplate = "standard";
    private string _userName = "";
    private string _openAIApiKey = "";
    private string _openAIModel = "gpt-5.4-mini";
    private string _openRouterApiKey = "";
    private string _openRouterModel = "stepfun/step-3.5-flash:free";
    private string _customSummaryEndpoint = "http://localhost:11434/v1/chat/completions";
    private string _customSummaryModel = "llama3.2";
    private string _customSummaryApiKey = "";
    private string _theme = "dark";
    private bool _postProcessingEnabled;
    private bool _removeFillerWords = true;
    private bool _enableDoubleTapDictation;
    private string _postProcessingPrompt = "";
    private bool _startAtLogin;
    private bool _openDashboardOnLaunch = true;
    private bool _saveMeetingRecordings = true;
    private string _recordingSavePolicy = "always";
    private bool _autoExportMeetings;
    private string _autoExportDirectory = "";
    private bool _showFloatingIndicator = true;
    private string _selectedIndicatorPosition = "Top Center";
    private bool _autoMeetingDetectionEnabled = true;
    private bool _onboardingCompleted;
    private IntPtr _pasteTargetWindow = IntPtr.Zero;
    private bool _shouldPasteToActiveApp;
    private bool _meetingsExpanded = true;
    private bool _meetingSortNewestFirst = true;
    private bool _isCapturingHotkey;
    private bool _awaitingHandsFreeSecondTap;
    private bool _isHandsFreeDictationLocked;
    private bool _runtimeStarted;
    private bool _isParkedForBackground;
    private bool _isWorkAreaMaximized;
    private Rect _restoreBounds;
    private string _dictationDateFilter = "all";
    private string _meetingDateFilter = "all";
    private string _searchQuery = "";
    private UIElement? _lastNonSearchPage;
    private System.Windows.Controls.Button? _lastNonSearchNav;
    private string? _selectedMeetingFolderId;
    private string _selectedMeetingTemplate = "Standard Meeting Notes";
    private MeetingItem? _selectedMeeting;
    private Dictionary<string, string> _activeSpeakerAliases = new();
    private bool _lastMeetingDetailShowTranscript = false;
    private bool _isMeetingRecording;
    private int _meetingMissingScanCount;
    private string? _currentMeetingTitle;
    private string? _currentMeetingKey;
    private int? _currentMeetingProcessId;
    private string? _currentMeetingRecordId;
    private DateTime _meetingEndPromptSnoozedUntil;
    private bool _meetingEndPromptVisible;
    private DateTime _meetingPromptCooldownUntil;
    private string? _meetingPromptCooldownPlatform;
    private string _runtimeDiagnostics = "Not checked yet.";
    private string _modelCacheDirectory = "";
    private string _modelCacheSize = "0 B";
    private string _setupReadiness = "Setup not checked yet.";
    private string _transcriptionWorkerStatus = "Not checked";
    private string _whisperRuntimeStatus = "Not checked";
    private string _selectedModelCacheStatus = "Not checked";
    private string _speakerDiarizationStatusLabel = "Not checked";
    private string _gpuRuntimeStatus = "Not checked";
    private string _qwenRuntimeStatus = "Not checked";
    private string _parakeetRuntimeStatus = "Not checked";
    private string _diarizationDependencyStatus = "Not checked yet.";
    private string _diarizationTokenStatus = "Not checked yet.";
    private string _meetingDetectionStatus = "Meeting detection has not scanned yet.";
    private string _runtimeSetupStatus = "";
    private bool _canInstallLocalRuntime;
    private bool _isInstallingLocalRuntime;
    private double? _indicatorLeft;
    private double? _indicatorTop;
    private bool _crashReportingEnabled;
    private bool _crashReportingPromptShown;
    private bool _crashReportingStartupValue;
    private UpdateManager? _updateManager;
    private UpdateInfo? _pendingUpdate;
    private bool _isUpdateReady;
    private bool _updateCheckInFlight;
    private MeetingPlaybackTrack? _selectedPlaybackTrack;
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds = 1;
    private string? _startupRecoveryWarning;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DictationItem> Dictations { get; } = [];
    public ObservableCollection<MeetingItem> Meetings { get; } = [];
    public ObservableCollection<MeetingFolderItem> MeetingFolders { get; } = [];
    public ObservableCollection<MeetingTemplateItem> CustomMeetingTemplates { get; } = [];
    public ObservableCollection<DictionaryEntryItem> DictionaryEntries { get; } = [];
    public ObservableCollection<UpcomingMeetingItem> UpcomingMeetings { get; } = [];
    public ObservableCollection<string> MicrophoneDevices { get; } = ["System default microphone"];
    public ObservableCollection<string> AsrEngines { get; } = ["whisper", "parakeet-v3"];
    public ObservableCollection<string> ModelProfiles { get; } = ["tiny", "base", "small", "medium", "large-v3-turbo"];
    public ObservableCollection<string> LanguageOptions { get; } = ["auto", "en", "es", "fr", "de", "it", "pt", "hi", "ta", "te", "kn", "ml"];
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
    public ObservableCollection<string> PasteBehaviors { get; } = ["active-app", "clipboard", "history-only"];
    public ObservableCollection<string> IndicatorPositions { get; } = ["Top Left", "Top Center", "Top Right", "Bottom Left", "Bottom Center", "Bottom Right", "Custom"];
    public ObservableCollection<string> ThemeOptions { get; } = ["Light", "Dark"];
    public ObservableCollection<string> SummaryProviders { get; } = ["local", "openai", "openrouter", "ollama", "lmstudio", "custom"];
    public ObservableCollection<string> SummaryTemplates { get; } = new(MeetingSummaryService.BuiltInTemplateNames);
    public ObservableCollection<string> RecordingSavePolicies { get; } = ["always", "prompt", "never"];
    public ObservableCollection<MeetingPlaybackTrack> MeetingPlaybackTracks { get; } = [];
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
    public int MeetingCount => Meetings.Count;
    public int VisibleMeetingCount => FilteredMeetings?.Cast<MeetingItem>().Count() ?? Meetings.Count;
    public string MeetingsChevron => _meetingsExpanded ? "⌄" : "›";
    public string MeetingSortLabel => _meetingSortNewestFirst ? "Newest first⌄" : "Oldest first⌄";
    public string CurrentMeetingFolderName => _selectedMeetingFolderId is null
        ? "All Meetings"
        : MeetingFolders.FirstOrDefault(folder => folder.Id == _selectedMeetingFolderId)?.Name ?? "All Meetings";
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
    public bool HasUpcomingMeetings => UpcomingMeetings.Count > 0;
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
    public string MeetingRecordingButtonText => _isMeetingRecording ? "Stop recording" : "Record meeting";
    public string ActiveModelLabel => SelectedAsrEngine == "parakeet-v3" ? "Parakeet v3" : $"Whisper {SelectedModelProfile}";
    public string WhisperStatusLabel => SelectedAsrEngine == "whisper" ? "Active" : "Downloaded";
    public string ParakeetStatusLabel => SelectedAsrEngine == "parakeet-v3" ? "Active" : "Optional";
    public string ShortcutModeLabel => EnableDoubleTapDictation ? "Hold to talk, or double-tap to lock recording" : "Hold to record, release to transcribe";
    public string CaptureHotkeyButtonText => _isCapturingHotkey ? "Press shortcut..." : "Record shortcut";
    public string ShortcutCaptureLabel => _isCapturingHotkey
        ? "Press a function key or a modifier shortcut such as Ctrl+Shift+Space. Press Esc to cancel."
        : "Choose a shortcut or record one that is free on this Windows laptop.";
    public string AppVersion
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return $"v{(string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString(3) ?? "0.3.0" : informational)}";
        }
    }
    public string SelectedMeetingTitle
    {
        get => _selectedMeeting?.Title ?? "";
        set
        {
            if (_selectedMeeting is null) return;
            var title = value?.Trim() ?? "";
            if (title.Length == 0 || title == _selectedMeeting.Title) return;
            UpdateSelectedMeeting(_selectedMeeting with { Title = title });
            OnPropertyChanged();
        }
    }
    public string SelectedMeetingMetadata => _selectedMeeting?.Metadata ?? "";
    public string SelectedMeetingSpeakerIdentificationLabel =>
        _selectedMeeting?.SpeakerIdentificationLabel ?? "Speaker status unavailable";
    public string SelectedMeetingNotes => string.IsNullOrWhiteSpace(_selectedMeeting?.Summary)
        ? ""
        : ApplySpeakerAliasesToNotes(_selectedMeeting.Summary, _activeSpeakerAliases);
    public string SelectedMeetingTemplate
    {
        get => _selectedMeetingTemplate;
        set
        {
            var normalized = NormalizeSummaryTemplateName(value);
            if (SetField(ref _selectedMeetingTemplate, normalized))
            {
                OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
            }
        }
    }
    public string SelectedMeetingNotesActionLabel => string.IsNullOrWhiteSpace(_selectedMeeting?.Summary) ? "Generate Notes" : "Regenerate Notes";
    public string SelectedMeetingTranscript
    {
        get => ApplySpeakerAliases(_selectedMeeting?.Transcript ?? "", _activeSpeakerAliases);
        set
        {
            if (_selectedMeeting is null || value == SelectedMeetingTranscript) return;
            UpdateSelectedMeeting(_selectedMeeting with
            {
                Transcript = value ?? "",
                WordCount = CountWords(value ?? "")
            });
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
        }
    }
    public string SelectedMeetingManualNotes
    {
        get => _selectedMeeting?.ManualNotes ?? "";
        set
        {
            if (_selectedMeeting is null || value == _selectedMeeting.ManualNotes) return;
            UpdateSelectedMeeting(_selectedMeeting with { ManualNotes = value ?? "" });
            OnPropertyChanged();
        }
    }
    public bool HasMeetingPlaybackTracks => MeetingPlaybackTracks.Count > 0;
    public MeetingPlaybackTrack? SelectedPlaybackTrack
    {
        get => _selectedPlaybackTrack;
        set
        {
            if (!SetField(ref _selectedPlaybackTrack, value) || value is null) return;
            try
            {
                _meetingPlayback.Load(value);
                _playbackPositionSeconds = 0;
                _playbackDurationSeconds = Math.Max(1, _meetingPlayback.Duration.TotalSeconds);
                OnPropertyChanged(nameof(PlaybackPositionSeconds));
                OnPropertyChanged(nameof(PlaybackDurationSeconds));
                OnPropertyChanged(nameof(PlaybackButtonLabel));
            }
            catch (Exception exception)
            {
                DictationStatus = $"Could not load recording: {exception.Message}";
            }
        }
    }
    public double PlaybackPositionSeconds
    {
        get => _playbackPositionSeconds;
        set
        {
            var next = Math.Clamp(value, 0, PlaybackDurationSeconds);
            if (Math.Abs(next - _playbackPositionSeconds) < 0.1) return;
            _playbackPositionSeconds = next;
            _meetingPlayback.Seek(TimeSpan.FromSeconds(next));
            OnPropertyChanged();
        }
    }
    public double PlaybackDurationSeconds => _playbackDurationSeconds;
    public string PlaybackButtonLabel => _meetingPlayback.IsPlaying ? "Pause" : "Play";
    public string RuntimeDiagnostics
    {
        get => _runtimeDiagnostics;
        private set => SetField(ref _runtimeDiagnostics, value);
    }
    public string TranscriptionWorkerStatus
    {
        get => _transcriptionWorkerStatus;
        private set => SetField(ref _transcriptionWorkerStatus, value);
    }
    public string WhisperRuntimeStatus
    {
        get => _whisperRuntimeStatus;
        private set => SetField(ref _whisperRuntimeStatus, value);
    }
    public string SelectedModelCacheStatus
    {
        get => _selectedModelCacheStatus;
        private set => SetField(ref _selectedModelCacheStatus, value);
    }
    public string SpeakerDiarizationStatusLabel
    {
        get => _speakerDiarizationStatusLabel;
        private set => SetField(ref _speakerDiarizationStatusLabel, value);
    }
    public string GpuRuntimeStatus
    {
        get => _gpuRuntimeStatus;
        private set => SetField(ref _gpuRuntimeStatus, value);
    }
    public string QwenRuntimeStatus
    {
        get => _qwenRuntimeStatus;
        private set => SetField(ref _qwenRuntimeStatus, value);
    }
    public string ParakeetRuntimeStatus
    {
        get => _parakeetRuntimeStatus;
        private set => SetField(ref _parakeetRuntimeStatus, value);
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
    public bool CanInstallLocalRuntime
    {
        get => _canInstallLocalRuntime;
        private set => SetField(ref _canInstallLocalRuntime, value);
    }
    public string ModelCacheSize
    {
        get => _modelCacheSize;
        private set => SetField(ref _modelCacheSize, value);
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

    public string SelectedModelProfile
    {
        get => _selectedModelProfile;
        set
        {
            if (SetField(ref _selectedModelProfile, value))
            {
                SaveSettings();
                OnPropertyChanged(nameof(ActiveModelLabel));
            }
        }
    }

    public string SelectedAsrEngine
    {
        get => _selectedAsrEngine;
        set
        {
            if (SetField(ref _selectedAsrEngine, AsrEngines.Contains(value) ? value : "whisper"))
            {
                SaveSettings();
                OnPropertyChanged(nameof(ActiveModelLabel));
                OnPropertyChanged(nameof(WhisperStatusLabel));
                OnPropertyChanged(nameof(ParakeetStatusLabel));
            }
        }
    }

    public string SelectedDictationLanguage
    {
        get => _selectedDictationLanguage;
        set
        {
            if (SetField(ref _selectedDictationLanguage, LanguageOptions.Contains(value) ? value : "en"))
                SaveSettings();
        }
    }

    public string SelectedMeetingAsrEngine
    {
        get => _selectedMeetingAsrEngine;
        set
        {
            if (SetField(ref _selectedMeetingAsrEngine, AsrEngines.Contains(value) ? value : "whisper"))
                SaveSettings();
        }
    }

    public string SelectedMeetingModelProfile
    {
        get => _selectedMeetingModelProfile;
        set
        {
            if (SetField(ref _selectedMeetingModelProfile, ModelProfiles.Contains(value) ? value : "base"))
                SaveSettings();
        }
    }

    public string? SelectedMeetingMicrophone
    {
        get => _selectedMeetingMicrophone;
        set
        {
            if (SetField(ref _selectedMeetingMicrophone, value))
                SaveSettings();
        }
    }

    public string SelectedHotkey
    {
        get => _selectedHotkey;
        set
        {
            var nextHotkey = NormalizeHotkey(value, allowCustom: true);
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

    public bool PostProcessingEnabled
    {
        get => _postProcessingEnabled;
        set
        {
            if (SetField(ref _postProcessingEnabled, value))
            {
                SaveSettings();
            }
        }
    }

    public bool RemoveFillerWords
    {
        get => _removeFillerWords;
        set
        {
            if (SetField(ref _removeFillerWords, value))
                SaveSettings();
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

    public string PostProcessingPrompt
    {
        get => _postProcessingPrompt;
        set
        {
            if (SetField(ref _postProcessingPrompt, value ?? ""))
            {
                SaveSettings();
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

    public string SelectedSummaryProvider
    {
        get => _selectedSummaryProvider;
        set
        {
            if (SetField(ref _selectedSummaryProvider, value))
            {
                SaveSettings();
            }
        }
    }

    public string SelectedSummaryTemplate
    {
        get => _selectedSummaryTemplate;
        set
        {
            var normalized = NormalizeSummaryTemplateName(value);
            if (SetField(ref _selectedSummaryTemplate, normalized))
            {
                SaveSettings();
            }
        }
    }

    public bool OpenDashboardOnLaunch
    {
        get => _openDashboardOnLaunch;
        set
        {
            if (SetField(ref _openDashboardOnLaunch, value))
            {
                SaveSettings();
            }
        }
    }

    public bool SaveMeetingRecordings
    {
        get => _saveMeetingRecordings;
        set
        {
            if (SetField(ref _saveMeetingRecordings, value))
            {
                SaveSettings();
            }
        }
    }

    public bool ShowFloatingIndicator
    {
        get => _showFloatingIndicator;
        set
        {
            if (SetField(ref _showFloatingIndicator, value))
            {
                _toastNotificationService.SetIdleIndicatorVisible(value);
                SaveSettings();
            }
        }
    }

    public string SelectedIndicatorPosition
    {
        get => _selectedIndicatorPosition;
        set
        {
            var next = IndicatorPositions.Contains(value) ? value : "Top Center";
            if (SetField(ref _selectedIndicatorPosition, next))
            {
                var clearCustomPosition = !next.Equals("Custom", StringComparison.OrdinalIgnoreCase);
                if (clearCustomPosition)
                {
                    _indicatorLeft = null;
                    _indicatorTop = null;
                }

                _toastNotificationService.SetIndicatorAnchor(next, clearCustomPosition);
                SaveSettings();
            }
        }
    }

    public string OpenAIApiKey
    {
        get => _openAIApiKey;
        set
        {
            if (SetField(ref _openAIApiKey, value))
            {
                SaveSettings();
            }
        }
    }

    public string OpenAIModel
    {
        get => _openAIModel;
        set
        {
            if (SetField(ref _openAIModel, value))
            {
                SaveSettings();
            }
        }
    }

    public string OpenRouterApiKey
    {
        get => _openRouterApiKey;
        set
        {
            if (SetField(ref _openRouterApiKey, value))
            {
                SaveSettings();
            }
        }
    }

    public string OpenRouterModel
    {
        get => _openRouterModel;
        set
        {
            if (SetField(ref _openRouterModel, value))
            {
                SaveSettings();
            }
        }
    }

    public string RecordingSavePolicy
    {
        get => _recordingSavePolicy;
        set
        {
            var normalized = RecordingSavePolicies.Contains(value) ? value : "always";
            if (SetField(ref _recordingSavePolicy, normalized))
            {
                _saveMeetingRecordings = normalized != "never";
                OnPropertyChanged(nameof(SaveMeetingRecordings));
                SaveSettings();
            }
        }
    }

    public bool AutoExportMeetings
    {
        get => _autoExportMeetings;
        set { if (SetField(ref _autoExportMeetings, value)) SaveSettings(); }
    }

    public string AutoExportDirectory
    {
        get => _autoExportDirectory;
        set { if (SetField(ref _autoExportDirectory, value ?? "")) SaveSettings(); }
    }

    public string CustomSummaryEndpoint
    {
        get => _customSummaryEndpoint;
        set { if (SetField(ref _customSummaryEndpoint, value ?? "")) SaveSettings(); }
    }

    public string CustomSummaryModel
    {
        get => _customSummaryModel;
        set { if (SetField(ref _customSummaryModel, value ?? "")) SaveSettings(); }
    }

    public string CustomSummaryApiKey
    {
        get => _customSummaryApiKey;
        set { if (SetField(ref _customSummaryApiKey, value ?? "")) SaveSettings(); }
    }

    public bool AutoMeetingDetectionEnabled
    {
        get => _autoMeetingDetectionEnabled;
        set
        {
            if (SetField(ref _autoMeetingDetectionEnabled, value))
            {
                SaveSettings();
                if (value)
                {
                    _meetingDetectionService.Start();
                }
                else
                {
                    _meetingDetectionService.Stop();
                    _meetingPromptService.Close();
                }
            }
        }
    }

    public MainWindow()
    {
        _meetingRecordingCoordinator = new(_logService);
        InitializeComponent();
        _meetingRecordingCoordinator.CaptureWarning += OnMeetingCaptureWarning;
#if DEBUG
        Title = $"Muesli Development — {AppVersion}";
#endif
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
        DataContext = this;
        UpcomingMeetings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasUpcomingMeetings));
        _hotkeyReleaseTimer.Tick += HotkeyReleaseTimer_Tick;

        var settings = _settingsStore.Load();
        LoadPersistedData();
        var recoveryWarnings = _settingsStore.RecoveryWarnings.Concat(_dataStore.RecoveryWarnings).ToList();
        if (recoveryWarnings.Count > 0)
        {
            _startupRecoveryWarning = string.Join(" ", recoveryWarnings);
            _logService.Info($"Persistence recovery: {_startupRecoveryWarning}");
        }
        RecoverInterruptedMeetingSession();
        foreach (var microphone in _dictationCoordinator.ListMicrophones())
        {
            if (!MicrophoneDevices.Contains(microphone))
            {
                MicrophoneDevices.Add(microphone);
            }
        }
        _selectedMicrophone = settings.MicrophoneName ?? _dictationCoordinator.PickPreferredMicrophone();
        _selectedAsrEngine = AsrEngines.Contains(settings.AsrEngine) ? settings.AsrEngine : "whisper";
        _selectedModelProfile = ModelProfiles.Contains(settings.ModelProfile) ? settings.ModelProfile : "base";
        _selectedDictationLanguage = LanguageOptions.Contains(settings.DictationLanguage) ? settings.DictationLanguage : "en";
        _selectedMeetingAsrEngine = AsrEngines.Contains(settings.MeetingAsrEngine) ? settings.MeetingAsrEngine : _selectedAsrEngine;
        _selectedMeetingModelProfile = ModelProfiles.Contains(settings.MeetingModelProfile) ? settings.MeetingModelProfile : _selectedModelProfile;
        _selectedMeetingMicrophone = settings.MeetingMicrophoneName ?? _selectedMicrophone;
        _selectedHotkey = NormalizeHotkey(settings.Hotkey, allowCustom: true);
        AddHotkeyOptionIfMissing(_selectedHotkey);
        _selectedPasteBehavior = PasteBehaviors.Contains(settings.PasteBehavior) ? settings.PasteBehavior : "active-app";
        _userName = string.IsNullOrWhiteSpace(settings.UserName) ? Environment.UserName.Trim() : settings.UserName.Trim();
        _onboardingCompleted = settings.OnboardingCompleted;
        _selectedSummaryProvider = SummaryProviders.Contains(settings.MeetingSummaryProvider) ? settings.MeetingSummaryProvider : "local";
        _selectedSummaryTemplate = NormalizeSummaryTemplateName(settings.MeetingSummaryTemplate);
        _openAIApiKey = settings.OpenAIApiKey;
        _openAIModel = string.IsNullOrWhiteSpace(settings.OpenAIModel) ? "gpt-5.4-mini" : settings.OpenAIModel;
        _openRouterApiKey = settings.OpenRouterApiKey;
        _openRouterModel = string.IsNullOrWhiteSpace(settings.OpenRouterModel) ? "stepfun/step-3.5-flash:free" : settings.OpenRouterModel;
        _customSummaryEndpoint = settings.CustomSummaryEndpoint;
        _customSummaryModel = settings.CustomSummaryModel;
        _customSummaryApiKey = settings.CustomSummaryApiKey;
        _theme = settings.Theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        _postProcessingEnabled = settings.PostProcessingEnabled;
        _removeFillerWords = settings.RemoveFillerWords;
        _enableDoubleTapDictation = settings.EnableDoubleTapDictation;
        _postProcessingPrompt = settings.PostProcessingPrompt;
        _startAtLogin = settings.StartAtLogin && StartupRegistrationService.IsEnabled();
        _openDashboardOnLaunch = settings.OpenDashboardOnLaunch;
        _saveMeetingRecordings = settings.SaveMeetingRecordings;
        _recordingSavePolicy = RecordingSavePolicies.Contains(settings.RecordingSavePolicy)
            ? settings.RecordingSavePolicy
            : settings.SaveMeetingRecordings ? "always" : "never";
        _autoExportMeetings = settings.AutoExportMeetings;
        _autoExportDirectory = settings.AutoExportDirectory;
        _showFloatingIndicator = settings.ShowFloatingIndicator;
        _selectedIndicatorPosition = IndicatorPositions.Contains(settings.IndicatorAnchor) ? settings.IndicatorAnchor : "Top Center";
        _autoMeetingDetectionEnabled = settings.AutoMeetingDetectionEnabled;
        _indicatorLeft = settings.IndicatorLeft;
        _indicatorTop = settings.IndicatorTop;
        _crashReportingEnabled = settings.CrashReportingEnabled;
        _crashReportingPromptShown = settings.CrashReportingPromptShown;
        _crashReportingStartupValue = _crashReportingEnabled;
        _toastNotificationService.SetSavedPosition(_indicatorLeft, _indicatorTop);
        _toastNotificationService.SetIndicatorAnchor(_selectedIndicatorPosition, clearCustomPosition: false);
        _toastNotificationService.SetIdleIndicatorVisible(_showFloatingIndicator, showNow: false);
        _toastNotificationService.ConfigureActions(StopActiveRecordingFromIndicatorAsync, CancelActiveRecordingFromIndicatorAsync);
        _toastNotificationService.PositionChanged += OnIndicatorPositionChanged;
        _meetingDetectionService.ScanCompleted += OnMeetingDetectionScanCompleted;
        _meetingAutoStopTimer.Tick += MeetingAutoStopTimer_Tick;
        _aliasSaveDebounceTimer.Tick += AliasSaveDebounceTimer_Tick;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
        _meetingPromptService.Reset();
OnPropertyChanged(nameof(SelectedMicrophone));
    OnPropertyChanged(nameof(SelectedAsrEngine));
    OnPropertyChanged(nameof(SelectedModelProfile));
    OnPropertyChanged(nameof(SelectedDictationLanguage));
    OnPropertyChanged(nameof(SelectedMeetingAsrEngine));
    OnPropertyChanged(nameof(SelectedMeetingModelProfile));
    OnPropertyChanged(nameof(SelectedMeetingMicrophone));
    OnPropertyChanged(nameof(SelectedHotkey));
    OnPropertyChanged(nameof(SelectedPasteBehavior));
    OnPropertyChanged(nameof(UserName));
    OnPropertyChanged(nameof(UserGreeting));
    OnPropertyChanged(nameof(SelectedSummaryProvider));
    OnPropertyChanged(nameof(SelectedSummaryTemplate));
    OnPropertyChanged(nameof(OpenAIApiKey));
    OnPropertyChanged(nameof(OpenAIModel));
    OnPropertyChanged(nameof(OpenRouterApiKey));
    OnPropertyChanged(nameof(OpenRouterModel));
    OnPropertyChanged(nameof(CustomSummaryEndpoint));
    OnPropertyChanged(nameof(CustomSummaryModel));
    OnPropertyChanged(nameof(CustomSummaryApiKey));
    OnPropertyChanged(nameof(PostProcessingEnabled));
    OnPropertyChanged(nameof(RemoveFillerWords));
    OnPropertyChanged(nameof(EnableDoubleTapDictation));
    OnPropertyChanged(nameof(PostProcessingPrompt));
    OnPropertyChanged(nameof(SelectedTheme));
    OnPropertyChanged(nameof(StartAtLogin));
    OnPropertyChanged(nameof(OpenDashboardOnLaunch));
    OnPropertyChanged(nameof(SaveMeetingRecordings));
    OnPropertyChanged(nameof(RecordingSavePolicy));
    OnPropertyChanged(nameof(AutoExportMeetings));
    OnPropertyChanged(nameof(AutoExportDirectory));
    OnPropertyChanged(nameof(ShowFloatingIndicator));
    OnPropertyChanged(nameof(SelectedIndicatorPosition));
    OnPropertyChanged(nameof(AutoMeetingDetectionEnabled));
    OnPropertyChanged(nameof(MeetingDetectionStatus));
    ApplyTheme(_theme);
    ShowPage(DictationsPage, DictationsNav);
    Loaded += (_, _) =>
    {
        StartRuntime(showOnboarding: !_isParkedForBackground);
        if (!string.IsNullOrWhiteSpace(_startupRecoveryWarning))
        {
            DictationStatus = _startupRecoveryWarning;
            _toastNotificationService.Show("Muesli recovered local data", _startupRecoveryWarning, ToastState.Error, 9000);
        }
    };
    Closing += (_, _) =>
    {
        _logService.Info("Main window closing.");
        _aliasSaveDebounceTimer.Stop();
        SaveActiveSpeakerAliases();
        _meetingAutoStopTimer.Stop();
        _meetingDetectionService.ScanCompleted -= OnMeetingDetectionScanCompleted;
        _globalHotkeyService.Dispose();
        _meetingDetectionService.Dispose();
        _meetingPromptService.Close();
        _meetingTranscriptionClient.Dispose();
        _meetingRecordingCoordinator.Dispose();
        _playbackTimer.Stop();
        _meetingPlayback.Dispose();
        _trayIconService.Dispose();
    };
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
    _trayIconService.Initialize(this);
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
    StartBackgroundUpdateCheck();
}
public void SetBackgroundStatus()
{
    DictationStatus = "Running in background";
}
public void ParkForBackgroundLaunch()
{
    _isParkedForBackground = true;
    ShowInTaskbar = false;
    WindowStartupLocation = WindowStartupLocation.Manual;
    WindowState = WindowState.Normal;
    Opacity = 0;
    Left = -32000;
    Top = -32000;
}
public void ShowDashboardFromBackground()
{
    _isParkedForBackground = false;
    FitDashboardToWorkArea();
    Opacity = 1;
    ShowInTaskbar = true;
    Show();
    WindowState = WindowState.Normal;
    Activate();
    ShowOnboardingIfNeeded();
}
private void FitDashboardToWorkArea()
{
    var area = SystemParameters.WorkArea;
    MinWidth = Math.Max(760, Math.Min(980, area.Width - 24));
    MinHeight = Math.Max(480, Math.Min(620, area.Height - 24));
    Width = Math.Min(1240, Math.Max(MinWidth, area.Width - 24));
    Height = Math.Min(820, Math.Max(MinHeight, area.Height - 24));
    Left = area.Left + Math.Max(12, (area.Width - Width) / 2);
    Top = area.Top + Math.Max(12, (area.Height - Height) / 2);
}
private async void HoldToDictate_MouseDown(object sender, MouseButtonEventArgs e)
{
    await StartDictationAsync(shouldPasteToActiveApp: false);
}
private void ShowOnboardingIfNeeded()
{
    if (_onboardingCompleted)
    {
        return;
    }
    var window = new Window
    {
        Owner = this,
        Title = "Welcome to Muesli",
        Width = 720,
        Height = 620,
        MinWidth = 680,
        MinHeight = 560,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBaseBrush"),
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        FontFamily = FontFamily
    };
    var root = new DockPanel { Margin = new Thickness(28) };
    window.Content = root;
    var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
    DockPanel.SetDock(footer, Dock.Bottom);
    root.Children.Add(footer);
    var finish = new WpfButton
    {
        Content = "Start using Muesli",
        Style = (Style)FindResource("PrimaryButton"),
        MinWidth = 160,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    DockPanel.SetDock(finish, Dock.Right);
    footer.Children.Add(finish);
    var skip = new WpfButton
    {
        Content = "Skip",
        Style = (Style)FindResource("GhostButton"),
        MinWidth = 80,
        Margin = new Thickness(0, 0, 10, 0),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    DockPanel.SetDock(skip, Dock.Right);
    footer.Children.Add(skip);
    var bodyScroll = new ScrollViewer
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    root.Children.Add(bodyScroll);
    var body = new StackPanel();
    bodyScroll.Content = body;
    body.Children.Add(new TextBlock
    {
        Text = "Set up local dictation",
        Style = (Style)FindResource("PageTitle")
    });
    body.Children.Add(new TextBlock
    {
        Text = "Choose a microphone, pick a shortcut that is free on this Windows laptop, and verify local model readiness.",
        Style = (Style)FindResource("PageSubtitle")
    });
    var nameInput = new WpfTextBox
    {
        Text = UserName,
        MinWidth = 260,
        Style = (Style)FindResource("MuesliTextBox")
    };
    body.Children.Add(BuildOnboardingRow("Your name", "Shown in the sidebar greeting.", nameInput));
    var micCombo = new System.Windows.Controls.ComboBox
    {
        ItemsSource = MicrophoneDevices,
        SelectedItem = SelectedMicrophone,
        Style = (Style)FindResource("MuesliComboBox")
    };
    body.Children.Add(BuildOnboardingRow("Microphone", "Used for dictation and meeting recording.", micCombo));
    var hotkeyCombo = new System.Windows.Controls.ComboBox
    {
        ItemsSource = HotkeyOptions,
        SelectedItem = SelectedHotkey,
        Style = (Style)FindResource("MuesliComboBox")
    };
    body.Children.Add(BuildOnboardingRow("Shortcut", "Use an alternate shortcut if F8 is already taken.", hotkeyCombo));
    var startupCheck = new System.Windows.Controls.CheckBox
    {
        Content = "Launch at login",
        IsChecked = StartAtLogin,
        Style = (Style)FindResource("MuesliCheckBox")
    };
    body.Children.Add(BuildOnboardingRow("Startup", "Keep Muesli available from the tray after sign-in.", startupCheck));
    var indicatorCheck = new System.Windows.Controls.CheckBox
    {
        Content = "Show floating indicator",
        IsChecked = ShowFloatingIndicator,
        Style = (Style)FindResource("MuesliCheckBox")
    };
    body.Children.Add(BuildOnboardingRow("Floating indicator", "Mirrors the OG app's small dictation pill.", indicatorCheck));
    var crashCheck = new System.Windows.Controls.CheckBox
    {
        Content = "Send anonymous crash reports",
        IsChecked = _crashReportingEnabled,
        Style = (Style)FindResource("MuesliCheckBox")
    };
    body.Children.Add(BuildOnboardingRow(
        "Crash reporting",
        "Helps fix bugs faster. Only stack traces and app version are sent — never your transcripts, meeting recordings, or settings. You can change this in About → Privacy.",
        crashCheck));
    var readinessText = new WpfTextBox
    {
        Text = SetupReadiness,
        MinHeight = 116,
        Padding = new Thickness(12),
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        IsReadOnly = true,
        Cursor = System.Windows.Input.Cursors.IBeam,
        Style = (Style)FindResource("MuesliTextBox")
    };
    var readinessActions = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    var checkSetup = new WpfButton { Content = "Check setup", Style = (Style)FindResource("SecondaryButton") };
    var installRuntime = new WpfButton { Content = "Install local transcription runtime", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(10, 0, 0, 0) };
    var downloadBase = new WpfButton { Content = "Download base model", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(10, 0, 0, 0) };
    readinessActions.Children.Add(checkSetup);
    readinessActions.Children.Add(installRuntime);
    readinessActions.Children.Add(downloadBase);
    var setupStatusText = new TextBlock
    {
        Text = RuntimeSetupStatus,
        Margin = new Thickness(0, 10, 0, 0),
        FontSize = 12,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap
    };
    var readinessPanel = new StackPanel();
    readinessPanel.Children.Add(readinessText);
    readinessPanel.Children.Add(readinessActions);
    readinessPanel.Children.Add(setupStatusText);
    body.Children.Add(BuildOnboardingRow("Local model readiness", "Whisper base is the recommended CPU fallback for broad laptop support.", readinessPanel));
    void RefreshRuntimeSetupUi()
    {
        setupStatusText.Text = RuntimeSetupStatus;
        installRuntime.IsEnabled = CanInstallLocalRuntime && !_isInstallingLocalRuntime;
        checkSetup.IsEnabled = !_isInstallingLocalRuntime;
        downloadBase.IsEnabled = !_isInstallingLocalRuntime;
    }
    checkSetup.Click += async (_, _) =>
    {
        await RefreshRuntimeDiagnosticsAsync();
        readinessText.Text = SetupReadiness;
        RefreshRuntimeSetupUi();
    };
    installRuntime.Click += async (_, _) =>
    {
        await InstallLocalRuntimeAsync(
            onStatus: status =>
            {
                RuntimeSetupStatus = status;
                setupStatusText.Text = status;
            },
            onAfterRefresh: () =>
            {
                readinessText.Text = SetupReadiness;
                RefreshRuntimeSetupUi();
            });
    };
    downloadBase.Click += async (_, _) =>
    {
        try
        {
            downloadBase.IsEnabled = false;
            DictationStatus = "Downloading base model";
            var result = await _dictationCoordinator.DownloadModelAsync("whisper", "base");
            DictationStatus = result.Text;
            await RefreshRuntimeDiagnosticsAsync();
            readinessText.Text = SetupReadiness;
        }
        catch (Exception exception)
        {
            DictationStatus = $"Base model download failed: {exception.Message}";
            _toastNotificationService.Show("Model download failed", exception.Message, ToastState.Error, 5200);
        }
        finally
        {
            downloadBase.IsEnabled = true;
        }
    };
    RefreshRuntimeSetupUi();
    finish.Click += (_, _) =>
    {
        UserName = string.IsNullOrWhiteSpace(nameInput.Text) ? UserName : nameInput.Text.Trim();
        SelectedMicrophone = micCombo.SelectedItem as string ?? SelectedMicrophone;
        SelectedHotkey = hotkeyCombo.SelectedItem as string ?? SelectedHotkey;
        StartAtLogin = startupCheck.IsChecked == true;
        ShowFloatingIndicator = indicatorCheck.IsChecked == true;
        _crashReportingEnabled = crashCheck.IsChecked == true;
        _crashReportingStartupValue = _crashReportingEnabled;
        _crashReportingPromptShown = true;
        OnPropertyChanged(nameof(CrashReportingEnabled));
        OnPropertyChanged(nameof(CrashReportingRestartHintVisible));
        _onboardingCompleted = true;
        SaveSettings();
        window.Close();
        _ = EnsureBaseModelDownloadedAsync();
    };
    skip.Click += (_, _) =>
    {
        _crashReportingPromptShown = true;
        _onboardingCompleted = true;
        SaveSettings();
        window.Close();
    };
    window.ShowDialog();
}
private Border BuildOnboardingRow(string title, string description, FrameworkElement control)
{
    var card = new Border
    {
        Style = (Style)FindResource("ContentCard"),
        Margin = new Thickness(0, 0, 0, 14)
    };
    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
    card.Child = grid;
    var copy = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
    copy.Children.Add(new TextBlock
    {
        Text = title,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
    });
    copy.Children.Add(new TextBlock
    {
        Text = description,
        Margin = new Thickness(0, 5, 0, 0),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
    });
    grid.Children.Add(copy);
    control.VerticalAlignment = VerticalAlignment.Center;
    Grid.SetColumn(control, 1);
    grid.Children.Add(control);
    return card;
}
private async void HoldToDictate_MouseUp(object sender, MouseButtonEventArgs e)
{
    await StopDictationAsync();
}
private void CaptureHotkey_Click(object sender, RoutedEventArgs e)
{
    _isCapturingHotkey = true;
    OnPropertyChanged(nameof(CaptureHotkeyButtonText));
    OnPropertyChanged(nameof(ShortcutCaptureLabel));
    DictationStatus = "Press a new dictation shortcut";
    Focus();
}
private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
{
    if (!_isCapturingHotkey)
    {
        if (e.Key == Key.Escape && !string.IsNullOrWhiteSpace(SearchQuery))
        {
            SearchQuery = "";
            e.Handled = true;
        }
        return;
    }
    e.Handled = true;
    if (e.Key == Key.Escape)
    {
        StopHotkeyCapture("Shortcut capture cancelled");
        return;
    }
    var label = BuildCapturedHotkeyLabel(e);
    if (label is null)
    {
        DictationStatus = "Press a function key or include Ctrl, Alt, or Shift";
        return;
    }
    AddHotkeyOptionIfMissing(label);
    SelectedHotkey = label;
    StopHotkeyCapture($"Shortcut set to {label}");
}
private void StopHotkeyCapture(string status)
{
    _isCapturingHotkey = false;
    DictationStatus = status;
    OnPropertyChanged(nameof(CaptureHotkeyButtonText));
    OnPropertyChanged(nameof(ShortcutCaptureLabel));
}
private static string? BuildCapturedHotkeyLabel(System.Windows.Input.KeyEventArgs e)
{
    var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
    if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
    {
        return null;
    }
    var modifiers = Keyboard.Modifiers;
    var parts = new List<string>();
    if (modifiers.HasFlag(ModifierKeys.Control))
    {
        parts.Add("Ctrl");
    }
    if (modifiers.HasFlag(ModifierKeys.Alt))
    {
        parts.Add("Alt");
    }
    if (modifiers.HasFlag(ModifierKeys.Shift))
    {
        parts.Add("Shift");
    }
    if (key is not (>= Key.F1 and <= Key.F24) && parts.Count == 0)
    {
        return null;
    }
    parts.Add(key == Key.Space ? "Space" : key.ToString());
    return string.Join("+", parts);
}
private Task StartHotkeyDictationAsync()
{
    return StartDictationAsync(shouldPasteToActiveApp: true);
}
private bool RegisterGlobalHotkey()
{
    try
    {
        _globalHotkeyService.Register(
            SelectedHotkey,
            () => Dispatcher.InvokeAsync(HandleHotkeyDownAsync),
            () => Dispatcher.InvokeAsync(HandleHotkeyUpAsync));
        return true;
    }
    catch (Exception exception)
    {
        DictationStatus = $"Hotkey failed: {exception.Message}";
        _logService.Error("Global hotkey registration failed.", exception);
        _toastNotificationService.Show("Hotkey failed", exception.Message, ToastState.Error, 4200);
        return false;
    }
}
private string NormalizeHotkey(string? value, bool allowCustom = false)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "F8";
    }
    var compact = value.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
    var preset = HotkeyOptions.FirstOrDefault(option =>
        option.Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .Equals(compact, StringComparison.OrdinalIgnoreCase));
    if (preset is not null)
    {
        return preset;
    }
    return allowCustom && TryNormalizeCustomHotkey(value, out var custom) ? custom : "F8";
}
private static bool TryNormalizeCustomHotkey(string value, out string normalized)
{
    normalized = "";
    var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
        return false;
    }
    var keyPart = parts[^1].Replace(" ", "", StringComparison.OrdinalIgnoreCase);
    if (!Enum.TryParse<Key>(keyPart, ignoreCase: true, out var key) || key == Key.None)
    {
        return false;
    }
    var modifiers = new List<string>();
    foreach (var part in parts.Take(parts.Length - 1))
    {
        if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("Control", StringComparison.OrdinalIgnoreCase))
        {
            modifiers.Add("Ctrl");
        }
        else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
        {
            modifiers.Add("Shift");
        }
        else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
        {
            modifiers.Add("Alt");
        }
        else
        {
            return false;
        }
    }
    normalized = modifiers.Count == 0 ? key.ToString() : $"{string.Join("+", modifiers)}+{key}";
    return key is >= Key.F1 and <= Key.F24 || modifiers.Count > 0;
}
private void AddHotkeyOptionIfMissing(string hotkey)
{
    if (!HotkeyOptions.Any(option => option.Equals(hotkey, StringComparison.OrdinalIgnoreCase)))
    {
        HotkeyOptions.Add(hotkey);
    }
}
private async Task HandleHotkeyDownAsync()
{
    if (!EnableDoubleTapDictation)
    {
        await StartHotkeyDictationAsync();
        return;
    }

    if (_isHandsFreeDictationLocked)
    {
        ResetHotkeyDictationState();
        await StopDictationAsync();
        return;
    }

    if (_dictationCoordinator.IsRecording)
    {
        if (_awaitingHandsFreeSecondTap)
        {
            _hotkeyReleaseTimer.Stop();
            _awaitingHandsFreeSecondTap = false;
            _isHandsFreeDictationLocked = true;
            DictationStatus = "Hands-free dictation active";
            _toastNotificationService.Show("Hands-free on", "Tap the shortcut again to stop", ToastState.Success, 1800);
        }

        return;
    }

    ResetHotkeyDictationState();
    await StartHotkeyDictationAsync();
}
private async Task HandleHotkeyUpAsync()
{
    if (!EnableDoubleTapDictation)
    {
        await StopDictationAsync();
        return;
    }

    if (!_dictationCoordinator.IsRecording || _dictationCoordinator.IsBusy || _isHandsFreeDictationLocked)
    {
        return;
    }

    _awaitingHandsFreeSecondTap = true;
    _hotkeyReleaseTimer.Stop();
    _hotkeyReleaseTimer.Start();
}
private async Task StartDictationAsync(bool shouldPasteToActiveApp)
{
    if (_dictationCoordinator.IsRecording || _dictationCoordinator.IsBusy)
    {
        return;
    }
    try
    {
        _shouldPasteToActiveApp = shouldPasteToActiveApp;
        _pasteTargetWindow = shouldPasteToActiveApp
            ? _activeAppPasteService.CaptureForegroundWindow()
            : IntPtr.Zero;
        DictationStatus = "Listening";
        _toastNotificationService.Show("Recording", $"Hold {SelectedHotkey} or the button while speaking", ToastState.Recording, 0);
        await _dictationCoordinator.StartAsync(SelectedMicrophone);
        DictationStatus = _dictationCoordinator.IsRecording ? "Listening" : "Ready";
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not start microphone: {exception.Message}";
        _logService.Error("Could not start dictation microphone.", exception);
        _toastNotificationService.Show("Microphone failed", exception.Message, ToastState.Error);
    }
}
private async Task StopDictationAsync()
{
    if (!_dictationCoordinator.IsRecording || _dictationCoordinator.IsBusy)
    {
        return;
    }

    ResetHotkeyDictationState();

    TranscriptionResult result;
    try
    {
        DictationStatus = "Transcribing";
        _toastNotificationService.Show("Transcribing", "Processing local audio", ToastState.Transcribing, 0);
        result = await _dictationCoordinator.StopAsync(new TranscriptionOptions(SelectedAsrEngine, SelectedModelProfile, WorkerLanguageHint(SelectedDictationLanguage)));
    }
    catch (Exception exception)
    {
        DictationStatus = $"Dictation failed: {exception.Message}";
        _logService.Error("Dictation transcription failed.", exception);
        _toastNotificationService.Show("Dictation failed", exception.Message, ToastState.Error);
        return;
    }
    var textToUse = "";
    if (!string.IsNullOrWhiteSpace(result.Text))
    {
        textToUse = RemoveFillerWords ? FillerWordFilter.Apply(result.Text) : result.Text;
        textToUse = DictionaryCorrectionService.Apply(textToUse, DictionaryEntries.Select(entry => entry.Record));
        textToUse = await PostProcessIfEnabledAsync(textToUse, "dictation", _dictationCoordinator.PostProcessAsync);
        Dictations.Insert(0, new DictationItem(
            $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            DateTime.Now,
            DateTime.Now.ToString("hh:mm tt"),
            textToUse,
            SelectedModelProfile,
            result.DurationMs,
            SelectedPasteBehavior));
        SaveDictations();
        OnPropertyChanged(nameof(DayStreak));
        OnPropertyChanged(nameof(WordsDictated));
        OnPropertyChanged(nameof(WordsDictatedDisplay));
        OnPropertyChanged(nameof(AverageWpm));
        RefreshSearchResults();
    }
    if (textToUse.Length > 0)
    {
        try
        {
            if (SelectedPasteBehavior == "history-only")
            {
                DictationStatus = "Voice note saved";
                _toastNotificationService.Show("Voice note saved", "Stored in dictation history without pasting", ToastState.Success);
            }
            else if (_shouldPasteToActiveApp && SelectedPasteBehavior == "active-app")
            {
                await _activeAppPasteService.PasteTextAsync(textToUse, _pasteTargetWindow);
                DictationStatus = "Pasted";
                _toastNotificationService.Show("Pasted", textToUse, ToastState.Success);
            }
            else
            {
                System.Windows.Clipboard.SetText(textToUse);
                DictationStatus = "Copied";
                _toastNotificationService.Show("Copied", "Transcript copied to clipboard", ToastState.Success);
            }
        }
        catch (Exception exception)
        {
            DictationStatus = $"Dictation ready, paste failed: {exception.Message}";
            _logService.Error("Active-app paste failed after successful dictation.", exception);
            _toastNotificationService.Show("Dictation ready", "Paste failed; transcript is in the app", ToastState.Error);
        }
    }
    else
    {
        var diagnostic = FirstDiagnosticLine(result.Diagnostic);
        DictationStatus = string.IsNullOrWhiteSpace(diagnostic) ? "No speech detected" : $"No speech detected. {diagnostic}";
        _logService.Info($"No speech detected. Diagnostic: {result.Diagnostic}");
        _toastNotificationService.Show("No speech detected", diagnostic, ToastState.Error, 3600);
    }
}
private async Task StopActiveRecordingFromIndicatorAsync()
{
    await Dispatcher.InvokeAsync(async () =>
    {
        if (_isMeetingRecording)
        {
            await ToggleMeetingRecordingAsync(null);
            return;
        }
        await StopDictationAsync();
    });
}
private async Task CancelActiveRecordingFromIndicatorAsync()
{
    await Dispatcher.InvokeAsync(async () =>
    {
        if (_isMeetingRecording)
        {
            await ToggleMeetingRecordingAsync(null);
            return;
        }
        if (!_dictationCoordinator.IsRecording || _dictationCoordinator.IsBusy)
        {
            return;
        }
        await _dictationCoordinator.CancelAsync();
        ResetHotkeyDictationState();
        _pasteTargetWindow = IntPtr.Zero;
        _shouldPasteToActiveApp = false;
        DictationStatus = "Dictation cancelled";
        _toastNotificationService.ShowIdle(SelectedHotkey);
    });
}
private async void HotkeyReleaseTimer_Tick(object? sender, EventArgs e)
{
    _hotkeyReleaseTimer.Stop();
    if (!_awaitingHandsFreeSecondTap || _isHandsFreeDictationLocked)
    {
        return;
    }

    _awaitingHandsFreeSecondTap = false;
    await StopDictationAsync();
}
private void ResetHotkeyDictationState()
{
    _hotkeyReleaseTimer.Stop();
    _awaitingHandsFreeSecondTap = false;
    _isHandsFreeDictationLocked = false;
}
private async void TestMic_Click(object sender, RoutedEventArgs e)
{
    if (_dictationCoordinator.IsBusy || _dictationCoordinator.IsRecording)
    {
        return;
    }
    try
    {
        DictationStatus = "Testing microphone for 2 seconds";
        _toastNotificationService.Show("Testing microphone", SelectedMicrophone ?? "Selected microphone", ToastState.Recording, 0);
        await _dictationCoordinator.StartAsync(SelectedMicrophone);
        await Task.Delay(2000);
        var result = await _dictationCoordinator.StopAsync(new TranscriptionOptions(SelectedAsrEngine, SelectedModelProfile, WorkerLanguageHint(SelectedDictationLanguage)));
        var diagnostic = FirstDiagnosticLine(result.Diagnostic);
        DictationStatus = string.IsNullOrWhiteSpace(diagnostic) ? "Mic test completed" : diagnostic;
        _toastNotificationService.Show("Mic test completed", DictationStatus, ToastState.Success, 3600);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Mic test failed: {exception.Message}";
        _toastNotificationService.Show("Mic test failed", exception.Message, ToastState.Error);
    }
}
private void CopyDictation_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictationItem item })
    {
        System.Windows.Clipboard.SetText(item.Text);
        DictationStatus = "Copied";
        _toastNotificationService.Show("Copied", item.Text, ToastState.Success);
    }
}
private async void DictationRow_Click(object sender, MouseButtonEventArgs e)
{
    // Ignore clicks on buttons (Copy/Delete icons)
    if (e.OriginalSource is System.Windows.Controls.Button or System.Windows.Controls.Image)
        return;
    if (sender is not FrameworkElement { DataContext: DictationItem item })
        return;
    if (sender is not System.Windows.DependencyObject dep)
        return;

    // Visual feedback: highlight text
    if (FindVisualChild<System.Windows.Controls.TextBox>(dep) is { } textBox)
    {
        textBox.Focus();
        textBox.SelectAll();
    }

    // Copy to clipboard
    System.Windows.Clipboard.SetText(item.Text);
    DictationStatus = "Copied to clipboard";
    _toastNotificationService.Show("Copied", "Dictation copied to clipboard", ToastState.Success, 2000);

    // Remove highlight after brief delay
    await Task.Delay(300);
    if (FindVisualChild<System.Windows.Controls.TextBox>(dep) is { } tb)
    {
        tb.Select(0, 0);
    }
}
private void DeleteDictation_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictationItem item })
    {
        Dictations.Remove(item);
        SaveDictations();
        OnPropertyChanged(nameof(DayStreak));
        OnPropertyChanged(nameof(WordsDictated));
        OnPropertyChanged(nameof(WordsDictatedDisplay));
        OnPropertyChanged(nameof(AverageWpm));
        RefreshSearchResults();
        DictationStatus = "Deleted dictation";
    }
}
private void CopyMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        CopyMeetingToClipboard(item);
    }
}
private void OpenMeetingAudio_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingItem item })
    {
        return;
    }
    OpenMeetingAudio(item);
}
private void CopyMeetingToClipboard(MeetingItem item)
{
    var text = string.IsNullOrWhiteSpace(item.Summary)
        ? item.Transcript
        : $"{item.Summary}{Environment.NewLine}{Environment.NewLine}## Transcript{Environment.NewLine}{item.Transcript}";
    System.Windows.Clipboard.SetText(text);
    DictationStatus = "Copied meeting";
    _toastNotificationService.Show("Copied", item.Title, ToastState.Success);
}
private void OpenMeetingDetail(MeetingItem item)
{
    _selectedMeeting = item;
    _selectedMeetingTemplate = NormalizeSummaryTemplateName(string.IsNullOrWhiteSpace(item.TemplateName) ? SelectedSummaryTemplate : item.TemplateName);
    _activeSpeakerAliases = new Dictionary<string, string>(item.SpeakerAliases ?? new Dictionary<string, string>());
    BuildSpeakerAliasPanel();
    BuildMeetingWarningsPanel(item);
    BuildMeetingNotesContent();
    _meetingPlayback.Close();
    MeetingPlaybackTracks.Clear();
    foreach (var track in MeetingRecordingPlaybackService.SelectTracks(item.SourcePath)) MeetingPlaybackTracks.Add(track);
    _selectedPlaybackTrack = MeetingPlaybackTracks.FirstOrDefault();
    if (_selectedPlaybackTrack is not null) _meetingPlayback.Load(_selectedPlaybackTrack);
    _playbackPositionSeconds = 0;
    _playbackDurationSeconds = Math.Max(1, _meetingPlayback.Duration.TotalSeconds);
    OnPropertyChanged(nameof(HasMeetingPlaybackTracks));
    OnPropertyChanged(nameof(SelectedPlaybackTrack));
    OnPropertyChanged(nameof(PlaybackPositionSeconds));
    OnPropertyChanged(nameof(PlaybackDurationSeconds));
    OnPropertyChanged(nameof(PlaybackButtonLabel));
    OnPropertyChanged(nameof(SelectedMeetingTitle));
    OnPropertyChanged(nameof(SelectedMeetingMetadata));
    OnPropertyChanged(nameof(SelectedMeetingSpeakerIdentificationLabel));
    OnPropertyChanged(nameof(SelectedMeetingNotes));
    OnPropertyChanged(nameof(SelectedMeetingTemplate));
    OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
    OnPropertyChanged(nameof(SelectedMeetingTranscript));
    OnPropertyChanged(nameof(SelectedMeetingManualNotes));
    MeetingsBrowserView.Visibility = Visibility.Collapsed;
    MeetingDetailView.Visibility = Visibility.Visible;
    var showTranscript = string.IsNullOrWhiteSpace(item.Summary) && !string.IsNullOrWhiteSpace(item.Transcript)
        ? true
        : _lastMeetingDetailShowTranscript;
    ShowMeetingDetailTab(showTranscript);
}
private void PlaybackTimer_Tick(object? sender, EventArgs e)
{
    _playbackPositionSeconds = _meetingPlayback.Position.TotalSeconds;
    OnPropertyChanged(nameof(PlaybackPositionSeconds));
    OnPropertyChanged(nameof(PlaybackButtonLabel));
}

private void ToggleMeetingPlayback_Click(object sender, RoutedEventArgs e)
{
    try
    {
        if (_meetingPlayback.IsPlaying) _meetingPlayback.Pause(); else _meetingPlayback.Play();
        OnPropertyChanged(nameof(PlaybackButtonLabel));
    }
    catch (Exception exception)
    {
        DictationStatus = $"Playback failed: {exception.Message}";
    }
}

private void StopMeetingPlayback_Click(object sender, RoutedEventArgs e)
{
    _meetingPlayback.Stop();
    _playbackPositionSeconds = 0;
    OnPropertyChanged(nameof(PlaybackPositionSeconds));
    OnPropertyChanged(nameof(PlaybackButtonLabel));
}
private void UpdateSelectedMeeting(MeetingItem updated)
{
    if (_selectedMeeting is null) return;
    var index = Meetings.IndexOf(_selectedMeeting);
    if (index < 0) return;
    Meetings[index] = updated;
    _selectedMeeting = updated;
    SaveMeetings();
    RefreshMeetingViews();
    RefreshSearchResults();
}
private void OpenMeetingAudio(MeetingItem item)
{
    var firstPath = item.SourcePath
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .FirstOrDefault();
    if (string.IsNullOrWhiteSpace(firstPath) || !System.IO.File.Exists(firstPath))
    {
        DictationStatus = "Meeting audio file not found";
        _toastNotificationService.Show("Audio not found", item.Title, ToastState.Error);
        return;
    }
    Process.Start(new ProcessStartInfo
    {
        FileName = firstPath,
        UseShellExecute = true
    });
}
private static List<string> DeleteTemporaryMeetingAudio(
    IEnumerable<string>? temporaryPaths,
    string? pathsToPreserve) =>
    MeetingAudioCleanup.DeleteTemporaryFiles(temporaryPaths, pathsToPreserve);

private void RecoverInterruptedMeetingSession()
{
    var session = _meetingSessionJournal.Load();
    if (session is null)
    {
        return;
    }

    var meetingId = $"meet_{session.SessionId}";
    var existingMeeting = Meetings.FirstOrDefault(item => item.Id.Equals(meetingId, StringComparison.Ordinal));
    if (existingMeeting is not null)
    {
        DeleteTemporaryMeetingAudio(_meetingSessionJournal.DiscoverAudio(session), existingMeeting.SourcePath);
        _meetingSessionJournal.Clear();
        return;
    }

    var audioPaths = _meetingSessionJournal.DiscoverAudio(session);
    var durationMs = (int)Math.Min(
        int.MaxValue,
        Math.Max(0, (DateTime.Now - session.StartedAt).TotalMilliseconds));
    var warning = audioPaths.Count > 0
        ? "Muesli was interrupted while recording. The recoverable audio was preserved; this meeting still needs transcription."
        : "Muesli was interrupted while recording before recoverable audio could be finalized.";
    var recovered = new MeetingItem(
        meetingId,
        session.Title,
        session.StartedAt,
        "",
        "",
        string.Join("; ", audioPaths),
        _selectedMeetingModelProfile,
        durationMs,
        null,
        0,
        SelectedSummaryTemplate,
        HealthWarnings: [warning],
        Status: "failed");
    Meetings.Insert(0, recovered);
    SaveMeetings();
    _meetingSessionJournal.Clear();
    _startupRecoveryWarning = string.IsNullOrWhiteSpace(_startupRecoveryWarning)
        ? warning
        : $"{_startupRecoveryWarning} {warning}";
    _logService.Info(warning);
}

private void DeleteMeeting(MeetingItem item)
{
    Meetings.Remove(item);
    SaveMeetings();
    RefreshMeetingViews();
    RefreshSearchResults();
    DictationStatus = "Deleted meeting";
}
private void DeleteMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        DeleteMeeting(item);
    }
}
private void OpenMeetingDetail_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        OpenMeetingDetail(item);
        ShowPage(MeetingsPage, MeetingsNav);
    }
}
private void BackToMeetings_Click(object sender, RoutedEventArgs e)
{
    SaveActiveSpeakerAliases();
    _selectedMeeting = null;
    _selectedMeetingTemplate = NormalizeSummaryTemplateName(SelectedSummaryTemplate);
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    MeetingWarningsPanel.Visibility = Visibility.Collapsed;
    MeetingWarningsItems.ItemsSource = null;
}
private void ShowMeetingNotesTab_Click(object sender, MouseButtonEventArgs e)
{
    _lastMeetingDetailShowTranscript = false;
    ShowMeetingDetailTab(showTranscript: false);
}
private void ShowMeetingTranscriptTab_Click(object sender, MouseButtonEventArgs e)
{
    _lastMeetingDetailShowTranscript = true;
    ShowMeetingDetailTab(showTranscript: true);
}
private void ShowMeetingDetailTab(bool showTranscript)
{
    MeetingNotesPanel.Visibility = showTranscript ? Visibility.Collapsed : Visibility.Visible;
    MeetingTranscriptPanel.Visibility = showTranscript ? Visibility.Visible : Visibility.Collapsed;
    MeetingNotesTab.Background = showTranscript
        ? System.Windows.Media.Brushes.Transparent
        : (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush");
    MeetingTranscriptTab.Background = showTranscript
        ? (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush")
        : System.Windows.Media.Brushes.Transparent;
    MeetingNotesTabLabel.Foreground = showTranscript
        ? (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
        : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
    MeetingTranscriptTabLabel.Foreground = showTranscript
        ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
        : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
}
private void BuildSpeakerAliasPanel()
{
    SpeakerAliasPanel.Children.Clear();
    if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.Transcript))
        return;

    var labels = DetectSpeakerLabels(_selectedMeeting.Transcript);
    if (labels.Count == 0)
        return;

    var header = new TextBlock
    {
        Text = "Speakers",
        Style = (Style)FindResource("SectionLabel"),
        Margin = new Thickness(0, 0, 0, 8)
    };
    SpeakerAliasPanel.Children.Add(header);

    var rows = new StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
    foreach (var label in labels)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Width = 80
        };

        var aliasBox = new System.Windows.Controls.TextBox
        {
            Text = _activeSpeakerAliases.TryGetValue(label, out var alias) ? alias : "",
            FontSize = 13,
            Padding = new Thickness(8, 4, 8, 4),
            Background = (System.Windows.Media.Brush)FindResource("BackgroundHoverBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 200,
            Tag = label
        };
        aliasBox.TextChanged += (_, _) =>
        {
            _activeSpeakerAliases[(string)aliasBox.Tag] = aliasBox.Text;
            OnPropertyChanged(nameof(SelectedMeetingTranscript));
            OnPropertyChanged(nameof(SelectedMeetingNotes));
            BuildMeetingNotesContent();
            _aliasSaveDebounceTimer.Stop();
            _aliasSaveDebounceTimer.Start();
        };

        row.Children.Add(labelText);
        row.Children.Add(aliasBox);
        rows.Children.Add(row);
    }

    SpeakerAliasPanel.Children.Add(rows);
}

private void SaveActiveSpeakerAliases()
{
    if (_selectedMeeting is null)
        return;

    var filtered = _activeSpeakerAliases
        .Where(p => !string.IsNullOrWhiteSpace(p.Value) && p.Key != p.Value.Trim())
        .ToDictionary(p => p.Key, p => p.Value.Trim());

    if (filtered.Count == 0 && (_selectedMeeting.SpeakerAliases is null || _selectedMeeting.SpeakerAliases.Count == 0))
        return;

    var index = Meetings.IndexOf(_selectedMeeting);
    if (index < 0)
        return;

    var updated = _selectedMeeting with { SpeakerAliases = filtered };
    Meetings[index] = updated;
    _selectedMeeting = updated;
    SaveMeetings();
}

private void BuildMeetingWarningsPanel(MeetingItem item)
{
    var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings(item.HealthWarnings, item.Transcript);
    if (warnings.Count == 0)
    {
        MeetingWarningsPanel.Visibility = Visibility.Collapsed;
        MeetingWarningsItems.ItemsSource = null;
        return;
    }

    MeetingWarningsItems.ItemsSource = warnings;
    MeetingWarningsPanel.Visibility = Visibility.Visible;
}

private static System.Windows.Controls.Grid CreateWrappedNoteRow(UIElement leading, TextBlock content)
{
    var row = new System.Windows.Controls.Grid
    {
        Margin = new Thickness(0, 2, 0, 2),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
    };
    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
    {
        Width = System.Windows.GridLength.Auto
    });
    row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
    {
        Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star)
    });

    content.TextWrapping = TextWrapping.Wrap;
    content.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;

    System.Windows.Controls.Grid.SetColumn(leading, 0);
    System.Windows.Controls.Grid.SetColumn(content, 1);
    row.Children.Add(leading);
    row.Children.Add(content);
    return row;
}

private static void SetMarkdownInlineText(TextBlock target, string text)
{
    target.Inlines.Clear();
    var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\*\*(.+?)\*\*");
    if (matches.Count == 0)
    {
        target.Text = text;
        return;
    }

    target.Text = "";
    var index = 0;
    foreach (System.Text.RegularExpressions.Match match in matches)
    {
        if (match.Index > index)
        {
            target.Inlines.Add(new System.Windows.Documents.Run(text[index..match.Index]));
        }

        target.Inlines.Add(new System.Windows.Documents.Bold(new System.Windows.Documents.Run(match.Groups[1].Value)));
        index = match.Index + match.Length;
    }

    if (index < text.Length)
    {
        target.Inlines.Add(new System.Windows.Documents.Run(text[index..]));
    }
}

private void BuildMeetingNotesContent()
{
    MeetingNotesContent.Children.Clear();

    var text = SelectedMeetingNotes;
    if (string.IsNullOrWhiteSpace(text))
    {
        var emptyState = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 4)
        };
        emptyState.Children.Add(new TextBlock
        {
            Text = "\uE70F",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 28,
            Foreground = (System.Windows.Media.Brush)FindResource("TextTertiaryBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        emptyState.Children.Add(new TextBlock
        {
            Text = "No notes yet",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 6)
        });
        emptyState.Children.Add(new TextBlock
        {
            Text = "Generate structured notes from this meeting transcript using the selected template.",
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var generateButton = new WpfButton
        {
            Content = "Generate Notes",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        generateButton.Click += GenerateSelectedMeetingNotes_Click;
        emptyState.Children.Add(generateButton);
        MeetingNotesContent.Children.Add(emptyState);
        return;
    }

    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
    foreach (var rawLine in lines)
    {
        var line = rawLine.TrimEnd();
        if (string.IsNullOrWhiteSpace(line))
        {
            MeetingNotesContent.Children.Add(new System.Windows.Controls.Grid { Height = 8 });
            continue;
        }

        var headingMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,3})\s+(.+)$");
        if (headingMatch.Success)
        {
            var level = headingMatch.Groups[1].Value.Length;
            var headingText = headingMatch.Groups[2].Value.Trim();
            MeetingNotesContent.Children.Add(new TextBlock
            {
                Text = headingText,
                FontWeight = FontWeights.Bold,
                FontSize = level == 1 ? 22 : (level == 2 ? 17 : 14),
                Foreground = (System.Windows.Media.Brush)FindResource(level <= 2 ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, level == 1 ? 4 : 18, 0, level == 3 ? 6 : 10)
            });
            continue;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^---+\s*$"))
        {
            MeetingNotesContent.Children.Add(new Border
            {
                Height = 1,
                Background = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
                Margin = new Thickness(0, 8, 0, 8)
            });
            continue;
        }

        var checkboxMatch = System.Text.RegularExpressions.Regex.Match(line, @"^-\s+\[([ xX])\]\s*(.*)$");
        if (checkboxMatch.Success)
        {
            var isChecked = checkboxMatch.Groups[1].Value.Trim().Equals("x", StringComparison.OrdinalIgnoreCase);
            var itemText = checkboxMatch.Groups[2].Value.Trim();
            var icon = new TextBlock
            {
                Text = isChecked ? "\uE73D" : "\uE739",
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource(isChecked ? "AccentBlueBrush" : "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 8, 0)
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, itemText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(icon, content));
            continue;
        }

        var bulletMatch = System.Text.RegularExpressions.Regex.Match(line, @"^[-\u2022]\s+(.+)$");
        if (bulletMatch.Success)
        {
            var bulletText = bulletMatch.Groups[1].Value.Trim();
            var bullet = new TextBlock
            {
                Text = "\u2022",
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, bulletText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(bullet, content));
            continue;
        }

        var numberedMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\.\s+(.+)$");
        if (numberedMatch.Success)
        {
            var number = numberedMatch.Groups[1].Value.Trim();
            var numText = numberedMatch.Groups[2].Value.Trim();
            var index = new TextBlock
            {
                Text = number + ".",
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0),
                Width = 20
            };
            var content = new TextBlock
            {
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap
            };
            SetMarkdownInlineText(content, numText);
            MeetingNotesContent.Children.Add(CreateWrappedNoteRow(index, content));
            continue;
        }

        var paragraph = new TextBlock
        {
            FontSize = 14,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
            LineHeight = 22
        };
        SetMarkdownInlineText(paragraph, line);
        MeetingNotesContent.Children.Add(paragraph);
    }
}
private async void GenerateSelectedMeetingNotes_Click(object sender, RoutedEventArgs e)
{
    await GenerateSelectedMeetingNotesAsync();
}

private async Task GenerateSelectedMeetingNotesAsync()
{
    if (_selectedMeeting is null || string.IsNullOrWhiteSpace(_selectedMeeting.Transcript))
    {
        return;
    }

    if (!string.IsNullOrWhiteSpace(_selectedMeeting.Summary))
    {
        var result = System.Windows.MessageBox.Show(
            $"Regenerate notes for \"{_selectedMeeting.Title}\" using the \"{SelectedMeetingTemplate}\" template?",
            "Regenerate notes",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
    }

    try
    {
        DictationStatus = "Generating meeting notes";
        _toastNotificationService.Show("Generating notes", SelectedMeetingTemplate, ToastState.Transcribing, 0);
        var summaryInput = string.IsNullOrWhiteSpace(_selectedMeeting.ManualNotes)
            ? _selectedMeeting.Transcript
            : $"{_selectedMeeting.Transcript}{Environment.NewLine}{Environment.NewLine}Manual notes supplied by the user:{Environment.NewLine}{_selectedMeeting.ManualNotes}";
        var summary = await MeetingSummaryService.CreateSummaryAsync(
            summaryInput,
            _selectedMeeting.Title,
            CurrentSettingsSnapshot() with
            {
                MeetingSummaryTemplate = SelectedMeetingTemplate,
                MeetingSummaryPromptOverride = CustomMeetingTemplates.FirstOrDefault(template =>
                    template.Name.Equals(SelectedMeetingTemplate, StringComparison.OrdinalIgnoreCase))?.Prompt ?? ""
            });

        var index = Meetings.IndexOf(_selectedMeeting);
        if (index < 0)
        {
            return;
        }

        var updated = _selectedMeeting with
        {
            Summary = summary,
            TemplateName = SelectedMeetingTemplate
        };
        Meetings[index] = updated;
        _selectedMeeting = updated;
        SaveMeetings();
        RefreshSearchResults();
        BuildMeetingWarningsPanel(updated);
        BuildMeetingNotesContent();
        OnPropertyChanged(nameof(SelectedMeetingNotes));
        OnPropertyChanged(nameof(SelectedMeetingNotesActionLabel));
        DictationStatus = "Meeting notes ready";
        _toastNotificationService.Show("Notes ready", updated.Title, ToastState.Success, 2800);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Notes generation failed: {exception.Message}";
        _toastNotificationService.Show("Notes generation failed", exception.Message, ToastState.Error, 4200);
    }
}

private void MoreMeetingActions_Click(object sender, RoutedEventArgs e)
{
    if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
    {
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }
}
private void ExportMeetingNotes_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is not null)
    {
        MeetingExporter.Export(_selectedMeeting, MeetingExportMode.Notes, _activeSpeakerAliases);
    }
}
private void ExportMeetingTranscript_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is not null)
    {
        MeetingExporter.Export(_selectedMeeting, MeetingExportMode.Transcript, _activeSpeakerAliases);
    }
}
private void ExportFullMeeting_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is not null)
    {
        MeetingExporter.Export(_selectedMeeting, MeetingExportMode.FullMeeting, _activeSpeakerAliases);
    }
}
private void CopySelectedMeetingNotes_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    if (string.IsNullOrWhiteSpace(SelectedMeetingNotes))
    {
        DictationStatus = "No notes to copy";
        _toastNotificationService.Show("No notes yet", "Generate notes first", ToastState.Error, 2600);
        return;
    }
    System.Windows.Clipboard.SetText(SelectedMeetingNotes);
    DictationStatus = "Copied meeting notes";
    _toastNotificationService.Show("Copied notes", _selectedMeeting.Title, ToastState.Success);
}
private void CopySelectedMeetingTranscript_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    System.Windows.Clipboard.SetText(SelectedMeetingTranscript);
    DictationStatus = "Copied transcript";
    _toastNotificationService.Show("Copied transcript", _selectedMeeting.Title, ToastState.Success);
}
private void OpenSelectedMeetingAudio_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is not null)
    {
        OpenMeetingAudio(_selectedMeeting);
    }
}
private void DeleteSelectedMeeting_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null)
    {
        return;
    }
    var item = _selectedMeeting;
    BackToMeetings_Click(sender, e);
    DeleteMeeting(item);
}
private void MoveMeeting_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingItem item } element)
    {
        return;
    }
    OpenMoveMeetingMenu(element, item);
}
private void MoveSelectedMeeting_Click(object sender, RoutedEventArgs e)
{
    if (_selectedMeeting is null || sender is not FrameworkElement element)
    {
        return;
    }
    OpenMoveMeetingMenu(element, _selectedMeeting);
}
private void OpenMoveMeetingMenu(FrameworkElement placementTarget, MeetingItem meeting)
{
    var menu = new ContextMenu();
    AddMoveMenuItem(menu, "All Meetings", meeting, null);
    if (MeetingFolders.Count > 0)
    {
        menu.Items.Add(new Separator());
    }
    foreach (var folder in MeetingFolders)
    {
        AddMoveMenuItem(menu, folder.Name, meeting, folder.Id);
    }
    menu.PlacementTarget = placementTarget;
    menu.IsOpen = true;
}
private void AddMoveMenuItem(ContextMenu menu, string header, MeetingItem meeting, string? folderId)
{
    var item = new MenuItem
    {
        Header = header,
        Tag = new MoveMeetingRequest(meeting.Id, folderId)
    };
    item.Click += MoveMeetingToFolder_Click;
    menu.Items.Add(item);
}
private void MoveMeetingToFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not MenuItem { Tag: MoveMeetingRequest request })
    {
        return;
    }
    var index = Meetings.ToList().FindIndex(meeting => meeting.Id == request.MeetingId);
    if (index < 0)
    {
        return;
    }
    var updated = Meetings[index] with { FolderId = request.FolderId };
    Meetings[index] = updated;
    if (_selectedMeeting?.Id == updated.Id)
    {
        _selectedMeeting = updated;
        OnPropertyChanged(nameof(SelectedMeetingMetadata));
    }
    SaveMeetings();
    RefreshMeetingViews();
    DictationStatus = "Moved meeting";
}
private async void ImportMeeting_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFileDialog
    {
        Title = "Import meeting audio or video",
        Filter = "Media files|*.wav;*.mp3;*.m4a;*.aac;*.mp4;*.mov;*.mkv;*.webm;*.ogg|All files|*.*"
    };
    if (dialog.ShowDialog(this) != true)
    {
        return;
    }
    try
    {
        DictationStatus = "Transcribing meeting";
        _toastNotificationService.Show("Transcribing meeting", System.IO.Path.GetFileName(dialog.FileName), ToastState.Transcribing, 0);
        var result = await _meetingTranscriptionClient.TranscribeFileAsync(
            System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
            dialog.FileName,
            new TranscriptionOptions(SelectedMeetingAsrEngine, SelectedMeetingModelProfile, WorkerLanguageHint(SelectedDictationLanguage)));
        var rawTranscript = result.Text;
        var transcript = RemoveFillerWords ? FillerWordFilter.Apply(rawTranscript) : rawTranscript;
        transcript = DictionaryCorrectionService.Apply(transcript, DictionaryEntries.Select(entry => entry.Record));
        transcript = await PostProcessIfEnabledAsync(transcript, "meeting import", _meetingTranscriptionClient.PostProcessAsync);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            DictationStatus = "No speech detected in imported meeting";
            _toastNotificationService.Show("No speech detected", System.IO.Path.GetFileName(dialog.FileName), ToastState.Error, 3600);
            return;
        }
        var summary = await CreateMeetingSummaryAsync(transcript, System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
        var wordCount = CountWords(transcript);
        var meeting = new MeetingItem(
            $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
            DateTime.Now,
            transcript,
            summary,
            dialog.FileName,
            SelectedMeetingModelProfile,
            result.DurationMs,
            _selectedMeetingFolderId,
            wordCount,
            SelectedSummaryTemplate,
            RawTranscript: rawTranscript,
            Origin: "import",
            TranscriptSegments: (result.Segments ?? [])
                .Select(segment => new MeetingTranscriptSegment(
                    "import",
                    "Imported audio",
                    segment.StartMs,
                    segment.EndMs,
                    segment.Text))
                .ToList(),
            DiarizationStatus: "not-applicable");
        Meetings.Insert(0, meeting);
        SaveMeetings();
        AutoExportMeeting(meeting);
        RefreshMeetingViews();
        RefreshSearchResults();
        DictationStatus = "Meeting transcribed";
        _toastNotificationService.Show("Meeting ready", meeting.Title, ToastState.Success);
        ShowPage(MeetingsPage, MeetingsNav);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Meeting import failed: {exception.Message}";
        _toastNotificationService.Show("Meeting import failed", exception.Message, ToastState.Error, 4200);
    }
}
private async void ToggleMeetingRecording_Click(object sender, RoutedEventArgs e)
{
    await ToggleMeetingRecordingAsync(null);
}
private async Task ToggleMeetingRecordingAsync(
    string? detectedTitle,
    int? targetProcessId = null,
    string? detectedMeetingKey = null)
{
    if (_meetingRecordingCoordinator.IsBusy)
    {
        return;
    }
    if (!_isMeetingRecording)
    {
        try
        {
            if (targetProcessId is null)
            {
                var detected = _meetingDetectionService.CheckNow(publish: false).DetectedMeeting;
                if (detected is not null)
                {
                    detectedTitle ??= detected.Title;
                    targetProcessId = detected.ProcessId;
                    detectedMeetingKey = detected.Key;
                }
            }
            DictationStatus = "Recording meeting";
            _toastNotificationService.Show("Recording meeting", "Starting microphone and meeting audio capture", ToastState.Recording, 0);
            var sessionId = Guid.NewGuid().ToString("N");
            var startedAt = DateTime.Now;
            var provisionalTitle = string.IsNullOrWhiteSpace(detectedTitle)
                ? $"Meeting {startedAt:yyyy-MM-dd HH-mm}"
                : detectedTitle;
            _meetingSessionJournal.Save(new ActiveMeetingSession(
                sessionId,
                provisionalTitle,
                startedAt,
                targetProcessId,
                detectedMeetingKey,
                "starting"));
            MeetingRecordingStartResult startResult;
            try
            {
                startResult = await _meetingRecordingCoordinator.StartAsync(SelectedMeetingMicrophone, targetProcessId, sessionId);
            }
            catch
            {
                _meetingSessionJournal.Clear();
                throw;
            }
            _meetingSessionJournal.Save(new ActiveMeetingSession(
                sessionId,
                provisionalTitle,
                startedAt,
                targetProcessId,
                detectedMeetingKey,
                startResult.RemoteAudioMode));
            _currentMeetingTitle = detectedTitle;
            _currentMeetingKey = detectedMeetingKey;
            _currentMeetingProcessId = targetProcessId;
            _currentMeetingRecordId = $"meet_{sessionId}";
            _isMeetingRecording = true;
            StartMeetingAutoStopMonitor();
            if (startResult.UsedAllSystemFallback)
            {
                _logService.Info(startResult.Warning ?? "Using all-system audio fallback.");
                _toastNotificationService.Show(
                    "Recording with fallback",
                    "Muesli could not isolate the meeting app, so other computer sounds may be included.",
                    ToastState.Error,
                    6000);
            }
            else if (startResult.RemoteAudioMode == "meeting-process")
            {
                _toastNotificationService.Show("Recording meeting", "Capturing microphone and meeting-app audio only", ToastState.Recording, 0);
            }
            else
            {
                _toastNotificationService.Show("Recording meeting", "Capturing microphone only", ToastState.Recording, 0);
            }
            OnPropertyChanged(nameof(MeetingRecordingButtonText));
            ShowPage(MeetingsPage, MeetingsNav);
        }
        catch (Exception exception)
        {
            DictationStatus = $"Meeting recording failed: {exception.Message}";
            _toastNotificationService.Show("Meeting recording failed", exception.Message, ToastState.Error);
        }
        return;
    }
    MeetingItem? processingMeeting = null;
    RecordedMeetingResult? recordedResult = null;
    try
    {
        BeginPostMeetingPromptCooldown(_currentMeetingKey);
        StopMeetingAutoStopMonitor();
        DictationStatus = "Transcribing meeting";
        _toastNotificationService.Show("Transcribing meeting", "Processing local meeting audio", ToastState.Transcribing, 0);
        _isMeetingRecording = false;
        OnPropertyChanged(nameof(MeetingRecordingButtonText));
        var title = string.IsNullOrWhiteSpace(_currentMeetingTitle)
            ? $"Meeting {DateTime.Now:yyyy-MM-dd HH-mm}"
            : _currentMeetingTitle;
        var result = await _meetingRecordingCoordinator.StopAsync(
            title,
            new TranscriptionOptions(SelectedMeetingAsrEngine, SelectedMeetingModelProfile, WorkerLanguageHint(SelectedDictationLanguage)),
            capture =>
            {
                processingMeeting = new MeetingItem(
                    _currentMeetingRecordId ?? $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    capture.Title,
                    capture.StartedAt,
                    "",
                    "",
                    CombineAudioPaths(capture.MicAudioPath, capture.SystemAudioPath),
                    SelectedMeetingModelProfile,
                    capture.DurationMs,
                    _selectedMeetingFolderId,
                    0,
                    SelectedSummaryTemplate,
                    HealthWarnings: capture.HealthWarnings,
                    Status: "processing");
                Meetings.Insert(0, processingMeeting);
                SaveMeetings();
                RefreshMeetingViews();
                RefreshSearchResults();
            });
        recordedResult = result;
        _currentMeetingTitle = null;
        _currentMeetingKey = null;
        _currentMeetingProcessId = null;
        _currentMeetingRecordId = null;
        if (processingMeeting is not null)
        {
            var checkpoint = processingMeeting with
            {
                Transcript = result.Transcript,
                RawTranscript = result.Transcript,
                WordCount = CountWords(result.Transcript),
                HealthWarnings = result.HealthWarnings ?? [],
                TranscriptSegments = result.TranscriptSegments ?? [],
                DiarizationStatus = result.DiarizationStatus
            };
            ReplaceProcessingMeeting(processingMeeting, checkpoint);
            processingMeeting = checkpoint;
            SaveMeetings();
        }
        var rawTranscript = result.Transcript;
        var transcript = RemoveFillerWords ? FillerWordFilter.Apply(rawTranscript) : rawTranscript;
        transcript = DictionaryCorrectionService.Apply(transcript, DictionaryEntries.Select(entry => entry.Record));
        transcript = await PostProcessIfEnabledAsync(transcript, "meeting", _meetingTranscriptionClient.PostProcessAsync);
        if (processingMeeting is not null)
        {
            var checkpoint = processingMeeting with
            {
                Transcript = transcript,
                WordCount = CountWords(transcript)
            };
            ReplaceProcessingMeeting(processingMeeting, checkpoint);
            processingMeeting = checkpoint;
            SaveMeetings();
        }
        if (string.IsNullOrWhiteSpace(transcript))
        {
            var retainedAudioPath = CombineAudioPaths(result.MicAudioPath, result.SystemAudioPath);
            var warnings = (result.HealthWarnings ?? []).ToList();
            warnings.Add("No speech was detected. This session is retained as failed so its outcome is not silently lost.");
            var failedMeeting = new MeetingItem(
                processingMeeting?.Id ?? $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                result.Title,
                result.StartedAt,
                "",
                "",
                retainedAudioPath,
                SelectedMeetingModelProfile,
                result.DurationMs,
                _selectedMeetingFolderId,
                0,
                SelectedSummaryTemplate,
                HealthWarnings: warnings,
                RawTranscript: rawTranscript,
                Status: "failed",
                TranscriptSegments: result.TranscriptSegments ?? [],
                DiarizationStatus: result.DiarizationStatus);
            ReplaceProcessingMeeting(processingMeeting, failedMeeting);
            SaveMeetings();
            DeleteTemporaryMeetingAudio(result.TemporaryAudioPaths, retainedAudioPath);
            _meetingSessionJournal.Clear();
            AutoExportMeeting(failedMeeting);
            RefreshMeetingViews();
            RefreshSearchResults();
            DictationStatus = "No speech detected in meeting";
            _toastNotificationService.Show("Meeting retained: no speech detected", "A compact recovery copy of the audio is still available", ToastState.Error, 5200);
            return;
        }
        var summary = await CreateMeetingSummaryAsync(transcript, result.Title);
        var wordCount = CountWords(transcript);
        var completionStatus = (result.HealthWarnings ?? []).Any(warning =>
            warning.Contains("partial transcript", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("transcription failed", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("capture failed", StringComparison.OrdinalIgnoreCase))
            ? "partial"
            : "completed";
        var sourceAudioPath = completionStatus == "partial"
            ? CombineAudioPaths(result.MicAudioPath, result.SystemAudioPath)
            : "";
        var meeting = new MeetingItem(
            processingMeeting?.Id ?? $"meet_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            result.Title,
            result.StartedAt,
            transcript,
            summary,
            sourceAudioPath,
            SelectedMeetingModelProfile,
            result.DurationMs,
            _selectedMeetingFolderId,
            wordCount,
            SelectedSummaryTemplate,
            HealthWarnings: result.HealthWarnings ?? new List<string>(),
            RawTranscript: rawTranscript,
            Status: completionStatus,
            TranscriptSegments: result.TranscriptSegments ?? [],
            DiarizationStatus: result.DiarizationStatus);
        ReplaceProcessingMeeting(processingMeeting, meeting);
        SaveMeetings();
        var cleanupFailures = DeleteTemporaryMeetingAudio(result.TemporaryAudioPaths, sourceAudioPath);
        if (cleanupFailures.Count > 0)
        {
            var cleanupWarning = $"Muesli could not remove {cleanupFailures.Count} temporary audio file(s).";
            var meetingWithCleanupWarning = meeting with
            {
                HealthWarnings = (meeting.HealthWarnings ?? []).Append(cleanupWarning).ToList()
            };
            ReplaceProcessingMeeting(meeting, meetingWithCleanupWarning);
            meeting = meetingWithCleanupWarning;
            SaveMeetings();
            _logService.Info($"{cleanupWarning} {string.Join("; ", cleanupFailures)}");
        }
        _meetingSessionJournal.Clear();
        AutoExportMeeting(meeting);
        RefreshMeetingViews();
        RefreshSearchResults();
        DictationStatus = string.IsNullOrWhiteSpace(transcript) ? "Meeting saved with no detected speech" : "Meeting ready";
        _toastNotificationService.Show("Meeting ready", meeting.Title, ToastState.Success);
        ShowPage(MeetingsPage, MeetingsNav);
    }
    catch (Exception exception)
    {
        StopMeetingAutoStopMonitor();
        _currentMeetingTitle = null;
        _currentMeetingKey = null;
        _currentMeetingProcessId = null;
        _currentMeetingRecordId = null;
        _isMeetingRecording = false;
        OnPropertyChanged(nameof(MeetingRecordingButtonText));
        if (processingMeeting is not null)
        {
            var warnings = (processingMeeting.HealthWarnings ?? []).ToList();
            warnings.Add($"Meeting processing failed: {exception.Message}");
            var failedMeeting = processingMeeting with
            {
                Status = "failed",
                HealthWarnings = warnings
            };
            ReplaceProcessingMeeting(processingMeeting, failedMeeting);
            SaveMeetings();
            if (recordedResult is not null)
            {
                DeleteTemporaryMeetingAudio(recordedResult.TemporaryAudioPaths, failedMeeting.SourcePath);
            }
            else if (_meetingSessionJournal.Load() is { } activeSession)
            {
                DeleteTemporaryMeetingAudio(_meetingSessionJournal.DiscoverAudio(activeSession), failedMeeting.SourcePath);
            }
            _meetingSessionJournal.Clear();
            RefreshMeetingViews();
            RefreshSearchResults();
        }
        _logService.Error("Meeting recording or processing failed.", exception);
        DictationStatus = $"Meeting recording failed: {exception.Message}";
        _toastNotificationService.Show("Meeting recording failed", exception.Message, ToastState.Error, 4200);
    }
}

private void ReplaceProcessingMeeting(MeetingItem? processingMeeting, MeetingItem replacement)
{
    if (processingMeeting is not null)
    {
        var index = Meetings.IndexOf(processingMeeting);
        if (index >= 0)
        {
            Meetings[index] = replacement;
            return;
        }
    }

    Meetings.Insert(0, replacement);
}

private static string CombineAudioPaths(string micPath, string? systemPath) =>
    string.Join("; ", new[] { micPath, systemPath }.Where(path => !string.IsNullOrWhiteSpace(path)));

private async void JoinAndRecordUpcoming_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: UpcomingMeetingItem item })
    {
        if (!string.IsNullOrWhiteSpace(item.MeetingUrl))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.MeetingUrl) { UseShellExecute = true });
        }
        await ToggleMeetingRecordingAsync(item.Title);
    }
}
private async void RecordOnlyUpcoming_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: UpcomingMeetingItem item })
    {
        await ToggleMeetingRecordingAsync(item.Title);
    }
}
private void StartMeetingAutoStopMonitor()
{
    _meetingMissingScanCount = 0;
    _meetingEndPromptSnoozedUntil = DateTime.MinValue;
    _meetingEndPromptVisible = false;
    _meetingAutoStopTimer.Stop();
    _meetingAutoStopTimer.Start();
}
private void StopMeetingAutoStopMonitor()
{
    _meetingMissingScanCount = 0;
    if (_meetingEndPromptVisible)
    {
        _meetingPromptService.Close();
    }
    _meetingEndPromptVisible = false;
    _meetingAutoStopTimer.Stop();
}
private async void MeetingAutoStopTimer_Tick(object? sender, EventArgs e)
{
    if (!_isMeetingRecording)
    {
        StopMeetingAutoStopMonitor();
        return;
    }
    if (!_meetingRecordingCoordinator.IsRecording && !_meetingRecordingCoordinator.IsBusy)
    {
        ResetMeetingRecordingUi("Meeting recording ended");
        return;
    }
    if (MeetingRecordingSafetyPolicy.IsDiskCriticallyLow())
    {
        _logService.Info("Meeting recording stopped because the capture drive has less than 1 GB free.");
        _toastNotificationService.Show("Recording stopped", "Less than 1 GB of disk space remains. Muesli is saving the meeting now.", ToastState.Error, 7000);
        await ToggleMeetingRecordingAsync(null);
        return;
    }

    if (string.IsNullOrWhiteSpace(_currentMeetingKey))
    {
        return;
    }

    var scan = _meetingDetectionService.CheckNow(publish: false);
    var sameMeeting = MeetingRecordingSafetyPolicy.IsSameMeeting(
        _currentMeetingKey,
        _currentMeetingProcessId,
        scan.DetectedMeeting);
    if (sameMeeting)
    {
        _meetingMissingScanCount = 0;
        return;
    }
    var currentPlatform = MeetingRecordingSafetyPolicy.PlatformFromKey(_currentMeetingKey);
    if (_meetingDetectionService.HasLivePresenceForPlatform(currentPlatform))
    {
        _meetingMissingScanCount = 0;
        return;
    }
    _meetingMissingScanCount++;
    if (_meetingMissingScanCount < MeetingRecordingSafetyPolicy.MissingScansBeforeConfirmation
        || _meetingRecordingCoordinator.IsBusy
        || _meetingEndPromptVisible
        || DateTime.Now < _meetingEndPromptSnoozedUntil)
    {
        return;
    }
    _meetingEndPromptVisible = true;
    _logService.Info("Meeting window disappeared; showing the end-of-meeting recording card.");
    _meetingPromptService.ShowMeetingEnded(
        _currentMeetingTitle ?? "Meeting",
        () => Dispatcher.InvokeAsync(async () =>
        {
            _meetingEndPromptVisible = false;
            await ToggleMeetingRecordingAsync(null);
        }),
        () => Dispatcher.Invoke(() =>
        {
            _meetingEndPromptVisible = false;
            _meetingMissingScanCount = 0;
            _meetingEndPromptSnoozedUntil = DateTime.Now.AddMinutes(
                MeetingRecordingSafetyPolicy.ContinueRecordingSnoozeMinutes);
            _logService.Info("User confirmed the meeting recording should continue.");
        }));
}

private void BeginPostMeetingPromptCooldown(string? meetingKey)
{
    var platform = MeetingRecordingSafetyPolicy.PlatformFromKey(meetingKey);
    if (string.IsNullOrWhiteSpace(platform))
    {
        return;
    }

    _meetingPromptCooldownPlatform = platform;
    _meetingPromptCooldownUntil = DateTime.Now.AddMinutes(
        MeetingRecordingSafetyPolicy.PostMeetingPromptCooldownMinutes);
    _ignoredMeetingPrompts[meetingKey!] = _meetingPromptCooldownUntil;
}
private void AliasSaveDebounceTimer_Tick(object? sender, EventArgs e)
{
    _aliasSaveDebounceTimer.Stop();
    SaveActiveSpeakerAliases();
}
private void ResetMeetingRecordingUi(string status)
{
    StopMeetingAutoStopMonitor();
    _currentMeetingTitle = null;
    _currentMeetingKey = null;
    _currentMeetingProcessId = null;
    _currentMeetingRecordId = null;
    _isMeetingRecording = false;
    OnPropertyChanged(nameof(MeetingRecordingButtonText));
    DictationStatus = status;
    _toastNotificationService.ShowIdle(SelectedHotkey);
}
private void AddDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    var phrase = DictionaryPhraseBox.Text.Trim();
    var replacement = DictionaryReplacementBox.Text.Trim();
    if (string.IsNullOrWhiteSpace(phrase) || string.IsNullOrWhiteSpace(replacement))
    {
        DictationStatus = "Dictionary entry needs both fields";
        return;
    }
    DictionaryEntries.Insert(0, new DictionaryEntryItem(
        $"dictentry_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
        phrase,
        replacement,
        DictionaryThresholdSlider.Value));
    DictionaryPhraseBox.Text = "";
    DictionaryReplacementBox.Text = "";
    SaveDictionary();
    OnPropertyChanged(nameof(HasDictionaryEntries));
}
private void SaveDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    SaveDictionary();
    DictationStatus = "Dictionary saved";
    OnPropertyChanged(nameof(HasDictionaryEntries));
}
private void ImportDictionary_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFileDialog
    {
        Title = "Import Muesli dictionary",
        Filter = "JSON files|*.json|All files|*.*"
    };
    if (dialog.ShowDialog(this) != true) return;
    try
    {
        var imported = DictionaryPortabilityService.Import(dialog.FileName);
        foreach (var record in imported)
        {
            var existing = DictionaryEntries.FirstOrDefault(item => item.Phrase.Equals(record.Phrase, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                DictionaryEntries.Add(new DictionaryEntryItem(record));
            }
            else
            {
                existing.Replacement = record.Replacement;
                existing.MatchingThreshold = record.MatchingThreshold;
            }
        }
        SaveDictionary();
        OnPropertyChanged(nameof(HasDictionaryEntries));
        DictationStatus = $"Imported {imported.Count} dictionary entries";
        _toastNotificationService.Show("Dictionary imported", $"{imported.Count} entries processed", ToastState.Success, 2800);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Dictionary import failed: {exception.Message}";
        _toastNotificationService.Show("Dictionary import failed", exception.Message, ToastState.Error, 4200);
    }
}
private void AutoExportMeeting(MeetingItem meeting)
{
    if (!AutoExportMeetings) return;
    try
    {
        var path = MeetingExporter.ExportMarkdownAutomatically(meeting, AutoExportDirectory, meeting.SpeakerAliases);
        _logService.Info($"Automatically exported meeting markdown: {path}");
    }
    catch (Exception exception)
    {
        _logService.Error("Automatic meeting export failed.", exception);
        DictationStatus = $"Meeting saved; automatic export failed: {exception.Message}";
    }
}

private void ExportDictionary_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.SaveFileDialog
    {
        Title = "Export Muesli dictionary",
        Filter = "JSON files|*.json",
        FileName = "muesli-dictionary.json",
        DefaultExt = ".json"
    };
    if (dialog.ShowDialog(this) != true) return;
    try
    {
        DictionaryPortabilityService.Export(dialog.FileName, DictionaryEntries.Select(item => item.Record));
        DictationStatus = "Dictionary exported";
        _toastNotificationService.Show("Dictionary exported", dialog.FileName, ToastState.Success, 2800);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Dictionary export failed: {exception.Message}";
        _toastNotificationService.Show("Dictionary export failed", exception.Message, ToastState.Error, 4200);
    }
}
private async void RefreshRuntimeDiagnostics_Click(object sender, RoutedEventArgs e)
{
    await RefreshRuntimeDiagnosticsAsync();
}
private void OpenModelCache_Click(object sender, RoutedEventArgs e)
{
    try
    {
        _runtimeDiagnosticsService.OpenModelCacheDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open model cache: {exception.Message}";
        _logService.Error("Could not open model cache.", exception);
    }
}
private void OpenLogs_Click(object sender, RoutedEventArgs e)
{
    try
    {
        _logService.OpenLogDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open logs: {exception.Message}";
    }
}
private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
{
    if (_isUpdateReady && _pendingUpdate is not null)
    {
        await ApplyPendingUpdateAsync(manualPrompt: true);
        return;
    }

    var manager = EnsureUpdateManager();
    if (manager is null || !manager.IsInstalled)
    {
        DictationStatus = "Opening release page";
        OpenExternalUrl("https://github.com/Muesli-HQ/Muesli-Windows/releases", "Could not open release page");
        return;
    }

    if (_updateCheckInFlight)
    {
        return;
    }

    _updateCheckInFlight = true;
    try
    {
        DictationStatus = "Checking for updates";
        _toastNotificationService.Show("Checking for updates", "Contacting GitHub", ToastState.Transcribing, 0);
        var info = await manager.CheckForUpdatesAsync();
        if (info is null)
        {
            DictationStatus = "Muesli is up to date";
            _toastNotificationService.Show("Up to date", "You're on the latest release.", ToastState.Success, 3600);
            return;
        }

        _toastNotificationService.Show("Downloading update", info.TargetFullRelease.Version.ToString(), ToastState.Transcribing, 0);
        await manager.DownloadUpdatesAsync(info);
        _pendingUpdate = info;
        IsUpdateReady = true;
        _toastNotificationService.Show("Update ready", "Click 'Restart and update' to apply.", ToastState.Success, 4200);
        await ApplyPendingUpdateAsync(manualPrompt: true);
    }
    catch (Exception exception)
    {
        DictationStatus = $"Update check failed: {exception.Message}";
        _logService.Error("Update check failed.", exception);
        _toastNotificationService.Show("Update check failed", exception.Message, ToastState.Error, 5200);
    }
    finally
    {
        _updateCheckInFlight = false;
    }
}

public bool IsUpdateReady
{
    get => _isUpdateReady;
    private set
    {
        if (SetField(ref _isUpdateReady, value))
        {
            OnPropertyChanged(nameof(UpdateButtonLabel));
        }
    }
}

public string UpdateButtonLabel => _isUpdateReady ? "Restart and update" : "Check Now";

private UpdateManager? EnsureUpdateManager()
{
    if (_updateManager is not null)
    {
        return _updateManager;
    }
    try
    {
        var source = new GithubSource("https://github.com/Muesli-HQ/Muesli-Windows", null, false);
        _updateManager = new UpdateManager(source);
        return _updateManager;
    }
    catch (Exception exception)
    {
        _logService.Error("Could not initialize update manager.", exception);
        return null;
    }
}

private void StartBackgroundUpdateCheck()
{
    if (_updateCheckInFlight) return;
    var manager = EnsureUpdateManager();
    if (manager is null || !manager.IsInstalled) return;
    _ = Task.Run(async () =>
    {
        try
        {
            var info = await manager.CheckForUpdatesAsync();
            if (info is null) return;
            await manager.DownloadUpdatesAsync(info);
            await Dispatcher.InvokeAsync(() =>
            {
                _pendingUpdate = info;
                IsUpdateReady = true;
                _toastNotificationService.Show("Update ready",
                    $"v{info.TargetFullRelease.Version} is ready. Restart Muesli to apply.",
                    ToastState.Success, 5200);
            });
        }
        catch (Exception exception)
        {
            _logService.Error("Background update check failed.", exception);
        }
    });
}

private Task ApplyPendingUpdateAsync(bool manualPrompt)
{
    if (_pendingUpdate is null) return Task.CompletedTask;
    if (_updateManager is null) return Task.CompletedTask;

    if (_meetingRecordingCoordinator.IsRecording || _dictationCoordinator.IsBusy)
    {
        if (manualPrompt)
        {
            _toastNotificationService.Show("Update deferred",
                "Stop the active recording or dictation first.", ToastState.Error, 4200);
        }
        return Task.CompletedTask;
    }

    if (manualPrompt)
    {
        var version = _pendingUpdate.TargetFullRelease.Version.ToString();
        var result = System.Windows.MessageBox.Show(
            $"Restart Muesli now to install v{version}?",
            "Update ready",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes) return Task.CompletedTask;
    }

    try
    {
        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate);
    }
    catch (Exception exception)
    {
        _logService.Error("Apply update failed.", exception);
        _toastNotificationService.Show("Update failed", exception.Message, ToastState.Error, 5200);
    }
    return Task.CompletedTask;
}
private void Donate_Click(object sender, RoutedEventArgs e)
{
    OpenExternalUrl("https://buymeacoffee.com/phequals7", "Could not open donation link");
}
private void ViewGitHub_Click(object sender, RoutedEventArgs e)
{
    OpenExternalUrl("https://github.com/Muesli-HQ/Muesli-Windows", "Could not open GitHub");
}
private void OpenExternalUrl(string url, string failurePrefix)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch (Exception exception)
    {
        DictationStatus = $"{failurePrefix}: {exception.Message}";
        _logService.Error(failurePrefix, exception);
    }
}
private async void ClearModelCache_Click(object sender, RoutedEventArgs e)
{
    var result = System.Windows.MessageBox.Show(
        this,
        "Delete downloaded local model files from the Muesli cache? They will download again when needed.",
        "Clear model cache",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
    if (result != MessageBoxResult.Yes)
    {
        return;
    }
    try
    {
        _runtimeDiagnosticsService.ClearModelCache();
        DictationStatus = "Model cache cleared";
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not clear model cache: {exception.Message}";
        _logService.Error("Could not clear model cache.", exception);
    }
}
private async void DownloadWhisperModel_Click(object sender, RoutedEventArgs e)
{
    try
    {
        DictationStatus = $"Downloading {SelectedModelProfile}";
        _toastNotificationService.Show("Downloading model", SelectedModelProfile, ToastState.Transcribing, 0);
        var result = await _dictationCoordinator.DownloadModelAsync("whisper", SelectedModelProfile);
        DictationStatus = result.Text;
        _toastNotificationService.Show("Model ready", SelectedModelProfile, ToastState.Success, 3600);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Model download failed: {exception.Message}";
        _logService.Error($"Model download failed for {SelectedModelProfile}.", exception);
        _toastNotificationService.Show("Model download failed", exception.Message, ToastState.Error, 5200);
    }
}
private async Task EnsureBaseModelDownloadedAsync()
{
    if (string.Equals(Environment.GetEnvironmentVariable("MUESLI_SKIP_AUTODOWNLOAD"), "1", StringComparison.Ordinal))
    {
        return;
    }
    if (_runtimeDiagnosticsService.IsWhisperModelCached("base"))
    {
        return;
    }
    try
    {
        DictationStatus = "Downloading base model";
        _toastNotificationService.Show("Downloading base model", "First-run setup", ToastState.Transcribing, 0);
        var result = await _dictationCoordinator.DownloadModelAsync("whisper", "base");
        DictationStatus = result.Text;
        _toastNotificationService.Show("Model ready", "base", ToastState.Success, 3600);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Base model download failed: {exception.Message}";
        _logService.Error("Base model auto-download failed.", exception);
        _toastNotificationService.Show("Model download failed", "Retry from Models → Download base.", ToastState.Error, 5200);
    }
}
public string SetupReadiness
{
    get => _setupReadiness;
    private set => SetField(ref _setupReadiness, value);
}
private void SetParakeetActive_Click(object sender, RoutedEventArgs e)
{
    SelectedAsrEngine = "parakeet-v3";
    DictationStatus = "Parakeet selected";
    _toastNotificationService.Show("Parakeet selected", "Install optional Parakeet runtime dependencies before first use", ToastState.Success, 3600);
}
private async void TestParakeet_Click(object sender, RoutedEventArgs e)
{
    try
    {
        DictationStatus = "Testing Parakeet runtime";
        _toastNotificationService.Show("Testing Parakeet", "Checking NVIDIA/CUDA backend", ToastState.Transcribing, 0);
        var result = await _meetingTranscriptionClient.DownloadModelAsync("parakeet", "parakeet-v3");
        DictationStatus = result.Text;
        _toastNotificationService.Show("Parakeet ready", "NVIDIA backend initialized", ToastState.Success, 4200);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Parakeet test failed: {exception.Message}";
        _logService.Error("Parakeet readiness test failed.", exception);
        var noNvidia = exception.Message.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        var toastTitle = noNvidia ? "Parakeet needs an NVIDIA GPU" : "Parakeet unavailable";
        _toastNotificationService.Show(toastTitle, exception.Message, ToastState.Error, 6200);
    }
}
private async void DownloadQwenModel_Click(object sender, RoutedEventArgs e)
{
    var model = Environment.GetEnvironmentVariable("MUESLI_POST_PROCESSOR_MODEL") ?? "Qwen/Qwen2.5-3B-Instruct";
    try
    {
        DictationStatus = "Downloading Qwen cleanup model";
        _toastNotificationService.Show("Downloading Qwen", model, ToastState.Transcribing, 0);
        var result = await _meetingTranscriptionClient.DownloadModelAsync("postprocess", model);
        DictationStatus = result.Text;
        _toastNotificationService.Show("Qwen ready", model, ToastState.Success, 3600);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Qwen download failed: {exception.Message}";
        _toastNotificationService.Show("Qwen download failed", exception.Message, ToastState.Error, 5200);
    }
}
private async void TestDiarization_Click(object sender, RoutedEventArgs e)
{
    try
    {
        DictationStatus = "Testing diarization model";
        _toastNotificationService.Show("Testing diarization", "Loading pyannote speaker model", ToastState.Transcribing, 0);
        var result = await _meetingTranscriptionClient.DownloadModelAsync("diarization", "pyannote/speaker-diarization-3.1");
        DictationStatus = result.Text;
        _toastNotificationService.Show("Diarization ready", "Speaker model loaded", ToastState.Success, 3600);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Diarization test failed: {exception.Message}";
        _logService.Error("Diarization readiness test failed.", exception);
        _toastNotificationService.Show("Diarization unavailable", exception.Message, ToastState.Error, 6200);
    }
}
private async void TestQwenCleanup_Click(object sender, RoutedEventArgs e)
{
    try
    {
        DictationStatus = "Testing Qwen cleanup";
        _toastNotificationService.Show("Testing cleanup", "Running local post-processing", ToastState.Transcribing, 0);
        var sample = "um make a todo list buy milk and then email the team and schedule follow up";
        var result = await _meetingTranscriptionClient.PostProcessAsync(sample, "diagnostic", PostProcessingPrompt);
        DictationStatus = string.IsNullOrWhiteSpace(result.Text) ? "Qwen cleanup returned no text" : "Qwen cleanup ready";
        _toastNotificationService.Show("Cleanup ready", result.Text, ToastState.Success, 5200);
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Qwen cleanup test failed: {exception.Message}";
        _logService.Error("Qwen post-processing readiness test failed.", exception);
        _toastNotificationService.Show("Cleanup unavailable", "Install/download Qwen before enabling cleanup", ToastState.Error, 6200);
    }
}
private void DeleteDictionaryEntry_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: DictionaryEntryItem item })
    {
        DictionaryEntries.Remove(item);
        SaveDictionary();
        OnPropertyChanged(nameof(HasDictionaryEntries));
    }
}
private void DictationsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
{
    if (sender is System.Windows.Controls.ListBox { SelectedItem: DictationItem item })
    {
        System.Windows.Clipboard.SetText(item.Text);
        DictationStatus = "Copied";
        _toastNotificationService.Show("Copied", item.Text, ToastState.Success);
    }
}
private void MeetingCard_Click(object sender, MouseButtonEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingItem item })
    {
        OpenMeetingDetail(item);
        ShowPage(MeetingsPage, MeetingsNav);
    }
}
private void ShowDictations_Click(object sender, RoutedEventArgs e) => ShowPage(DictationsPage, DictationsNav);
private void ClearSearch_Click(object sender, RoutedEventArgs e) => SearchQuery = "";
private void ShowMeetings_Click(object sender, RoutedEventArgs e)
{
    SaveActiveSpeakerAliases();
    _selectedMeetingFolderId = null;
    _selectedMeeting = null;
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    RefreshMeetingViews();
    ShowPage(MeetingsPage, MeetingsNav);
}
private void ShowDictionary_Click(object sender, RoutedEventArgs e) => ShowPage(DictionaryPage, DictionaryNav);
private void ShowModels_Click(object sender, RoutedEventArgs e) => ShowPage(ModelsPage, ModelsNav);
private void ShowShortcuts_Click(object sender, RoutedEventArgs e) => ShowPage(ShortcutsPage, ShortcutsNav);
private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNav);
private void ShowAbout_Click(object sender, RoutedEventArgs e) => ShowPage(AboutPage, AboutNav);
private void SetLightTheme_Click(object sender, RoutedEventArgs e)
{
    SetTheme("light");
}
private void SetDarkTheme_Click(object sender, RoutedEventArgs e)
{
    SetTheme("dark");
}
private void SetTheme(string theme)
{
    try
    {
        _theme = theme;
        ApplyTheme(_theme);
        RefreshNavButtonStyles();
        SaveSettings();
        DictationStatus = theme.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? "Light mode enabled"
            : "Dark mode enabled";
        OnPropertyChanged(nameof(SelectedTheme));
    }
    catch (Exception exception)
    {
        _logService.Error("Theme switch failed.", exception);
        DictationStatus = $"Theme switch failed: {exception.Message}";
        _toastNotificationService.Show("Theme switch failed", exception.Message, ToastState.Error, 4200);
    }
}
private void ToggleMeetings_Click(object sender, RoutedEventArgs e)
{
    _meetingsExpanded = !_meetingsExpanded;
    MeetingsChildren.Visibility = _meetingsExpanded ? Visibility.Visible : Visibility.Collapsed;
    OnPropertyChanged(nameof(MeetingsChevron));
    ShowPage(MeetingsPage, MeetingsNav);
}
private void AddMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    var baseName = "New Folder";
    var index = 1;
    var name = baseName;
    while (MeetingFolders.Any(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
    {
        index++;
        name = $"{baseName} {index}";
    }
    var folder = new MeetingFolderItem($"folder_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", name);
    MeetingFolders.Add(folder);
    SaveActiveSpeakerAliases();
    _selectedMeetingFolderId = folder.Id;
    _selectedMeeting = null;
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    SaveMeetingFolders();
    RefreshMeetingViews();
    ShowPage(MeetingsPage, MeetingsNav);
}
private void ManageTemplates_Click(object sender, RoutedEventArgs e)
{
    var window = new Window
    {
        Owner = this,
        Title = "Manage Templates",
        Width = 760,
        Height = 560,
        MinWidth = 680,
        MinHeight = 480,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBaseBrush"),
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        FontFamily = FontFamily,
        Content = BuildTemplatesManagerContent()
    };
    window.ShowDialog();
}
private FrameworkElement BuildTemplatesManagerContent()
{
    var root = new DockPanel { Margin = new Thickness(24) };
    var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
    DockPanel.SetDock(header, Dock.Top);
    root.Children.Add(header);
    var done = new WpfButton
    {
        Content = "Done",
        Style = (Style)FindResource("SecondaryButton"),
        Width = 88,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    done.Click += (_, _) => Window.GetWindow(done)?.Close();
    DockPanel.SetDock(done, Dock.Right);
    header.Children.Add(done);
    var create = new WpfButton
    {
        Content = "+ New template",
        Style = (Style)FindResource("SecondaryButton"),
        Width = 128,
        Margin = new Thickness(0, 0, 8, 0),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    DockPanel.SetDock(create, Dock.Right);
    header.Children.Add(create);
    var titleStack = new StackPanel();
    titleStack.Children.Add(new TextBlock
    {
        Text = "Manage Templates",
        FontSize = 22,
        FontWeight = FontWeights.SemiBold,
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
    });
    titleStack.Children.Add(new TextBlock
    {
        Text = "Create reusable prompt-based note formats for meetings.",
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = 13,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
    });
    header.Children.Add(titleStack);
    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
    root.Children.Add(grid);
    var templates = new WpfListBox
    {
        ItemsSource = CustomMeetingTemplates,
        Background = System.Windows.Media.Brushes.Transparent,
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        BorderThickness = new Thickness(0),
        MinHeight = 360,
        ItemContainerStyle = (Style)FindResource("MuesliListBoxItem")
    };
    templates.ItemTemplate = BuildMeetingTemplateItemTemplate();
    var listWrap = new Border
    {
        Background = (System.Windows.Media.Brush)FindResource("BackgroundRaisedBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Child = templates
    };
    Grid.SetColumn(listWrap, 0);
    grid.Children.Add(listWrap);
    var form = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
    Grid.SetColumn(form, 1);
    grid.Children.Add(form);
    form.Children.Add(new TextBlock
    {
        Text = "TEMPLATE",
        Style = (Style)FindResource("SectionLabel")
    });
    var editorCard = new Border
    {
        Background = (System.Windows.Media.Brush)FindResource("BackgroundRaisedBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(16)
    };
    form.Children.Add(editorCard);
    var editor = new StackPanel();
    editorCard.Child = editor;
    var nameBox = new WpfTextBox
    {
        Style = (Style)FindResource("MuesliTextBox"),
        Height = 34
    };
    var promptBox = new WpfTextBox
    {
        Style = (Style)FindResource("MuesliTextBox"),
        Margin = new Thickness(0, 6, 0, 0),
        MinHeight = 180,
        Padding = new Thickness(10),
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    editor.Children.Add(new TextBlock { Text = "Name", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
    editor.Children.Add(nameBox);
    editor.Children.Add(new TextBlock { Text = "Prompt", Margin = new Thickness(0, 12, 0, 0), FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush") });
    editor.Children.Add(promptBox);
    templates.SelectionChanged += (_, _) =>
    {
        if (templates.SelectedItem is not MeetingTemplateItem template)
        {
            return;
        }
        nameBox.Text = template.Name;
        promptBox.Text = template.Prompt;
    };
    var actions = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
    editor.Children.Add(actions);
    create.Click += (_, _) =>
    {
        nameBox.Text = "";
        promptBox.Text = "";
        templates.SelectedItem = null;
        nameBox.Focus();
    };
    var cancel = new WpfButton { Content = "Cancel", Style = (Style)FindResource("GhostButton") };
    cancel.Click += (_, _) =>
    {
        nameBox.Text = "";
        promptBox.Text = "";
        templates.SelectedItem = null;
    };
    actions.Children.Add(cancel);
    var save = new WpfButton { Content = "Save changes", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("SecondaryButton") };
    save.Click += (_, _) =>
    {
        var name = nameBox.Text.Trim();
        var prompt = promptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
        {
            _toastNotificationService.Show("Template needs name and prompt", "Enter both fields", ToastState.Error, 3200);
            return;
        }
        if (templates.SelectedItem is MeetingTemplateItem existing)
        {
            existing.Name = name;
            existing.Prompt = prompt;
            templates.Items.Refresh();
        }
        else
        {
            var item = new MeetingTemplateItem(name, prompt);
            CustomMeetingTemplates.Add(item);
            templates.SelectedItem = item;
        }
        SaveMeetingTemplates();
        DictationStatus = "Template saved";
    };
    actions.Children.Add(save);
    var delete = new WpfButton { Content = "Delete", Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("GhostButton"), Foreground = System.Windows.Media.Brushes.IndianRed };
    delete.Click += (_, _) =>
    {
        if (templates.SelectedItem is not MeetingTemplateItem selected)
        {
            return;
        }
        CustomMeetingTemplates.Remove(selected);
        SummaryTemplates.Remove(selected.Name);
        SaveMeetingTemplates();
        nameBox.Text = "";
        promptBox.Text = "";
    };
    actions.Children.Add(delete);
    if (CustomMeetingTemplates.Count == 0)
    {
        nameBox.Text = "";
        promptBox.Text = "";
    }
    else
    {
        templates.SelectedIndex = 0;
    }
    return root;
}
private static DataTemplate BuildMeetingTemplateItemTemplate()
{
    var template = new DataTemplate(typeof(MeetingTemplateItem));
    var border = new FrameworkElementFactory(typeof(Border));
    border.SetValue(Border.PaddingProperty, new Thickness(12));
    border.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 8));
    border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
    border.SetResourceReference(Border.BackgroundProperty, "BackgroundRaisedBrush");
    border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushSoft");
    border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
    var stack = new FrameworkElementFactory(typeof(StackPanel));
    stack.SetValue(StackPanel.OrientationProperty, WpfOrientation.Vertical);
    border.AppendChild(stack);
    var title = new FrameworkElementFactory(typeof(TextBlock));
    title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeetingTemplateItem.Name)));
    title.SetValue(TextBlock.FontSizeProperty, 12.0);
    title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
    title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
    stack.AppendChild(title);
    var prompt = new FrameworkElementFactory(typeof(TextBlock));
    prompt.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(MeetingTemplateItem.Prompt)));
    prompt.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
    prompt.SetValue(TextBlock.FontSizeProperty, 12.0);
    prompt.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
    prompt.SetValue(TextBlock.MaxHeightProperty, 38.0);
    prompt.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
    prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
    stack.AppendChild(prompt);
    template.VisualTree = border;
    return template;
}
private void SelectMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    _selectedMeetingFolderId = folder.Id;
    _selectedMeeting = null;
    MeetingsBrowserView.Visibility = Visibility.Visible;
    MeetingDetailView.Visibility = Visibility.Collapsed;
    RefreshMeetingViews();
    ShowPage(MeetingsPage, MeetingsNav);
}
private void RenameMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    folder.IsRenaming = true;
}
private void FolderNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    if (e.Key == Key.Enter)
    {
        CommitFolderRename(folder);
        e.Handled = true;
    }
    else if (e.Key == Key.Escape)
    {
        folder.IsRenaming = false;
        e.Handled = true;
    }
}
private void FolderNameBox_LostFocus(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder } && folder.IsRenaming)
    {
        CommitFolderRename(folder);
    }
}
private void CommitFolderRename(MeetingFolderItem folder)
{
    folder.Name = string.IsNullOrWhiteSpace(folder.Name) ? "New Folder" : folder.Name.Trim();
    folder.IsRenaming = false;
    SaveMeetingFolders();
    RefreshMeetingViews();
}
private void MoveMeetingFolderUp_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        MoveMeetingFolder(folder, -1);
    }
}
private void MoveMeetingFolderDown_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        MoveMeetingFolder(folder, 1);
    }
}
private void MoveMeetingFolder(MeetingFolderItem folder, int direction)
{
    var oldIndex = MeetingFolders.IndexOf(folder);
    var newIndex = oldIndex + direction;
    if (oldIndex < 0 || newIndex < 0 || newIndex >= MeetingFolders.Count)
    {
        return;
    }
    MeetingFolders.Move(oldIndex, newIndex);
    SaveMeetingFolders();
    RefreshMeetingViews();
}
private void DeleteMeetingFolder_Click(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement { DataContext: MeetingFolderItem folder })
    {
        return;
    }
    var result = System.Windows.MessageBox.Show(
        this,
        "Delete this folder? Meetings inside it will stay in All Meetings.",
        "Delete folder",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question);
    if (result != MessageBoxResult.Yes)
    {
        return;
    }
    MeetingFolders.Remove(folder);
    for (var index = 0; index < Meetings.Count; index++)
    {
        if (string.Equals(Meetings[index].FolderId, folder.Id, StringComparison.Ordinal))
        {
            Meetings[index] = Meetings[index] with { FolderId = null };
        }
    }
    if (string.Equals(_selectedMeetingFolderId, folder.Id, StringComparison.Ordinal))
    {
        _selectedMeetingFolderId = null;
    }
    SaveMeetingFolders();
    SaveMeetings();
    RefreshMeetingViews();
}
private void ToggleMeetingSort_Click(object sender, RoutedEventArgs e)
{
    _meetingSortNewestFirst = !_meetingSortNewestFirst;
    var sorted = _meetingSortNewestFirst
        ? Meetings.OrderByDescending(item => item.CreatedAt).ToList()
        : Meetings.OrderBy(item => item.CreatedAt).ToList();
    Meetings.Clear();
    foreach (var item in sorted)
    {
        Meetings.Add(item);
    }
    OnPropertyChanged(nameof(MeetingSortLabel));
}
private void OpenButtonContextMenu_Click(object sender, RoutedEventArgs e)
{
    if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
    {
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }
}
private void SetDictationFilter_Click(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.MenuItem { Tag: string filter })
    {
        return;
    }
    _dictationDateFilter = filter;
    FilteredDictations.Refresh();
    OnPropertyChanged(nameof(DictationHeaderLabel));
    OnPropertyChanged(nameof(DictationFilterLabel));
    OnPropertyChanged(nameof(HasDictations));
}
private void SetMeetingFilter_Click(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.MenuItem { Tag: string filter })
    {
        return;
    }
    _meetingDateFilter = filter;
    RefreshMeetingViews();
    OnPropertyChanged(nameof(MeetingFilterLabel));
}
private bool PassesMeetingFolder(object item)
{
    return _selectedMeetingFolderId is null ||
           item is MeetingItem meeting &&
           string.Equals(meeting.FolderId, _selectedMeetingFolderId, StringComparison.Ordinal);
}
private bool PassesSearch(object item)
{
    var query = _searchQuery.Trim();
    if (query.Length == 0)
    {
        return true;
    }
    return item switch
    {
        DictationItem dictation => ContainsSearch(dictation.Text, query) ||
                                  ContainsSearch(dictation.ModelProfile, query),
        MeetingItem meeting => ContainsSearch(meeting.Title, query) ||
                               ContainsSearch(meeting.Summary, query) ||
                               ContainsSearch(meeting.Transcript, query) ||
                               ContainsSearch(meeting.Metadata, query),
        _ => true
    };
}
private static bool ContainsSearch(string value, string query)
{
    return value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
private void RefreshMeetingViews()
{
    UpdateMeetingFolderCounts();
    FilteredMeetings.Refresh();
    OnPropertyChanged(nameof(MeetingCount));
    OnPropertyChanged(nameof(VisibleMeetingCount));
    OnPropertyChanged(nameof(CurrentMeetingFolderName));
    OnPropertyChanged(nameof(HasMeetings));
}
private void UpdateMeetingFolderCounts()
{
    foreach (var folder in MeetingFolders)
    {
        folder.Count = Meetings.Count(meeting => string.Equals(meeting.FolderId, folder.Id, StringComparison.Ordinal));
    }
}
private static bool PassesDateFilter(object item, string filter)
{
    if (filter == "all")
    {
        return true;
    }
    var date = item switch
    {
        DictationItem dictation => dictation.Timestamp,
        MeetingItem meeting => meeting.CreatedAt,
        _ => DateTime.MinValue
    };
    return date >= DateTime.Now - FilterWindow(filter);
}
private static TimeSpan FilterWindow(string filter)
{
    return filter switch
    {
        "last2Days" => TimeSpan.FromDays(2),
        "lastWeek" => TimeSpan.FromDays(7),
        "last2Weeks" => TimeSpan.FromDays(14),
        "lastMonth" => TimeSpan.FromDays(31),
        "last3Months" => TimeSpan.FromDays(93),
        _ => TimeSpan.MaxValue
    };
}
private static string FilterLabel(string filter)
{
    return filter switch
    {
        "last2Days" => "Last 2 days",
        "lastWeek" => "Last week",
        "last2Weeks" => "Last 2 weeks",
        "lastMonth" => "Last month",
        "last3Months" => "Last 3 months",
        _ => "All time"
    };
}
private string NormalizeSummaryTemplateName(string? value)
{
    var normalized = MeetingSummaryService.NormalizeTemplateName(value);
    if (MeetingSummaryService.IsBuiltInTemplate(normalized))
    {
        return normalized;
    }

    var custom = SummaryTemplates.FirstOrDefault(template => template.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    return custom ?? "Standard Meeting Notes";
}
private void ShowPage(UIElement activePage, System.Windows.Controls.Button activeNav)
{
    foreach (var page in new UIElement[]
             {
                 DictationsPage,
                 SearchPage,
                 MeetingsPage,
                 DictionaryPage,
                 ModelsPage,
                 ShortcutsPage,
                 SettingsPage,
                 AboutPage
             })
    {
        page.Visibility = page == activePage ? Visibility.Visible : Visibility.Collapsed;
    }
    if (activePage != SearchPage)
    {
        _lastNonSearchPage = activePage;
        _lastNonSearchNav = activeNav;
    }
    foreach (var button in new[]
             {
                 DictationsNav,
                 MeetingsNav,
                 AllMeetingsNav,
                 DictionaryNav,
                 ModelsNav,
                 ShortcutsNav,
                 SettingsNav,
                 AboutNav
             })
    {
        var isActive = button == activeNav;
        button.Background = isActive
            ? (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush")
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = isActive
            ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }
}
private void RefreshNavButtonStyles()
{
    if (DictationsPage.Visibility == Visibility.Visible)
        ShowPage(DictationsPage, DictationsNav);
    else if (MeetingsPage.Visibility == Visibility.Visible)
        ShowPage(MeetingsPage, MeetingsNav);
    else if (DictionaryPage.Visibility == Visibility.Visible)
        ShowPage(DictionaryPage, DictionaryNav);
    else if (ModelsPage.Visibility == Visibility.Visible)
        ShowPage(ModelsPage, ModelsNav);
    else if (ShortcutsPage.Visibility == Visibility.Visible)
        ShowPage(ShortcutsPage, ShortcutsNav);
    else if (SettingsPage.Visibility == Visibility.Visible)
        ShowPage(SettingsPage, SettingsNav);
    else if (AboutPage.Visibility == Visibility.Visible)
        ShowPage(AboutPage, AboutNav);
    else if (SearchPage.Visibility == Visibility.Visible)
        ShowPage(SearchPage, _lastNonSearchNav ?? DictationsNav);
}
private void UpdateSearchPageVisibility()
{
    if (!string.IsNullOrWhiteSpace(SearchQuery))
    {
        if (SearchPage.Visibility != Visibility.Visible)
        {
            ShowPage(SearchPage, _lastNonSearchNav ?? DictationsNav);
        }
        return;
    }
    if (SearchPage.Visibility == Visibility.Visible)
    {
        ShowPage(_lastNonSearchPage ?? DictationsPage, _lastNonSearchNav ?? DictationsNav);
    }
}
private void RefreshSearchResults()
{
    SearchDictationResults.Refresh();
    SearchMeetingResults.Refresh();
    OnPropertyChanged(nameof(SearchDictationCount));
    OnPropertyChanged(nameof(SearchMeetingCount));
    OnPropertyChanged(nameof(SearchResultsSummary));
    OnPropertyChanged(nameof(HasSearchResults));
}
private void Minimize_Click(object sender, RoutedEventArgs e)
{
    WindowState = WindowState.Minimized;
}
private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
{
    if (_isWorkAreaMaximized)
    {
        RestoreFromWorkAreaMaximize();
        return;
    }
    MaximizeToWorkArea();
}
private void MaximizeToWorkArea()
{
    if (WindowState == WindowState.Minimized)
    {
        WindowState = WindowState.Normal;
    }
    _restoreBounds = new Rect(Left, Top, Width, Height);
    var area = SystemParameters.WorkArea;
    WindowState = WindowState.Normal;
    Left = area.Left;
    Top = area.Top;
    Width = area.Width;
    Height = area.Height;
    _isWorkAreaMaximized = true;
}
private void RestoreFromWorkAreaMaximize()
{
    WindowState = WindowState.Normal;
    Left = _restoreBounds.Left;
    Top = _restoreBounds.Top;
    Width = _restoreBounds.Width;
    Height = _restoreBounds.Height;
    _isWorkAreaMaximized = false;
}
private void Close_Click(object sender, RoutedEventArgs e)
{
    Hide();
}
private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
{
    if (e.ButtonState == MouseButtonState.Pressed && e.GetPosition(this).Y < 48)
    {
        DragMove();
    }
}
private string? PromptForText(string title, string label, string initialValue)
{
    var dialog = new Window
    {
        Owner = this,
        Title = title,
        Width = 360,
        Height = 170,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        Background = (System.Windows.Media.Brush)FindResource("BackgroundBaseBrush")
    };
    var input = new System.Windows.Controls.TextBox
    {
        Text = initialValue,
        Height = 36,
        Padding = new Thickness(10, 7, 10, 7),
        Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        Background = (System.Windows.Media.Brush)FindResource("BackgroundHoverBrush"),
        BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrushSoft")
    };
    var save = new System.Windows.Controls.Button
    {
        Content = "Save",
        Width = 90,
        Height = 32,
        Margin = new Thickness(8, 0, 0, 0),
        Style = (Style)FindResource("PrimaryButton")
    };
    var cancel = new System.Windows.Controls.Button
    {
        Content = "Cancel",
        Width = 90,
        Height = 32,
        Style = (Style)FindResource("GhostButton")
    };
    save.Click += (_, _) => dialog.DialogResult = true;
    cancel.Click += (_, _) => dialog.DialogResult = false;
    var buttons = new StackPanel
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        Margin = new Thickness(0, 16, 0, 0)
    };
    buttons.Children.Add(cancel);
    buttons.Children.Add(save);
    var content = new StackPanel
    {
        Margin = new Thickness(18)
    };
    content.Children.Add(new TextBlock
    {
        Text = label,
        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        Margin = new Thickness(0, 0, 0, 8)
    });
    content.Children.Add(input);
    content.Children.Add(buttons);
    dialog.Content = content;
    input.SelectAll();
    input.Focus();
    return dialog.ShowDialog() == true ? input.Text : null;
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
private async Task<string> PostProcessIfEnabledAsync(
    string text,
    string context,
    Func<string, string, string, Task<PostProcessingResult>> postProcessor)
{
    if (!PostProcessingEnabled || string.IsNullOrWhiteSpace(text))
    {
        return text;
    }
    try
    {
        DictationStatus = "Cleaning transcript";
        _toastNotificationService.Show("Cleaning transcript", "Applying Qwen post-processing", ToastState.Transcribing, 0);
        var result = await postProcessor(text, context, PostProcessingPrompt);
        if (!string.IsNullOrWhiteSpace(result.Diagnostic) &&
            result.Diagnostic.Contains("fallback", StringComparison.OrdinalIgnoreCase))
        {
            DictationStatus = "Cleaned with fallback";
        }
        return string.IsNullOrWhiteSpace(result.Text) ? text : result.Text.Trim();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Post-processing skipped: {exception.Message}";
        _toastNotificationService.Show("Post-processing skipped", "Raw transcript kept", ToastState.Error, 3200);
        return text;
    }
}
private async Task<string> CreateMeetingSummaryAsync(string transcript, string title)
{
    DictationStatus = SelectedSummaryProvider == "local" ? "Creating local summary" : "Creating AI summary";
    _toastNotificationService.Show("Summarizing meeting", SelectedSummaryProvider, ToastState.Transcribing, 0);
    try
    {
        return await MeetingSummaryService.CreateSummaryAsync(transcript, title, CurrentSettingsSnapshot());
    }
    catch (Exception exception)
    {
        _logService.Error($"The selected '{SelectedSummaryProvider}' summary provider failed; preserving the transcript without generated notes.", exception);
        DictationStatus = $"Transcript saved; {SelectedSummaryProvider} summary failed";
        _toastNotificationService.Show("Transcript preserved", $"{SelectedSummaryProvider} summary failed: {exception.Message}", ToastState.Error, 5200);
        return "";
    }
}
private async Task RefreshRuntimeDiagnosticsAsync()
{
    try
    {
        RuntimeDiagnostics = "Checking runtime...";
        SetupReadiness = "Checking setup...";
        RuntimeSetupStatus = "";
        var diagnostics = await _runtimeDiagnosticsService.InspectAsync();
        RuntimeDiagnostics = diagnostics.Summary;
        ModelCacheDirectory = diagnostics.ModelCacheDirectory;
        ModelCacheSize = diagnostics.ModelCacheSize;
        ApplyRuntimeDiagnostics(diagnostics);
        _logService.Info($"Runtime diagnostics refreshed. {SetupReadiness.Replace(Environment.NewLine, " | ")}");
        _logService.Info($"Runtime diagnostics details. {diagnostics.Summary.Replace(Environment.NewLine, " | ")}");
    }
    catch (Exception exception)
    {
        RuntimeDiagnostics = $"Diagnostics failed: {exception.Message}";
        SetupReadiness = "Setup check failed. Open logs for details.";
        RuntimeSetupStatus = "Setup check failed. Open logs for details.";
        CanInstallLocalRuntime = !string.IsNullOrWhiteSpace(WorkerRuntimeLocator.FindSetupScriptOrNull());
        TranscriptionWorkerStatus = "Needs setup";
        WhisperRuntimeStatus = "Needs setup";
        SelectedModelCacheStatus = "Not downloaded";
        SpeakerDiarizationStatusLabel = "Optional setup needed";
        GpuRuntimeStatus = "CPU mode";
        QwenRuntimeStatus = "Optional, not installed";
        ParakeetRuntimeStatus = "Optional, not installed";
        DiarizationDependencyStatus = "Optional setup needed";
        DiarizationTokenStatus = "Optional if model access fails";
        _logService.Error("Runtime diagnostics failed.", exception);
    }
}
private void ApplyRuntimeDiagnostics(RuntimeDiagnostics diagnostics)
{
    var workerOk = File.Exists(diagnostics.WorkerScript);
    var whisperOk = HasReadySignal(diagnostics.DependencyStatus);
    var qwenOk = HasReadySignal(diagnostics.PostProcessingDependencyStatus);
    var parakeetOk = HasReadySignal(diagnostics.ParakeetDependencyStatus);
    var diarizationOk = HasReadySignal(diagnostics.DiarizationDependencyStatus);
    var selectedModelCached = SelectedAsrEngine != "whisper" || _runtimeDiagnosticsService.IsWhisperModelCached(SelectedModelProfile);
    var hasHfToken = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HF_TOKEN"));
    var hasPythonOverride = HasValidPythonOverride();
    var setupScriptAvailable = !string.Equals(diagnostics.SetupScript, "setup-worker-runtime.ps1 not found", StringComparison.OrdinalIgnoreCase);
    var workerAssetsPresent = WorkerRuntimeLocator.HasWorkerRequirementsLayout();

    TranscriptionWorkerStatus = workerOk ? "Ready" : "Needs setup";
    WhisperRuntimeStatus = whisperOk ? "Ready" : "Needs setup";
    SelectedModelCacheStatus = selectedModelCached ? "Cached" : "Not downloaded";
    SpeakerDiarizationStatusLabel = diarizationOk ? "Ready" : "Optional setup needed";
    GpuRuntimeStatus = diagnostics.CudaStatus.Contains("CUDA available", StringComparison.OrdinalIgnoreCase) ? "CUDA available" : "CPU mode";
    var noNvidiaGpu = diagnostics.CudaStatus.Contains("CUDA not available", StringComparison.OrdinalIgnoreCase);
    QwenRuntimeStatus = qwenOk ? "Ready" : "Optional, not installed";
    ParakeetRuntimeStatus = parakeetOk
        ? "Ready"
        : noNvidiaGpu ? "Requires NVIDIA GPU" : "Optional, not installed";
    DiarizationDependencyStatus = SpeakerDiarizationStatusLabel;
    DiarizationTokenStatus = hasHfToken ? "Configured" : "Optional if model access fails";
    CanInstallLocalRuntime = !hasPythonOverride && setupScriptAvailable && workerAssetsPresent && (!workerOk || !whisperOk);
    if (workerOk && whisperOk)
    {
        RuntimeSetupStatus = selectedModelCached
            ? "Local transcription runtime is installed."
            : "Local runtime is ready. Download a Whisper model before offline use.";
    }
    else if (hasPythonOverride)
    {
        RuntimeSetupStatus = "MUESLI_PYTHON override is active. Fix that Python environment or clear the override to use Muesli's setup flow.";
    }
    else if (!workerAssetsPresent)
    {
        RuntimeSetupStatus = "This install is missing worker runtime files. Reinstall Muesli.";
    }
    else if (!setupScriptAvailable)
    {
        RuntimeSetupStatus = "Runtime setup script is missing from this build. Reinstall Muesli.";
    }
    else
    {
        RuntimeSetupStatus = "Install local transcription runtime to finish setup.";
    }
    SetupReadiness = BuildSetupReadiness(workerOk, whisperOk, selectedModelCached, diarizationOk, setupScriptAvailable, workerAssetsPresent, hasPythonOverride);
}
private string BuildSetupReadiness(RuntimeDiagnostics diagnostics)
{
    var workerOk = File.Exists(diagnostics.WorkerScript);
    var whisperOk = HasReadySignal(diagnostics.DependencyStatus);
    var selectedModelCached = SelectedAsrEngine != "whisper" || _runtimeDiagnosticsService.IsWhisperModelCached(SelectedModelProfile);
    var diarizationOk = HasReadySignal(diagnostics.DiarizationDependencyStatus);
    var setupScriptAvailable = !string.Equals(diagnostics.SetupScript, "setup-worker-runtime.ps1 not found", StringComparison.OrdinalIgnoreCase);
    var workerAssetsPresent = WorkerRuntimeLocator.HasWorkerRequirementsLayout();
    var hasPythonOverride = HasValidPythonOverride();
    return BuildSetupReadiness(workerOk, whisperOk, selectedModelCached, diarizationOk, setupScriptAvailable, workerAssetsPresent, hasPythonOverride);
}
private string BuildSetupReadiness(bool workerOk, bool whisperOk, bool selectedModelCached, bool diarizationOk, bool setupScriptAvailable, bool workerAssetsPresent, bool hasPythonOverride)
{
    var lines = new List<string>();
    if (!workerAssetsPresent)
    {
        lines.Add("This install is missing worker runtime files. Reinstall Muesli.");
    }
    else if (workerOk && whisperOk)
    {
        lines.Add("Core transcription is ready.");
    }
    else if (hasPythonOverride)
    {
        lines.Add("MUESLI_PYTHON override is active. Fix that Python environment or clear the override to use the built-in setup flow.");
    }
    else if (setupScriptAvailable)
    {
        lines.Add("Local transcription setup still needs attention. Use Install local transcription runtime.");
    }
    else
    {
        lines.Add("Local transcription setup still needs attention, and the setup script is missing from this build.");
    }

    lines.Add(selectedModelCached
        ? $"Whisper {SelectedModelProfile} is already cached for offline use."
        : $"Download Whisper {SelectedModelProfile} before relying on offline transcription.");

    if (!diarizationOk)
    {
        lines.Add("Speaker diarization is optional and still needs its extra runtime setup.");
    }

    return string.Join(Environment.NewLine, lines);
}
private async Task InstallLocalRuntimeAsync(Action<string>? onStatus = null, Action? onAfterRefresh = null)
{
    if (_isInstallingLocalRuntime)
    {
        return;
    }

    if (!CanInstallLocalRuntime)
    {
        var message = string.IsNullOrWhiteSpace(RuntimeSetupStatus)
            ? "Local transcription runtime setup is not available."
            : RuntimeSetupStatus;
        onStatus?.Invoke(message);
        return;
    }

    try
    {
        _isInstallingLocalRuntime = true;
        RuntimeSetupStatus = "Preparing local transcription runtime...";
        onStatus?.Invoke(RuntimeSetupStatus);
        DictationStatus = "Installing local transcription runtime";
        _toastNotificationService.Show("Installing runtime", "Preparing local transcription runtime", ToastState.Transcribing, 0);
        _logService.Info("Starting local transcription runtime setup from onboarding.");

        var result = await _runtimeDiagnosticsService.InstallLocalRuntimeAsync(progress =>
        {
            var message = MapRuntimeSetupProgress(progress);
            if (message is null)
            {
                return;
            }

            RuntimeSetupStatus = message;
            onStatus?.Invoke(message);
        });

        if (result.Detail.Length > 0)
        {
            _logService.Info($"Runtime setup output:{Environment.NewLine}{result.Detail}");
        }

        await RefreshRuntimeDiagnosticsAsync();
        onAfterRefresh?.Invoke();

        if (result.Success)
        {
            RuntimeSetupStatus = "Local transcription runtime installed. Check setup is now passing.";
            onStatus?.Invoke(RuntimeSetupStatus);
            DictationStatus = "Local transcription runtime installed";
            _toastNotificationService.Show("Runtime ready", "Local transcription runtime installed", ToastState.Success, 3600);
        }
        else
        {
            RuntimeSetupStatus = "Runtime setup failed. Open logs for details.";
            onStatus?.Invoke(RuntimeSetupStatus);
            DictationStatus = "Runtime setup failed";
            _toastNotificationService.Show("Runtime setup failed", result.Summary, ToastState.Error, 5200);
        }
    }
    catch (Exception exception)
    {
        RuntimeSetupStatus = "Runtime setup failed. Open logs for details.";
        onStatus?.Invoke(RuntimeSetupStatus);
        DictationStatus = $"Runtime setup failed: {exception.Message}";
        _logService.Error("Runtime setup failed.", exception);
        _toastNotificationService.Show("Runtime setup failed", "Open logs for details", ToastState.Error, 5200);
    }
    finally
    {
        _isInstallingLocalRuntime = false;
        onAfterRefresh?.Invoke();
    }
}
private static bool HasReadySignal(string status)
{
    if (string.IsNullOrWhiteSpace(status))
    {
        return false;
    }

    return status.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
           status.Contains("ready", StringComparison.OrdinalIgnoreCase) ||
           status.Contains("installed", StringComparison.OrdinalIgnoreCase);
}
private static string? MapRuntimeSetupProgress(string rawLine)
{
    if (string.IsNullOrWhiteSpace(rawLine))
    {
        return null;
    }

    var line = rawLine.Trim();
    if (line.Contains("Supported Python launcher found", StringComparison.OrdinalIgnoreCase))
    {
        return "Found a supported Python runtime.";
    }

    if (line.Contains("Collecting", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Downloading", StringComparison.OrdinalIgnoreCase))
    {
        return "Downloading Python packages...";
    }

    if (line.Contains("Installing collected packages", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase))
    {
        return "Installing transcription dependencies...";
    }

    if (line.Contains("Requirement already satisfied", StringComparison.OrdinalIgnoreCase))
    {
        return "Checking installed transcription dependencies...";
    }

    if (line.Contains("Muesli worker runtime is ready", StringComparison.OrdinalIgnoreCase))
    {
        return "Local transcription runtime is ready.";
    }

    return null;
}
private static bool HasValidPythonOverride()
{
    var path = Environment.GetEnvironmentVariable("MUESLI_PYTHON");
    return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
private MuesliSettings CurrentSettingsSnapshot()
{
    return new MuesliSettings
    {
        Hotkey = SelectedHotkey,
        UserName = UserName,
        AsrEngine = SelectedAsrEngine,
        ModelProfile = SelectedModelProfile,
        DictationLanguage = SelectedDictationLanguage,
        MeetingAsrEngine = SelectedMeetingAsrEngine,
        MeetingModelProfile = SelectedMeetingModelProfile,
        MeetingMicrophoneName = SelectedMeetingMicrophone,
        PasteBehavior = SelectedPasteBehavior,
        OnboardingCompleted = _onboardingCompleted,
        PostProcessingEnabled = PostProcessingEnabled,
        RemoveFillerWords = RemoveFillerWords,
        EnableDoubleTapDictation = EnableDoubleTapDictation,
        PostProcessingPrompt = PostProcessingPrompt,
        StartAtLogin = StartAtLogin,
        AutoMeetingDetectionEnabled = AutoMeetingDetectionEnabled,
        MeetingSummaryProvider = SelectedSummaryProvider,
        MeetingSummaryTemplate = SelectedSummaryTemplate,
        MeetingSummaryPromptOverride = CustomMeetingTemplates.FirstOrDefault(template =>
            template.Name.Equals(SelectedSummaryTemplate, StringComparison.OrdinalIgnoreCase))?.Prompt ?? "",
        OpenDashboardOnLaunch = OpenDashboardOnLaunch,
        SaveMeetingRecordings = SaveMeetingRecordings,
        RecordingSavePolicy = RecordingSavePolicy,
        AutoExportMeetings = AutoExportMeetings,
        AutoExportDirectory = AutoExportDirectory,
        ShowFloatingIndicator = ShowFloatingIndicator,
        IndicatorAnchor = SelectedIndicatorPosition,
        OpenAIApiKey = OpenAIApiKey,
        OpenAIModel = OpenAIModel,
        OpenRouterApiKey = OpenRouterApiKey,
        OpenRouterModel = OpenRouterModel,
        CustomSummaryEndpoint = CustomSummaryEndpoint,
        CustomSummaryModel = CustomSummaryModel,
        CustomSummaryApiKey = CustomSummaryApiKey,
        Theme = _theme,
        MicrophoneName = SelectedMicrophone,
        IndicatorLeft = _indicatorLeft,
        IndicatorTop = _indicatorTop,
        CrashReportingEnabled = _crashReportingEnabled,
        CrashReportingPromptShown = _crashReportingPromptShown
    };
}

public bool CrashReportingEnabled
{
    get => _crashReportingEnabled;
    set
    {
        if (SetField(ref _crashReportingEnabled, value))
        {
            OnPropertyChanged(nameof(CrashReportingRestartHintVisible));
            SaveSettings();
        }
    }
}

public System.Windows.Visibility CrashReportingRestartHintVisible =>
    _crashReportingEnabled != _crashReportingStartupValue
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;
private void OnIndicatorPositionChanged(object? sender, IndicatorPositionChangedEventArgs e)
{
    _indicatorLeft = e.Left;
    _indicatorTop = e.Top;
    _selectedIndicatorPosition = "Custom";
    OnPropertyChanged(nameof(SelectedIndicatorPosition));
    _toastNotificationService.SetIndicatorAnchor("Custom", clearCustomPosition: false);
    SaveSettings();
}
private void ApplyTheme(string theme)
{
    var light = theme.Equals("light", StringComparison.OrdinalIgnoreCase);
    SetColor("BackgroundDeep", light ? "#F5F5F7" : "#111214");
    SetColor("BackgroundBase", light ? "#FFFFFF" : "#161719");
    SetColor("BackgroundRaised", light ? "#F0F0F2" : "#1C1D20");
    SetColor("BackgroundHover", light ? "#E8E8EC" : "#232528");
    SetColor("SurfacePrimary", light ? "#E5E5EA" : "#262830");
    SetColor("SurfaceSelected", light ? "#D6DFFE" : "#2E3340");
    SetColor("AccentBlue", light ? "#2563EB" : "#6BA3F7");
    SetColor("TextPrimary", light ? "#E0000000" : "#EBFFFFFF");
    SetColor("TextSecondary", light ? "#A6000000" : "#9EFFFFFF");
    SetColor("TextTertiary", light ? "#73000000" : "#66FFFFFF");
    SetBrush("BorderBrushSoft", light ? "#14000000" : "#12FFFFFF");
    SetBrush("BorderBrushMedium", light ? "#1F000000" : "#1CFFFFFF");
    SetBrush("PrimaryButtonBackgroundBrush", light ? "#D6DFFE" : "#26364F");
    SetBrush("PrimaryButtonBorderBrush", light ? "#9BB7F5" : "#3C5D8E");
    SetBrush("AccentBadgeBackgroundBrush", light ? "#D6E4FF" : "#226BA3F7");
    SetBrush("SuccessBadgeBackgroundBrush", light ? "#D4F5E0" : "#24342E");
    if (System.Windows.Application.Current?.MainWindow is not null)
    {
        System.Windows.Application.Current.MainWindow.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BackgroundDeepBrush"];
    }
    UpdateThemeToggleVisuals(light);
}
private void UpdateThemeToggleVisuals(bool light)
{
    var selected = (System.Windows.Media.Brush)FindResource("SurfaceSelectedBrush");
    var clear = System.Windows.Media.Brushes.Transparent;
    var accent = (System.Windows.Media.Brush)FindResource("AccentBlueBrush");
    var tertiary = (System.Windows.Media.Brush)FindResource("TextTertiaryBrush");
    LightThemeButton.Background = light ? selected : clear;
    LightThemeButton.Foreground = light ? accent : tertiary;
    DarkThemeButton.Background = light ? clear : selected;
    DarkThemeButton.Foreground = light ? tertiary : accent;
}
private static void SetColor(string key, string value)
{
    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
    System.Windows.Application.Current.Resources[key] = color;
    var brushKey = $"{key}Brush";
    SetBrush(brushKey, color);
}
private static void SetBrush(string key, string value)
{
    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
    SetBrush(key, color);
}
private static void SetBrush(string key, System.Windows.Media.Color color)
{
    if (System.Windows.Application.Current.Resources[key] is SolidColorBrush existing)
    {
        if (existing.IsFrozen)
        {
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
            return;
        }
        existing.Color = color;
        return;
    }
    System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
}
private void OnMeetingDetected(object? sender, DetectedMeeting meeting)
{
    if (!AutoMeetingDetectionEnabled)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but AutoDetection is disabled.");
        return;
    }
    if (_isMeetingRecording)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but already recording.");
        return;
    }
    if (_meetingRecordingCoordinator.IsBusy)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but coordinator is busy.");
        return;
    }
    if (_meetingPromptCooldownUntil > DateTime.Now
        && string.Equals(_meetingPromptCooldownPlatform, meeting.Platform, StringComparison.OrdinalIgnoreCase))
    {
        var remaining = (_meetingPromptCooldownUntil - DateTime.Now).TotalSeconds;
        _logService.Info($"Meeting detected ({meeting.Platform}) but post-meeting prompts are paused for {remaining:F0}s more.");
        return;
    }
    if (_meetingPromptCooldownUntil <= DateTime.Now)
    {
        _meetingPromptCooldownPlatform = null;
    }
    if (_meetingPromptService.IsVisible)
    {
        _logService.Info($"Meeting detected ({meeting.Platform}) but prompt is already visible — resetting.");
        _meetingPromptService.Reset();
        return;
    }
    if (_ignoredMeetingPrompts.TryGetValue(meeting.Key, out var ignoredUntil) && ignoredUntil > DateTime.Now)
    {
        var remaining = (ignoredUntil - DateTime.Now).TotalSeconds;
        _logService.Info($"Meeting detected ({meeting.Platform}) but prompt ignored for {remaining:F0}s more.");
        return;
    }

    _meetingPromptService.Show(
        meeting,
        () => Dispatcher.InvokeAsync(() => ToggleMeetingRecordingAsync(meeting.Title, meeting.ProcessId, meeting.Key)),
        () =>
        {
            _ignoredMeetingPrompts[meeting.Key] = DateTime.Now.AddMinutes(30);
            DictationStatus = "Meeting prompt ignored";
        });
}

private void OnMeetingCaptureWarning(object? sender, string warning)
{
    Dispatcher.BeginInvoke(() =>
    {
        DictationStatus = "Recording meeting with safety fallback";
        _toastNotificationService.Show(
            "Recording with safety fallback",
            "Zoom's isolated audio was silent, so Muesli switched to all system audio.",
            ToastState.Recording,
            6500);
    });
}
    private void OnMeetingDetectionScanCompleted(object? sender, MeetingDetectionScan scan)
    {
        Dispatcher.Invoke(() =>
        {
            MeetingDetectionStatus = scan.Found
                ? scan.Summary
                : $"No meeting found. {scan.Summary}";
        });
    }

    private void CheckMeetingDetection_Click(object sender, RoutedEventArgs e)
    {
        // One-shot scan; do not toggle AutoMeetingDetectionEnabled — the user
        // controls that via the checkbox above this button.
        var scan = _meetingDetectionService.CheckNow(publish: AutoMeetingDetectionEnabled);
        MeetingDetectionStatus = scan.Found
            ? scan.Summary
            : $"No meeting found. {scan.Summary}";
        DictationStatus = scan.Found ? "Meeting detected" : "No meeting detected";
    }

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
                dictation.DurationMs,
                dictation.DeliveryMode));
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
                meeting.RawTranscript,
                meeting.ManualNotes,
                meeting.Status,
                meeting.Origin,
                meeting.TranscriptSegments,
                ResolveDiarizationStatus(meeting)));
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

    private void SaveDictations()
    {
        _dataStore.SaveDictations(Dictations.Select(item => new PersistedDictation(
            item.Id,
            item.Timestamp,
            item.Text,
            item.DurationMs,
            item.ModelProfile,
            item.DeliveryMode)));
    }

    private void SaveMeetings()
    {
        _dataStore.SaveMeetings(Meetings.Select(item => new PersistedMeeting
        {
            Id = item.Id,
            Title = item.Title,
            CreatedAt = item.CreatedAt,
            DurationMs = item.DurationMs,
            Transcript = item.Transcript,
            RawTranscript = item.RawTranscript,
            Summary = item.Summary,
            ManualNotes = item.ManualNotes,
            SourcePath = item.SourcePath,
            ModelProfile = item.ModelProfile,
            FolderId = item.FolderId,
            WordCount = item.WordCount,
            TemplateName = item.TemplateName,
            SpeakerAliases = item.SpeakerAliases ?? new Dictionary<string, string>(),
            HealthWarnings = MeetingRecordingCoordinator.CleanupHealthWarnings(item.HealthWarnings, item.Transcript),
            TranscriptSegments = item.TranscriptSegments ?? [],
            DiarizationStatus = item.DiarizationStatus,
            Status = item.Status,
            Origin = item.Origin
        }));
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        return text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string ResolveDiarizationStatus(PersistedMeeting meeting)
    {
        if (!string.IsNullOrWhiteSpace(meeting.DiarizationStatus)
            && !meeting.DiarizationStatus.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return meeting.DiarizationStatus;
        }

        if (meeting.Origin.Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            return "not-applicable";
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
            meeting.Transcript,
            @"\[\d{2}:\d{2}:\d{2}\]\s+Speaker \d+:",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            return "identified";
        }

        if (meeting.Transcript.Contains("[System audio]", StringComparison.OrdinalIgnoreCase)
            || meeting.HealthWarnings.Any(warning =>
                warning.Contains("diarization", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("speaker identification", StringComparison.OrdinalIgnoreCase)))
        {
            return "fallback";
        }

        return "unknown";
    }

    private static string WorkerLanguageHint(string selectedLanguage) =>
        selectedLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : selectedLanguage;

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
        if (string.IsNullOrWhiteSpace(transcript) || aliases.Count == 0)
            return transcript;

        var result = transcript;
        foreach (var pair in aliases.OrderByDescending(p => p.Key.Length))
        {
            if (!string.IsNullOrWhiteSpace(pair.Value) && pair.Key != pair.Value)
            {
                result = result.Replace(pair.Key, pair.Value);
            }
        }

        return result;
    }

    private static string ApplySpeakerAliasesToNotes(string notes, Dictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(notes) || aliases.Count == 0)
            return notes;

        var result = notes;
        foreach (var pair in aliases.OrderByDescending(p => p.Key.Length))
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Key == pair.Value)
                continue;

            var escaped = System.Text.RegularExpressions.Regex.Escape(pair.Key);
            var pattern = new System.Text.RegularExpressions.Regex($@"(?<!\w){escaped}(?!\w)");
            result = pattern.Replace(result, pair.Value);
        }

        return result;
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

public sealed record DictationItem(
    string Id,
    DateTime Timestamp,
    string Time,
    string Text,
    string ModelProfile,
    int DurationMs,
    string DeliveryMode = "active-app")
{
    private DateTime LocalTimestamp => Timestamp.Kind == DateTimeKind.Utc ? Timestamp.ToLocalTime() : Timestamp;

    public string DateGroupLabel => LocalTimestamp.Date switch
    {
        var date when date == DateTime.Today => "TODAY",
        var date when date == DateTime.Today.AddDays(-1) => "YESTERDAY",
        _ => LocalTimestamp.ToString("MMMM d, yyyy")
    };
}

public sealed record MeetingItem(
    string Id,
    string Title,
    DateTime CreatedAt,
    string Transcript,
    string Summary,
    string SourcePath,
    string ModelProfile,
    int DurationMs,
    string? FolderId,
    int WordCount = 0,
    string TemplateName = "",
    Dictionary<string, string>? SpeakerAliases = null,
    List<string>? HealthWarnings = null,
    string RawTranscript = "",
    string ManualNotes = "",
    string Status = "completed",
    string Origin = "recording",
    List<MeetingTranscriptSegment>? TranscriptSegments = null,
    string DiarizationStatus = "unknown")
{
    public string Metadata => $"{CreatedAt:yyyy-MM-dd HH:mm} • {DurationLabel}" +
                              (Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? "" : $" • {Status}");

    public string SpeakerIdentificationLabel => DiarizationStatus switch
    {
        "identified" => "Speakers identified",
        "fallback" => "Chronological speaker fallback",
        "not-applicable" => "Single audio track",
        _ => "Speaker status unavailable"
    };

    public string DurationLabel
    {
        get
        {
            var seconds = Math.Max(0, (int)Math.Round(DurationMs / 1000.0));
            if (seconds >= 3600)
            {
                return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
            }

            if (seconds >= 60)
            {
                var minutes = seconds / 60;
                var remainingSeconds = seconds % 60;
                return remainingSeconds == 0 ? $"{minutes}m" : $"{minutes}m {remainingSeconds}s";
            }

            return $"{seconds}s";
        }
    }

    public string PreviewText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(Summary) ? Transcript : Summary;
            if (string.IsNullOrWhiteSpace(text))
                return "";
            text = text.Trim();
            return text.Length > 200 ? text[..200] + "…" : text;
        }
    }
}

public sealed class MeetingFolderItem : INotifyPropertyChanged
{
    private int _count;
    private bool _isRenaming;
    private string _name;

    public MeetingFolderItem(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountDisplay)));
        }
    }

    public string CountDisplay => Count < 1000 ? Count.ToString() : Count < 10000 ? $"{Count / 1000.0:0.0}k" : $"{Count / 1000}k";

    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value)
            {
                return;
            }

            _isRenaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRenaming)));
        }
    }
}

public sealed record MoveMeetingRequest(string MeetingId, string? FolderId);

public sealed class MeetingTemplateItem
{
    public MeetingTemplateItem(PersistedMeetingTemplate record)
    {
        Id = string.IsNullOrWhiteSpace(record.Id) ? $"template_{Guid.NewGuid():N}" : record.Id;
        Name = record.Name;
        Prompt = record.Prompt;
        Icon = string.IsNullOrWhiteSpace(record.Icon) ? "square.and.pencil" : record.Icon;
    }

    public MeetingTemplateItem(string name, string prompt, string icon = "square.and.pencil")
    {
        Id = $"template_{Guid.NewGuid():N}";
        Name = name;
        Prompt = prompt;
        Icon = icon;
    }

    public string Id { get; }
    public string Name { get; set; }
    public string Prompt { get; set; }
    public string Icon { get; set; }

    public PersistedMeetingTemplate Record => new()
    {
        Id = Id,
        Name = Name,
        Prompt = Prompt,
        Icon = Icon
    };
}

public sealed class DictionaryEntryItem : INotifyPropertyChanged
{
    private string _phrase;
    private string _replacement;
    private double _matchingThreshold;

    public DictionaryEntryItem(DictionaryEntryRecord record)
        : this(record.Id, record.Phrase, record.Replacement, record.MatchingThreshold)
    {
    }

    public DictionaryEntryItem(string id, string phrase, string replacement, double matchingThreshold)
    {
        Id = id;
        _phrase = phrase;
        _replacement = replacement;
        _matchingThreshold = matchingThreshold <= 0 ? 0.85 : matchingThreshold;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Phrase
    {
        get => _phrase;
        set
        {
            if (_phrase == value)
            {
                return;
            }

            _phrase = value;
            NotifyDictionaryChanged();
        }
    }

    public string Replacement
    {
        get => _replacement;
        set
        {
            if (_replacement == value)
            {
                return;
            }

            _replacement = value;
            NotifyDictionaryChanged();
        }
    }

    public double MatchingThreshold
    {
        get => _matchingThreshold;
        set
        {
            var next = Math.Round(Math.Clamp(value, 0.70, 0.95), 2);
            if (Math.Abs(_matchingThreshold - next) < 0.001)
            {
                return;
            }

            _matchingThreshold = next;
            NotifyDictionaryChanged();
        }
    }

    public string ThresholdDisplay => MatchingThreshold.ToString("0.00");
    public string Display => $"{Phrase} → {Replacement}";

    public DictionaryEntryRecord Record => new()
    {
        Id = Id,
        Phrase = Phrase,
        Replacement = Replacement,
        MatchingThreshold = MatchingThreshold
    };

    private void NotifyDictionaryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Record)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThresholdDisplay)));
    }
}
