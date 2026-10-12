using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.WinUI.ViewModels;

public sealed record SettingsPermissionItem(
    string Name,
    string Status,
    bool Granted,
    string Help,
    bool CanOpenSystemSettings,
    bool IsLast)
{
    public bool ShowGrantAction => !Granted && CanOpenSystemSettings;
    public bool ShowGrantedLabel => Granted;
    public bool ShowUnavailableLabel => !Granted && !CanOpenSystemSettings;
    public string DisplayStatus => Granted ? "Granted" : Status;
    public string GrantActionName => $"Grant {Name} access in Windows Settings";
}

public partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly WinUiSettingsContext _settingsContext;
    private readonly WinUiLibraryContext _library;
    private readonly IStartupRegistrationService _startup;
    private readonly IAppDialogService _dialogs;
    private readonly WinUiComputerUseContext _computerUse;
    private readonly WinUiMeetingContext _meetings;
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiMeetingDetectionService _detection;
    private readonly Action<bool> _applyDetection;
    private readonly WindowsMicrophoneAccessService _microphone = new();
    private MuesliSettings _source = new();

    public SettingsPageViewModel(
        WinUiSettingsContext settingsContext,
        WinUiLibraryContext library,
        IStartupRegistrationService startup,
        IAppDialogService dialogs,
        WinUiComputerUseContext computerUse,
        WinUiMeetingContext meetings,
        WinUiDictationContext dictation,
        WinUiMeetingDetectionService detection,
        Action<bool> applyDetection)
    {
        _settingsContext = settingsContext;
        _library = library;
        _startup = startup;
        _dialogs = dialogs;
        _computerUse = computerUse;
        _meetings = meetings;
        _dictation = dictation;
        _detection = detection;
        _applyDetection = applyDetection;
        _computerUse.Changed += OnComputerUseChanged;
        _meetings.Changed += OnMeetingsChanged;
    }

    private void OnComputerUseChanged(object? sender, EventArgs args) =>
        ComputerUseSignedIn = _computerUse.ChatGpt.IsSignedIn;

    private void OnMeetingsChanged(object? sender, EventArgs args)
    {
        CaptureStatus = _meetings.CaptureStatus;
        DetectionStatus = _detection.Status;
    }

    public void Dispose()
    {
        _computerUse.Changed -= OnComputerUseChanged;
        _meetings.Changed -= OnMeetingsChanged;
    }

    [ObservableProperty] public partial int SectionIndex { get; set; }
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial bool OpenDashboardOnLaunch { get; set; }
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    [NotifyPropertyChangedFor(nameof(IsStartupUnavailable))]
    [ObservableProperty] public partial bool IsStartupAvailable { get; private set; } = true;
    [ObservableProperty] public partial string StartupAvailabilityNotice { get; private set; } = "";
    [ObservableProperty] public partial bool AutoMeetingDetectionEnabled { get; set; }
    [ObservableProperty] public partial bool SaveMeetingRecordings { get; set; }
    [ObservableProperty] public partial bool AutoExportMarkdownEnabled { get; set; }
    [ObservableProperty] public partial string AutoExportMarkdownDirectory { get; set; } = "";
    [ObservableProperty] public partial bool AutoExportPdfEnabled { get; set; }
    [ObservableProperty] public partial bool PostMeetingHookEnabled { get; set; }
    [ObservableProperty] public partial string PostMeetingHookExecutablePath { get; set; } = "";
    [ObservableProperty] public partial int PostMeetingHookTimeoutSeconds { get; set; } = 30;
    [ObservableProperty] public partial string MeetingSummaryTemplate { get; set; } = "Standard Meeting Notes";
    [ObservableProperty] public partial IReadOnlyList<string> MeetingTemplates { get; private set; } = [];
    [ObservableProperty] public partial bool RemoveFillerWords { get; set; }
    [NotifyPropertyChangedFor(nameof(ShowCleanupPreset))]
    [ObservableProperty] public partial bool EnableLocalCleanup { get; set; }
    [NotifyPropertyChangedFor(nameof(CleanupSourceHelp), nameof(ShowCleanupPreset))]
    [ObservableProperty] public partial int CleanupSourceIndex { get; set; }
    [NotifyPropertyChangedFor(nameof(CleanupPresetPreview))]
    [ObservableProperty] public partial int CleanupPresetIndex { get; set; }
    private IReadOnlyList<CleanupPromptPreset> _cleanupPresets = CleanupPrompts.BuiltIns;
    public IReadOnlyList<string> CleanupPresetNames => _cleanupPresets.Select(preset => preset.Name).ToList();
    public string CleanupPresetPreview => _cleanupPresets[Math.Clamp(CleanupPresetIndex, 0, _cleanupPresets.Count - 1)].Prompt;
    /// <summary>Presets shape the prompt sent to a cloud source; the local model keeps its own.</summary>
    public bool ShowCleanupPreset => EnableLocalCleanup && CleanupSourceIndex > 0;
    [ObservableProperty] public partial bool EnableAppContext { get; set; }
    private static readonly string[] CleanupSources = ["local", "chatgpt", "openai", "openrouter", "ollama", "lmstudio", "custom"];

    /// <summary>Says where the dictation goes, so a cloud choice is never a surprise.</summary>
    public string CleanupSourceHelp => CleanupSourceIndex switch
    {
        <= 0 => "Runs the cleanup model from Models on this PC. Nothing leaves your computer.",
        1 => "Sends each dictation to your signed-in ChatGPT account. Adds a moment before pasting.",
        4 or 5 => "Sends each dictation to the local server set under Meetings > Meeting Summaries.",
        _ => "Sends each dictation to this provider, using the key and model set under Meetings > Meeting Summaries."
    };
    [ObservableProperty] public partial bool ComputerUseEnabled { get; set; }
    [ObservableProperty] public partial bool ShowFloatingIndicator { get; set; }
    [ObservableProperty] public partial bool SoundEnabled { get; set; }
    [ObservableProperty] public partial bool ShowLiveWaveformOnHover { get; set; }
    [ObservableProperty] public partial int HotkeyTriggerThresholdMs { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; } = "F8";
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial int RecordingColorIndex { get; set; }
    [ObservableProperty] public partial int IndicatorStyleIndex { get; set; }
    [ObservableProperty] public partial int IndicatorPositionIndex { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> IndicatorPositions { get; private set; } = IndicatorAnchors;
    [ObservableProperty] public partial int AutoExportContentIndex { get; set; }
    [ObservableProperty] public partial int PasteShortcutIndex { get; set; }
    [ObservableProperty] public partial bool PauseMediaDuringDictation { get; set; }
    [ObservableProperty] public partial bool MuteAudioDuringDictation { get; set; }
    [ObservableProperty] public partial int RecordingFormatIndex { get; set; }
    private static readonly string[] PasteShortcuts = ["ctrl-v", "ctrl-shift-v", "shift-insert"];
    [ObservableProperty] public partial int MeetingSummaryRetryCount { get; set; } = MeetingSummaryRetryPolicy.DefaultRetryCount;

    /// <summary>macOS indicator_position choices; "Custom" appears only after a drag.</summary>
    public static readonly string[] IndicatorAnchors =
        ["Top Left", "Top Center", "Top Right", "Middle Left", "Middle Right", "Bottom Left", "Bottom Center", "Bottom Right"];
    private const string CustomIndicatorPosition = "Custom (drag to reposition)";
    private static readonly string[] AutoExportContents = ["notes", "transcript", "full-meeting"];

    /// <summary>
    /// The recording pill's accent presets, in macOS order. "1e1e2e" is the shared stored value for
    /// the default accent; a hex outside this list (index -1) is preserved on save.
    /// </summary>
    public static readonly (string Hex, string Name)[] RecordingColors =
    [
        ("1e1e2e", "Default"), ("2563eb", "Blue"), ("ef4444", "Red"), ("f59e0b", "Amber"),
        ("10b981", "Green"), ("8b5cf6", "Purple"), ("ec4899", "Pink")
    ];
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<SettingsPermissionItem> Permissions { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<string> Microphones { get; private set; } = [];
    [ObservableProperty] public partial string? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> DictationModels { get; private set; } = [];
    [ObservableProperty] public partial string SelectedDictationModel { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> MeetingModels { get; private set; } = [];
    [ObservableProperty] public partial string SelectedMeetingModel { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> LiveModels { get; private set; } = [];
    [ObservableProperty] public partial string SelectedLiveModel { get; set; } = "Off";
    [ObservableProperty] public partial int LiveOwnershipIndex { get; set; }
    /// <summary>Index into <see cref="ComputerUseConfiguration.PlannerModels"/>.</summary>
    [ObservableProperty] public partial int ComputerUseModelIndex { get; set; }
    public IReadOnlyList<string> ComputerUseModels { get; } = ComputerUseConfiguration.PlannerModels.Select(model => model.Label).ToList();
    /// <summary>The thinking efforts the selected model supports, as labels.</summary>
    [ObservableProperty] public partial IReadOnlyList<string> ComputerUseEfforts { get; private set; } = [];
    [ObservableProperty] public partial int ComputerUseEffortIndex { get; set; }
    [ObservableProperty] public partial int ComputerUseTimeoutSeconds { get; set; } = 120;
    private string _computerUseEffort = "";
    [NotifyPropertyChangedFor(nameof(ComputerUseSignInLabel))]
    [ObservableProperty] public partial bool ComputerUseSignedIn { get; private set; }
    public string ComputerUseSignInLabel => ComputerUseSignedIn ? "Sign out of ChatGPT" : "Sign in with ChatGPT";
    public string ComputerUseNotice => ComputerUseConfiguration.Notice;
    [ObservableProperty] public partial string CaptureStatus { get; private set; } = "Idle";
    [ObservableProperty] public partial string DetectionStatus { get; private set; } = "";
    [NotifyPropertyChangedFor(nameof(ShowOllamaSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenAISettings))]
    [NotifyPropertyChangedFor(nameof(ShowChatGptSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenRouterSettings))]
    [NotifyPropertyChangedFor(nameof(ShowLmStudioSettings))]
    [NotifyPropertyChangedFor(nameof(ShowCustomHttpSettings))]
    [NotifyPropertyChangedFor(nameof(SummaryDisclosure))]
    [ObservableProperty] public partial SummaryProviderInfo? SelectedSummaryProvider { get; set; } = SummaryProviderDisclosure.For("local");
    [NotifyPropertyChangedFor(nameof(SummaryDisclosure))]
    [ObservableProperty] public partial string OllamaEndpoint { get; set; } = "http://localhost:11434";
    [ObservableProperty] public partial string OllamaModel { get; set; } = "llama3.1:8b";
    [NotifyPropertyChangedFor(nameof(SummaryDisclosure))]
    [ObservableProperty] public partial string LmStudioEndpoint { get; set; } = "http://localhost:1234";
    [ObservableProperty] public partial string LmStudioModel { get; set; } = "";
    [NotifyPropertyChangedFor(nameof(SummaryDisclosure))]
    [ObservableProperty] public partial string CustomLlmEndpoint { get; set; } = "http://localhost:8080/v1/chat/completions";
    [ObservableProperty] public partial string CustomLlmModel { get; set; } = "";
    [ObservableProperty] public partial string CustomLlmApiKey { get; set; } = "";
    [ObservableProperty] public partial string OpenAIModel { get; set; } = "";
    [ObservableProperty] public partial string OpenRouterModel { get; set; } = "";
    [ObservableProperty] public partial string OpenAIApiKey { get; set; } = "";
    [ObservableProperty] public partial string OpenRouterApiKey { get; set; } = "";

    /// <summary>True while the startup toggle is disabled, so the reason can be shown beside it.</summary>
    public bool IsStartupUnavailable => !IsStartupAvailable;

    public IReadOnlyList<SummaryProviderInfo> SummaryProviders { get; } = SummaryProviderDisclosure.Available;

    public bool ShowOllamaSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.Ollama;
    public bool ShowChatGptSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.ChatGpt;
    public bool ShowOpenAISettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.OpenAI;
    public bool ShowOpenRouterSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.OpenRouter;
    public bool ShowLmStudioSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.LmStudio;
    public bool ShowCustomHttpSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.CustomLlm;
    public string SummaryDisclosure =>
        SummaryProviderDisclosure.DisclosureFor(
            SelectedSummaryProvider?.Id,
            OllamaEndpoint,
            LmStudioEndpoint,
            CustomLlmEndpoint);

    // P5-04: the "Sync" tab held nothing but an unavailable-on-Windows notice. Its single honest
    // statement now sits in the General tab's DATA section, next to the permission row that already
    // reports "Calendar / iPhone sync - Not available"
    // (Muesli.Windows.Core/Services/WindowsPermissionStatus.cs:41-45).
    public bool ShowGeneral => SectionIndex == 0;
    public bool ShowDictation => SectionIndex == 1;
    public bool ShowComputerUse => SectionIndex == 2;
    public bool ShowMeetings => SectionIndex == 3;
    public bool ShowAppearance => SectionIndex == 4;

    /// <summary>
    /// P5-11. Says what the field is for and whether it is currently usable, instead of leaving a
    /// blank box. An empty directory makes the exporter fail with "Select a rooted auto-export
    /// directory." (Muesli.Windows.Platform/Services/PostMeetingAutomationService.cs:423-424).
    /// </summary>
    /// <summary>
    /// PDF auto-export is real but release-gated (EXP-01). The control stays disabled until the
    /// QuestPDF Community-license decision is recorded, so the UI never promises a PDF it cannot
    /// write.
    /// </summary>
    public bool PdfAutoExportAvailable => MeetingDocumentWriter.PdfExportApproved;
    public string PdfAutoExportHelp => PdfAutoExportAvailable
        ? "Save a PDF copy beside the Markdown file. Existing edited files are kept."
        : "Unavailable until QuestPDF Community-license eligibility is approved (EXP-01).";

    public string AutoExportFolderHelp =>
        !AutoExportMarkdownEnabled
            ? "Turn on automatic export to choose where the Markdown copy is written."
            : string.IsNullOrWhiteSpace(AutoExportMarkdownDirectory)
                ? "Nothing is exported until you choose a folder on this PC."
                : "A Markdown copy of each completed meeting is written here.";

    /// <summary>
    /// P5-09. Settings commit only on Save, so an uncommitted edit has to be visible from every tab
    /// rather than being dropped silently when the section changes.
    /// </summary>
    [ObservableProperty] public partial bool HasUnsavedChanges { get; private set; }

    /// <summary>Clears the dirty flag and starts tracking edits. Called after the initial load.</summary>
    public void MarkSaved()
    {
        HasUnsavedChanges = false;
        _trackEdits = true;
    }

    public void ReportFolderPickerFailure(string message) =>
        ShowStatus($"The folder could not be chosen: {message}", isError: true);

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_trackEdits && e.PropertyName is { } name && !NonEditableProperties.Contains(name))
        {
            HasUnsavedChanges = true;
        }
    }

    /// <summary>
    /// Status, catalogue and derived properties: changing one of these is not a user edit.
    /// Everything else on this view model is a settings field that Save writes.
    /// </summary>
    private static readonly HashSet<string> NonEditableProperties =
    [
        nameof(SectionIndex), nameof(HasUnsavedChanges), nameof(StatusMessage), nameof(IsStatusOpen),
        nameof(IsStatusError), nameof(Permissions), nameof(Microphones), nameof(DictationModels),
        nameof(MeetingModels), nameof(LiveModels), nameof(MeetingTemplates), nameof(ComputerUseEfforts), nameof(IndicatorPositions),
        nameof(CaptureStatus), nameof(DetectionStatus), nameof(IsStartupAvailable),
        nameof(IsStartupUnavailable), nameof(StartupAvailabilityNotice), nameof(AutoExportFolderHelp), nameof(CleanupSourceHelp),
        nameof(CleanupPresetNames), nameof(CleanupPresetPreview), nameof(ShowCleanupPreset),
        nameof(SummaryDisclosure), nameof(ShowOllamaSettings), nameof(ShowOpenAISettings), nameof(ShowChatGptSettings),
        nameof(ShowOpenRouterSettings), nameof(ShowGeneral), nameof(ShowDictation),
        nameof(ShowComputerUse), nameof(ShowMeetings), nameof(ShowAppearance)
    ];

    private bool _trackEdits;

    public async Task LoadAsync()
    {
        _trackEdits = false;
        _source = _settingsContext.Load();
        UserName = _source.UserName;
        OpenDashboardOnLaunch = _source.OpenDashboardOnLaunch;
        try
        {
            StartAtLogin = await _startup.IsEnabledAsync();
            IsStartupAvailable = true;
            StartupAvailabilityNotice = "";
        }
        catch (Exception exception)
        {
            // The Windows startup task lives in the MSIX manifest, so it is only reachable
            // when the shell runs with package identity. Say so instead of leaving a dead toggle.
            StartAtLogin = false;
            IsStartupAvailable = false;
            StartupAvailabilityNotice =
                "Launch at sign-in is unavailable: this build is running without package identity, " +
                $"so the Windows startup task cannot be read ({exception.GetType().Name}). " +
                "Install the packaged build to manage it here.";
        }

        AutoMeetingDetectionEnabled = _source.AutoMeetingDetectionEnabled;
        SaveMeetingRecordings = _source.SaveMeetingRecordings;
        RecordingFormatIndex = _source.MeetingRecordingFormat == "m4a" ? 1 : 0;
        AutoExportMarkdownEnabled = _source.AutoExportMarkdownEnabled;
        AutoExportMarkdownDirectory = _source.AutoExportMarkdownDirectory;
        AutoExportPdfEnabled = _source.AutoExportPdfEnabled;
        PostMeetingHookEnabled = _source.PostMeetingHookEnabled;
        PostMeetingHookExecutablePath = _source.PostMeetingHookExecutablePath;
        PostMeetingHookTimeoutSeconds = _source.PostMeetingHookTimeoutSeconds;
        MeetingTemplates = MeetingSummaryService.BuiltInTemplateNames.Concat(_library.LoadMeetingTemplates().Select(item => item.Name)).Distinct().ToList();
        MeetingSummaryTemplate = MeetingSummaryService.NormalizeTemplateName(_source.MeetingSummaryTemplate);
        RemoveFillerWords = _source.RemoveFillerWords;
        PasteShortcutIndex = Math.Max(0, Array.IndexOf(PasteShortcuts, _source.PasteShortcut));
        PauseMediaDuringDictation = _source.PauseMediaDuringDictation;
        MuteAudioDuringDictation = _source.MuteAudioDuringDictation;
        MeetingSummaryRetryCount = MeetingSummaryRetryPolicy.Clamp(_source.MeetingSummaryRetryCount);
        EnableLocalCleanup = _source.EnableLocalCleanup;
        CleanupSourceIndex = Math.Max(0, Array.IndexOf(CleanupSources, _source.CleanupSource));
        _cleanupPresets = CleanupPrompts.All(_source.CustomCleanupPrompts);
        OnPropertyChanged(nameof(CleanupPresetNames));
        CleanupPresetIndex = Math.Max(0, _cleanupPresets.ToList().FindIndex(preset => preset.Id == _source.ActiveCleanupPromptId));
        EnableAppContext = _source.EnableAppContext;
        ComputerUseEnabled = _source.ComputerUseEnabled;
        ShowFloatingIndicator = _source.ShowFloatingIndicator;
        SoundEnabled = _source.SoundEnabled;
        ShowLiveWaveformOnHover = _source.ShowLiveWaveformOnHover;
        HotkeyTriggerThresholdMs = _source.HotkeyTriggerThresholdMs;
        Hotkey = _source.Hotkey;
        ThemeIndex = _source.Theme.Trim().ToLowerInvariant() switch { "light" => 1, "dark" => 2, _ => 0 };
        IndicatorStyleIndex = _source.IndicatorStyle == "minimal" ? 1 : 0;
        var anchor = Array.IndexOf(IndicatorAnchors, _source.IndicatorAnchor);
        IndicatorPositions = anchor < 0 ? [.. IndicatorAnchors, CustomIndicatorPosition] : IndicatorAnchors;
        IndicatorPositionIndex = anchor < 0 ? IndicatorAnchors.Length : anchor;
        AutoExportContentIndex = Math.Max(0, Array.IndexOf(AutoExportContents, _source.AutoExportMarkdownContent));
        RecordingColorIndex = Array.FindIndex(RecordingColors, color => color.Hex == _source.RecordingColorHex);
        Microphones = new[] { "Automatic" }.Concat(_dictation.ListMicrophones()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SelectedMicrophone = string.IsNullOrWhiteSpace(_source.MicrophoneName) ? "Automatic" : _source.MicrophoneName;
        // macOS lists only installed models; the Models page owns downloads. The saved selections
        // stay listed so the picker never shows blank (dictation then fails closed with guidance).
        DictationModels = TranscriptionModelCatalog.Models
            .Where(model => TranscriptionModelReadiness.IsVerified(model) ||
                model.Id == _source.DictationModelId || model.Id == _source.FinalMeetingModelId)
            .Select(model => model.DisplayName).ToList();
        MeetingModels = DictationModels;
        SelectedDictationModel = TranscriptionModelCatalog.Get(_source.DictationModelId).DisplayName;
        SelectedMeetingModel = TranscriptionModelCatalog.Get(_source.FinalMeetingModelId).DisplayName;
        LiveModels = new[] { "Off" }.Concat(StreamingModelCatalog.Models.Select(model => model.DisplayName)).ToList();
        SelectedLiveModel = string.IsNullOrWhiteSpace(_source.LiveMeetingModelId)
            ? "Off"
            : StreamingModelCatalog.Get(_source.LiveMeetingModelId)?.DisplayName ?? "Off";
        LiveOwnershipIndex = LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(_source.LiveTranscriptOwnership)
            == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? 1 : 0;
        _computerUseEffort = _source.ComputerUseReasoningEffort;
        ComputerUseModelIndex = IndexOfPlannerModel(_source.ComputerUsePlannerModel);
        ShowComputerUseEfforts();
        ComputerUseTimeoutSeconds = _source.ComputerUseTimeoutSeconds;
        ComputerUseSignedIn = _computerUse.ChatGpt.IsSignedIn;
        CaptureStatus = _meetings.CaptureStatus;
        DetectionStatus = _detection.Status;
        SelectedSummaryProvider = SummaryProviderDisclosure.For(_source.MeetingSummaryProvider);
        OllamaEndpoint = _source.OllamaEndpoint;
        OllamaModel = _source.OllamaModel;
        LmStudioEndpoint = _source.LmStudioEndpoint;
        LmStudioModel = _source.LmStudioModel;
        CustomLlmEndpoint = _source.CustomLlmEndpoint;
        CustomLlmModel = _source.CustomLlmModel;
        CustomLlmApiKey = _source.ResolvedCustomLlmApiKey;
        OpenAIModel = _source.OpenAIModel;
        OpenRouterModel = _source.OpenRouterModel;
        OpenAIApiKey = _source.ResolvedOpenAIApiKey;
        OpenRouterApiKey = _source.ResolvedOpenRouterApiKey;
        await RefreshMicrophonePermissionAsync();
        NotifySections();
    }

    partial void OnSectionIndexChanged(int value) => NotifySections();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var threshold = HotkeyTriggerTiming.ClampMilliseconds(HotkeyTriggerThresholdMs);
        var theme = ThemeIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        var dictationModel = TranscriptionModelCatalog.Models.FirstOrDefault(model =>
            model.DisplayName == SelectedDictationModel)?.Id ?? _source.DictationModelId;
        var meetingModel = TranscriptionModelCatalog.Models.FirstOrDefault(model =>
            model.DisplayName == SelectedMeetingModel)?.Id ?? _source.FinalMeetingModelId;
        var liveModel = SelectedLiveModel == "Off"
            ? null
            : StreamingModelCatalog.Models.FirstOrDefault(model => model.DisplayName == SelectedLiveModel)?.Id;
        var enableComputerUse = ComputerUseEnabled;
        // Fields this page does not edit (indicator drag position, Shortcuts page, tour state) may
        // have changed since it loaded, so the draft starts from what is on disk now.
        var current = _settingsContext.Load();
        var draft = current with
        {
            UserName = UserName.Trim(),
            OpenDashboardOnLaunch = OpenDashboardOnLaunch,
            StartAtLogin = StartAtLogin,
            AutoMeetingDetectionEnabled = AutoMeetingDetectionEnabled,
            SaveMeetingRecordings = SaveMeetingRecordings,
            MeetingRecordingFormat = RecordingFormatIndex == 1 ? "m4a" : "wav",
            AutoExportMarkdownEnabled = AutoExportMarkdownEnabled,
            AutoExportMarkdownDirectory = AutoExportMarkdownDirectory.Trim(),
            AutoExportPdfEnabled = AutoExportPdfEnabled,
            AutoExportMarkdownContent = AutoExportContents[Math.Clamp(AutoExportContentIndex, 0, AutoExportContents.Length - 1)],
            PostMeetingHookEnabled = PostMeetingHookEnabled,
            PostMeetingHookExecutablePath = PostMeetingHookExecutablePath.Trim(),
            PostMeetingHookTimeoutSeconds = Math.Clamp(PostMeetingHookTimeoutSeconds, 1, 600),
            MeetingSummaryTemplate = MeetingSummaryTemplate,
            MeetingSummaryRetryCount = MeetingSummaryRetryPolicy.Clamp(MeetingSummaryRetryCount),
            RemoveFillerWords = RemoveFillerWords,
            PasteShortcut = PasteShortcuts[Math.Clamp(PasteShortcutIndex, 0, PasteShortcuts.Length - 1)],
            PauseMediaDuringDictation = PauseMediaDuringDictation,
            MuteAudioDuringDictation = MuteAudioDuringDictation,
            EnableLocalCleanup = EnableLocalCleanup,
            CleanupSource = CleanupSources[Math.Clamp(CleanupSourceIndex, 0, CleanupSources.Length - 1)],
            ActiveCleanupPromptId = _cleanupPresets[Math.Clamp(CleanupPresetIndex, 0, _cleanupPresets.Count - 1)].Id,
            EnableAppContext = EnableAppContext,
            ComputerUseEnabled = enableComputerUse,
            ShowFloatingIndicator = ShowFloatingIndicator,
            SoundEnabled = SoundEnabled,
            ShowLiveWaveformOnHover = ShowLiveWaveformOnHover,
            HotkeyTriggerThresholdMs = threshold,
            Theme = theme,
            IndicatorStyle = IndicatorStyleIndex == 1 ? "minimal" : "classic",
            IndicatorAnchor = IndicatorPositionIndex >= 0 && IndicatorPositionIndex < IndicatorAnchors.Length ? IndicatorAnchors[IndicatorPositionIndex] : current.IndicatorAnchor,
            RecordingColorHex = RecordingColorIndex >= 0 ? RecordingColors[RecordingColorIndex].Hex : _source.RecordingColorHex,
            MicrophoneName = SelectedMicrophone == "Automatic" ? null : SelectedMicrophone,
            DictationModelId = dictationModel,
            FinalMeetingModelId = meetingModel,
            LiveMeetingModelId = liveModel,
            LiveTranscriptOwnership = LiveTranscriptOwnershipDescriptor.SettingValueFor(
                LiveOwnershipIndex == 1
                    ? LiveTranscriptOwnershipMode.UnifiedLiveAndFinal
                    : LiveTranscriptOwnershipMode.PreviewOnly),
            ComputerUsePlannerProvider = "chatgpt",
            ComputerUsePlannerModel = SelectedPlannerModel.Id,
            ComputerUseReasoningEffort = ComputerUseConfiguration.Effort(SelectedPlannerModel, _computerUseEffort),
            ComputerUseTimeoutSeconds = Math.Clamp(ComputerUseTimeoutSeconds, 1, 600),
            MeetingSummaryProvider = SelectedSummaryProvider?.Id ?? "local",
            OllamaEndpoint = OllamaEndpoint.Trim(),
            OllamaModel = OllamaModel.Trim(),
            LmStudioEndpoint = LmStudioEndpoint.Trim(),
            LmStudioModel = LmStudioModel.Trim(),
            CustomLlmEndpoint = CustomLlmEndpoint.Trim(),
            CustomLlmModel = CustomLlmModel.Trim(),
            OpenAIModel = OpenAIModel.Trim(),
            OpenRouterModel = OpenRouterModel.Trim(),
            ResolvedOpenAIApiKey = OpenAIApiKey.Trim(),
            ResolvedOpenRouterApiKey = OpenRouterApiKey.Trim(),
            ResolvedCustomLlmApiKey = CustomLlmApiKey.Trim()
        };
        string? warning = null;
        if (draft.ComputerUseEnabled &&
            !ComputerUseConfiguration.TryValidate(draft, _computerUse.ChatGpt.IsSignedIn, out var error))
        {
            draft = draft with { ComputerUseEnabled = false };
            ComputerUseEnabled = false;
            warning = $"Computer Use was turned off: {error}";
        }

        HotkeyTriggerThresholdMs = threshold;
        // Windows can refuse the startup task (e.g. disabled in Task Manager). That must not
        // block keys and every other setting from saving.
        if (IsStartupAvailable)
        {
            try { await _startup.SetEnabledAsync(StartAtLogin); }
            catch (Exception exception)
            {
                warning = (warning is null ? "" : warning + " ") + $"Launch at sign-in could not be changed: {exception.Message}";
            }
        }

        try
        {
            _settingsContext.SaveProviderKeys(OpenAIApiKey, OpenRouterApiKey, CustomLlmApiKey);
            _settingsContext.Save(draft);
            _source = draft;
            _applyDetection(draft.AutoMeetingDetectionEnabled);
            HasUnsavedChanges = false;
            ShowStatus(warning is null ? "Settings saved." : $"Settings saved. {warning}", isError: warning is not null);
        }
        catch (Exception exception)
        {
            // The dirty badge deliberately stays up: nothing reached disk, so the edits are
            // still pending and the user must be able to see that from any tab.
            ShowStatus($"Settings could not be saved: {exception.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task TestMicrophoneAsync()
    {
        MicrophoneProbeResult probe;
        try { probe = await _microphone.ProbeAsync(SelectedMicrophone == "Automatic" ? null : SelectedMicrophone); }
        catch (Exception exception) { ShowStatus($"Microphone test failed: {exception.Message}", true); return; }
        AssignPermissions(WindowsPermissionStatus.Build(
            true,
            probe.Captured,
            probe.Captured ? "Capture verified" : probe.Failure.ToString(),
            string.Join(" ", new[] { probe.PolicyHint, probe.Message }.Where(text => !string.IsNullOrWhiteSpace(text)))));
        ShowStatus(probe.Captured ? "Microphone capture verified. No audio was retained." : probe.Message, !probe.Captured);
    }

    [RelayCommand]
    private async Task ClearDictationsAsync(CancellationToken cancellationToken)
    {
        var choice = await _dialogs.ConfirmAsync(
            "Permanently clear dictation history in this isolated WinUI profile?",
            "Clear dictation history",
            "Clear history",
            "Cancel",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        var count = _library.ClearDictations();
        ShowStatus(count == 0 ? "No dictations to clear." : $"Cleared {count} dictation(s).");
    }

    [RelayCommand]
    private async Task ClearMeetingsAsync(CancellationToken cancellationToken)
    {
        var choice = await _dialogs.ConfirmAsync(
            "Permanently clear meeting history in this isolated WinUI profile? Retained recordings are not deleted.",
            "Clear meeting history",
            "Clear history",
            "Cancel",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        var count = _library.ClearMeetings();
        ShowStatus(count == 0 ? "No meetings to clear." : $"Cleared {count} meeting(s).");
    }

    [RelayCommand]
    private async Task ToggleChatGptSignInAsync()
    {
        if (_computerUse.ChatGpt.IsSignedIn) _computerUse.SignOutOfChatGpt();
        else await _computerUse.SignInToChatGptAsync();
        ComputerUseSignedIn = _computerUse.ChatGpt.IsSignedIn;
    }

    private ComputerUseConfiguration.PlannerModel SelectedPlannerModel =>
        ComputerUseConfiguration.PlannerModels[Math.Clamp(ComputerUseModelIndex, 0, ComputerUseConfiguration.PlannerModels.Count - 1)];

    private static int IndexOfPlannerModel(string id) =>
        ComputerUseConfiguration.PlannerModels.ToList().IndexOf(ComputerUseConfiguration.Model(id));

    /// <summary>macOS keeps the chosen thinking level when the new model supports it, else its default.</summary>
    partial void OnComputerUseModelIndexChanged(int value) => ShowComputerUseEfforts();

    partial void OnComputerUseEffortIndexChanged(int value)
    {
        if (value >= 0 && value < SelectedPlannerModel.Efforts.Count) _computerUseEffort = SelectedPlannerModel.Efforts[value];
    }

    private void ShowComputerUseEfforts()
    {
        var model = SelectedPlannerModel;
        var effort = ComputerUseConfiguration.Effort(model, _computerUseEffort);
        ComputerUseEfforts = model.Efforts.Select(ComputerUseConfiguration.EffortLabel).ToList();
        ComputerUseEffortIndex = model.Efforts.ToList().IndexOf(effort);
    }

    /// <summary>
    /// ComboBox SelectedItem two-way bindings can clear when ItemsSource is assigned.
    /// Re-apply the current selections after load and when a hidden tab is shown.
    /// </summary>
    /// <remarks>
    /// P5-11. <see cref="MeetingSummaryTemplate"/> was missing from this list, which is exactly why
    /// "Default notes template" rendered as an empty ComboBox while the drop-down listed twelve
    /// templates. Its field initialiser is already "Standard Meeting Notes" and
    /// <see cref="LoadAsync"/> assigns the same normalised value, so <c>SetProperty</c> no-ops, no
    /// PropertyChanged is raised, and the selection is never pushed back after the ItemsSource
    /// assignment cleared it. Round-tripping through an empty value forces that push.
    /// </remarks>
    public void ReapplyPickerSelections()
    {
        var tracking = _trackEdits;
        _trackEdits = false;
        try
        {
            var live = string.IsNullOrWhiteSpace(SelectedLiveModel) ? "Off" : SelectedLiveModel;
            var microphone = string.IsNullOrWhiteSpace(SelectedMicrophone) ? "Automatic" : SelectedMicrophone;
            var dictation = SelectedDictationModel;
            var meeting = SelectedMeetingModel;
            var template = MeetingSummaryService.NormalizeTemplateName(MeetingSummaryTemplate);
            var summary = SelectedSummaryProvider ?? SummaryProviderDisclosure.For("local");
            SelectedLiveModel = "";
            SelectedMicrophone = "";
            SelectedDictationModel = "";
            SelectedMeetingModel = "";
            MeetingSummaryTemplate = "";
            SelectedSummaryProvider = null;
            SelectedLiveModel = LiveModels.Contains(live) ? live : "Off";
            SelectedMicrophone = Microphones.Contains(microphone) ? microphone : "Automatic";
            if (!string.IsNullOrWhiteSpace(dictation)) SelectedDictationModel = dictation;
            if (!string.IsNullOrWhiteSpace(meeting)) SelectedMeetingModel = meeting;
            MeetingSummaryTemplate = MeetingTemplates.Contains(template)
                ? template
                : MeetingSummaryService.NormalizeTemplateName(null);
            SelectedSummaryProvider = SummaryProviders.Contains(summary)
                ? summary
                : SummaryProviderDisclosure.For(summary.Id);
        }
        finally
        {
            _trackEdits = tracking;
        }
    }

    partial void OnAutoExportMarkdownEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(AutoExportFolderHelp));

    partial void OnAutoExportMarkdownDirectoryChanged(string value) =>
        OnPropertyChanged(nameof(AutoExportFolderHelp));

    /// <summary>
    /// Re-assigns the permission list so value converters re-run after a theme switch.
    /// </summary>
    public void RefreshPermissionPresentation()
    {
        if (Permissions.Count == 0) return;
        var current = Permissions;
        Permissions = [];
        Permissions = current;
    }

    private async Task RefreshMicrophonePermissionAsync()
    {
        try
        {
            var probe = await _microphone.ProbeAsync(SelectedMicrophone == "Automatic" ? null : SelectedMicrophone);
            AssignPermissions(WindowsPermissionStatus.Build(
                true,
                probe.Captured,
                probe.Captured ? "Capture verified" : probe.Failure.ToString(),
                string.Join(" ", new[] { probe.PolicyHint, probe.Message }.Where(text => !string.IsNullOrWhiteSpace(text)))));
        }
        catch (Exception exception)
        {
            AssignPermissions(WindowsPermissionStatus.Build(true, false, "Unavailable", exception.Message));
        }
    }

    private void AssignPermissions(IReadOnlyList<WindowsPermissionRow> rows)
    {
        Permissions = rows.Select((row, index) => new SettingsPermissionItem(
            row.Name,
            row.Status,
            row.Granted,
            row.Help,
            CanOpenSystemSettings: string.Equals(row.Name, "Microphone", StringComparison.OrdinalIgnoreCase),
            IsLast: index == rows.Count - 1)).ToList();
    }

    private void NotifySections()
    {
        OnPropertyChanged(nameof(ShowGeneral));
        OnPropertyChanged(nameof(ShowDictation));
        OnPropertyChanged(nameof(ShowComputerUse));
        OnPropertyChanged(nameof(ShowMeetings));
        OnPropertyChanged(nameof(ShowAppearance));
    }

    private void ShowStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }
}
