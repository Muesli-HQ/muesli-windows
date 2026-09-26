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

    private void OnComputerUseChanged(object? sender, EventArgs args)
    {
        ComputerUseStatus = _computerUse.Status;
        ComputerUseVoiceLabel = _computerUse.VoiceButtonText;
        ComputerUseStopEnabled = _computerUse.StopEnabled;
        ComputerUseDiagnostics = _computerUse.Diagnostics;
    }

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
    [ObservableProperty] public partial bool EnableLocalCleanup { get; set; }
    [ObservableProperty] public partial bool ComputerUseEnabled { get; set; }
    [ObservableProperty] public partial bool ShowFloatingIndicator { get; set; }
    [ObservableProperty] public partial bool SoundEnabled { get; set; }
    [ObservableProperty] public partial bool ShowLiveWaveformOnHover { get; set; }
    [ObservableProperty] public partial int HotkeyTriggerThresholdMs { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; } = "F8";
    [ObservableProperty] public partial int ThemeIndex { get; set; }
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
    [ObservableProperty] public partial int ComputerUseProviderIndex { get; set; }
    [ObservableProperty] public partial string ComputerUsePlannerModel { get; set; } = "";
    [ObservableProperty] public partial int ComputerUsePlannerTimeoutSeconds { get; set; } = 30;
    [ObservableProperty] public partial int ComputerUsePerActionTimeoutSeconds { get; set; } = 10;
    [ObservableProperty] public partial int ComputerUseMaximumActionCount { get; set; } = 5;
    [ObservableProperty] public partial string ComputerUseAllowedApplications { get; set; } = "";
    [ObservableProperty] public partial string ComputerUseAllowedBrowserDomains { get; set; } = "";
    [ObservableProperty] public partial int ComputerUseBrowserInterfaceIndex { get; set; }
    [ObservableProperty] public partial string ComputerUseBrowserEndpoint { get; set; } = "http://127.0.0.1:9222";
    [ObservableProperty] public partial string ComputerUseStatus { get; private set; } = "";
    [ObservableProperty] public partial string ComputerUseVoiceLabel { get; private set; } = "Speak planner command";
    [ObservableProperty] public partial bool ComputerUseStopEnabled { get; private set; }
    [ObservableProperty] public partial string ComputerUseDiagnostics { get; private set; } = "";
    [ObservableProperty] public partial string CaptureStatus { get; private set; } = "Idle";
    [ObservableProperty] public partial string DetectionStatus { get; private set; } = "";
    [NotifyPropertyChangedFor(nameof(ShowOllamaSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenAISettings))]
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
    public string ChatGptSubscriptionNotice { get; } =
        "ChatGPT subscription sign-in is not available on Windows. Cloud providers in the list above send transcript text to the selected destination.";

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
        nameof(MeetingModels), nameof(LiveModels), nameof(MeetingTemplates), nameof(ComputerUseStatus),
        nameof(ComputerUseVoiceLabel), nameof(ComputerUseStopEnabled), nameof(ComputerUseDiagnostics),
        nameof(CaptureStatus), nameof(DetectionStatus), nameof(IsStartupAvailable),
        nameof(IsStartupUnavailable), nameof(StartupAvailabilityNotice), nameof(AutoExportFolderHelp),
        nameof(SummaryDisclosure), nameof(ShowOllamaSettings), nameof(ShowOpenAISettings),
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
        AutoExportMarkdownEnabled = _source.AutoExportMarkdownEnabled;
        AutoExportMarkdownDirectory = _source.AutoExportMarkdownDirectory;
        AutoExportPdfEnabled = _source.AutoExportPdfEnabled;
        PostMeetingHookEnabled = _source.PostMeetingHookEnabled;
        PostMeetingHookExecutablePath = _source.PostMeetingHookExecutablePath;
        PostMeetingHookTimeoutSeconds = _source.PostMeetingHookTimeoutSeconds;
        MeetingTemplates = MeetingSummaryService.BuiltInTemplateNames.Concat(_library.LoadMeetingTemplates().Select(item => item.Name)).Distinct().ToList();
        MeetingSummaryTemplate = MeetingSummaryService.NormalizeTemplateName(_source.MeetingSummaryTemplate);
        RemoveFillerWords = _source.RemoveFillerWords;
        EnableLocalCleanup = _source.EnableLocalCleanup;
        ComputerUseEnabled = _source.ComputerUseEnabled;
        ShowFloatingIndicator = _source.ShowFloatingIndicator;
        SoundEnabled = _source.SoundEnabled;
        ShowLiveWaveformOnHover = _source.ShowLiveWaveformOnHover;
        HotkeyTriggerThresholdMs = _source.HotkeyTriggerThresholdMs;
        Hotkey = _source.Hotkey;
        ThemeIndex = _source.Theme.Trim().ToLowerInvariant() switch { "light" => 1, "dark" => 2, _ => 0 };
        Microphones = new[] { "Automatic" }.Concat(_dictation.ListMicrophones()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SelectedMicrophone = string.IsNullOrWhiteSpace(_source.MicrophoneName) ? "Automatic" : _source.MicrophoneName;
        DictationModels = TranscriptionModelCatalog.Models.Select(model => model.DisplayName).ToList();
        MeetingModels = DictationModels;
        SelectedDictationModel = TranscriptionModelCatalog.Get(_source.DictationModelId).DisplayName;
        SelectedMeetingModel = TranscriptionModelCatalog.Get(_source.FinalMeetingModelId).DisplayName;
        LiveModels = new[] { "Off" }.Concat(StreamingModelCatalog.Models.Select(model => model.DisplayName)).ToList();
        SelectedLiveModel = string.IsNullOrWhiteSpace(_source.LiveMeetingModelId)
            ? "Off"
            : StreamingModelCatalog.Get(_source.LiveMeetingModelId)?.DisplayName ?? "Off";
        LiveOwnershipIndex = LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(_source.LiveTranscriptOwnership)
            == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? 1 : 0;
        ComputerUseProviderIndex = ComputerUseConfiguration.ProviderLabel(_source.ComputerUsePlannerProvider) == "OpenAI" ? 1 : 0;
        ComputerUsePlannerModel = _source.ComputerUsePlannerModel;
        ComputerUsePlannerTimeoutSeconds = _source.ComputerUsePlannerTimeoutSeconds;
        ComputerUsePerActionTimeoutSeconds = _source.ComputerUsePerActionTimeoutSeconds;
        ComputerUseMaximumActionCount = _source.ComputerUseMaximumActionCount;
        ComputerUseAllowedApplications = _source.ComputerUseAllowedApplications;
        ComputerUseAllowedBrowserDomains = _source.ComputerUseAllowedBrowserDomains;
        ComputerUseBrowserInterfaceIndex = ComputerUseConfiguration.BrowserInterfaceLabel(_source.ComputerUseBrowserInterface) == "Loopback DevTools" ? 1 : 0;
        ComputerUseBrowserEndpoint = _source.ComputerUseBrowserEndpoint;
        ComputerUseStatus = _computerUse.Status;
        ComputerUseVoiceLabel = _computerUse.VoiceButtonText;
        ComputerUseStopEnabled = _computerUse.StopEnabled;
        _computerUse.RefreshDiagnostics();
        ComputerUseDiagnostics = _computerUse.Diagnostics;
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
        var draft = _source with
        {
            UserName = UserName.Trim(),
            OpenDashboardOnLaunch = OpenDashboardOnLaunch,
            StartAtLogin = StartAtLogin,
            AutoMeetingDetectionEnabled = AutoMeetingDetectionEnabled,
            SaveMeetingRecordings = SaveMeetingRecordings,
            AutoExportMarkdownEnabled = AutoExportMarkdownEnabled,
            AutoExportMarkdownDirectory = AutoExportMarkdownDirectory.Trim(),
            AutoExportPdfEnabled = AutoExportPdfEnabled,
            PostMeetingHookEnabled = PostMeetingHookEnabled,
            PostMeetingHookExecutablePath = PostMeetingHookExecutablePath.Trim(),
            PostMeetingHookTimeoutSeconds = Math.Clamp(PostMeetingHookTimeoutSeconds, 1, 600),
            MeetingSummaryTemplate = MeetingSummaryTemplate,
            RemoveFillerWords = RemoveFillerWords,
            EnableLocalCleanup = EnableLocalCleanup,
            ComputerUseEnabled = enableComputerUse,
            ShowFloatingIndicator = ShowFloatingIndicator,
            SoundEnabled = SoundEnabled,
            ShowLiveWaveformOnHover = ShowLiveWaveformOnHover,
            HotkeyTriggerThresholdMs = threshold,
            Theme = theme,
            MicrophoneName = SelectedMicrophone == "Automatic" ? null : SelectedMicrophone,
            DictationModelId = dictationModel,
            FinalMeetingModelId = meetingModel,
            LiveMeetingModelId = liveModel,
            LiveTranscriptOwnership = LiveTranscriptOwnershipDescriptor.SettingValueFor(
                LiveOwnershipIndex == 1
                    ? LiveTranscriptOwnershipMode.UnifiedLiveAndFinal
                    : LiveTranscriptOwnershipMode.PreviewOnly),
            ComputerUsePlannerProvider = ComputerUseProviderIndex == 1 ? "openai" : "none",
            ComputerUsePlannerModel = ComputerUsePlannerModel.Trim(),
            ComputerUsePlannerTimeoutSeconds = ComputerUsePlannerTimeoutSeconds,
            ComputerUsePerActionTimeoutSeconds = ComputerUsePerActionTimeoutSeconds,
            ComputerUseMaximumActionCount = ComputerUseMaximumActionCount,
            ComputerUseAllowedApplications = ComputerUseConfiguration.NormalizeAllowlist(ComputerUseAllowedApplications),
            ComputerUseAllowedBrowserDomains = ComputerUseConfiguration.NormalizeAllowlist(ComputerUseAllowedBrowserDomains),
            ComputerUseIncludeWindowText = false,
            ComputerUseIncludeBrowserPageText = false,
            ComputerUseIncludeScreenshots = false,
            ComputerUseBrowserInterface = ComputerUseBrowserInterfaceIndex == 1 ? "loopback-devtools" : "none",
            ComputerUseBrowserEndpoint = ComputerUseBrowserEndpoint.Trim(),
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
        if (draft.ComputerUseEnabled &&
            !ComputerUseConfiguration.TryValidate(draft, draft.ResolvedOpenAIApiKey, out var error))
        {
            draft = draft with { ComputerUseEnabled = false };
            ComputerUseEnabled = false;
            ShowStatus(error, isError: true);
        }

        HotkeyTriggerThresholdMs = threshold;
        ComputerUseAllowedApplications = draft.ComputerUseAllowedApplications;
        ComputerUseAllowedBrowserDomains = draft.ComputerUseAllowedBrowserDomains;
        try
        {
            if (IsStartupAvailable) await _startup.SetEnabledAsync(StartAtLogin);
            _settingsContext.SaveProviderKeys(OpenAIApiKey, OpenRouterApiKey, CustomLlmApiKey);
            _settingsContext.Save(draft);
            _source = draft;
            _applyDetection(draft.AutoMeetingDetectionEnabled);
            HasUnsavedChanges = false;
            ShowStatus("Settings saved.");
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
        var probe = await _microphone.ProbeAsync(SelectedMicrophone == "Automatic" ? null : SelectedMicrophone);
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
    private async Task SpeakComputerUseAsync() => await _computerUse.ToggleVoiceAsync();

    [RelayCommand]
    private async Task StopComputerUseAsync() => await _computerUse.CancelAsync();

    [RelayCommand]
    private void OpenComputerUseDiagnostics()
    {
        _computerUse.RefreshDiagnostics();
        ComputerUseDiagnostics = _computerUse.Diagnostics;
        ShowStatus("Loaded the redacted Computer Use trace.");
    }

    [RelayCommand]
    private void PreviewComputerUseConfirmation()
    {
        var apps = ComputerUseConfiguration.ParseAllowlist(ComputerUseAllowedApplications);
        if (apps.Length == 0)
        {
            ShowStatus("Allow at least one application before previewing a confirmation.", isError: true);
            return;
        }

        _computerUse.PreviewConfirmation(new ComputerUseAction(
            ComputerUseActionKind.FocusWindow,
            new ComputerUseTarget(apps[0], null, null, null, null),
            null,
            ComputerUseRisk.None));
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
