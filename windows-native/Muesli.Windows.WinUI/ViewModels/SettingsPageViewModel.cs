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
    private readonly WinUiDictationContext _dictation;
    private readonly Action<bool> _applyDetection;
    private readonly WindowsMicrophoneAccessService _microphone = new();

    public SettingsPageViewModel(
        WinUiSettingsContext settingsContext,
        WinUiLibraryContext library,
        IStartupRegistrationService startup,
        IAppDialogService dialogs,
        WinUiComputerUseContext computerUse,
        WinUiDictationContext dictation,
        Action<bool> applyDetection)
    {
        _settingsContext = settingsContext;
        _library = library;
        _startup = startup;
        _dialogs = dialogs;
        _computerUse = computerUse;
        _dictation = dictation;
        _applyDetection = applyDetection;
        _computerUse.Changed += OnComputerUseChanged;
    }

    private void OnComputerUseChanged(object? sender, EventArgs args)
    {
        ComputerUseStatus = _computerUse.Status;
        ComputerUseVoiceLabel = _computerUse.VoiceButtonText;
        ComputerUseStopEnabled = _computerUse.StopEnabled;
        ComputerUseDiagnostics = _computerUse.Diagnostics;
    }

    public void Dispose()
    {
        _computerUse.Changed -= OnComputerUseChanged;
        // Its local callback listener would otherwise outlive the page, and a late approval would
        // save a key from a view model that is gone.
        try { _openRouterSignIn?.Cancel(); }
        catch (ObjectDisposedException) { }
        _debounce?.Cancel();
        FlushPendingEdits();
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
    [ObservableProperty] public partial bool ShareUsageAnalytics { get; set; }
    [ObservableProperty] public partial bool ShowFloatingIndicator { get; set; }
    [ObservableProperty] public partial bool SoundEnabled { get; set; }
    [ObservableProperty] public partial bool ShowLiveWaveformOnHover { get; set; }
    [ObservableProperty] public partial int HotkeyTriggerThresholdMs { get; set; }
    [ObservableProperty] public partial string Hotkey { get; set; } = "F8";
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial int RecordingColorIndex { get; set; }
    [ObservableProperty] public partial int IndicatorStyleIndex { get; set; }

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
        : "Not available yet.";

    public string AutoExportFolderHelp =>
        !AutoExportMarkdownEnabled
            ? "Turn on automatic export to choose where the Markdown copy is written."
            : string.IsNullOrWhiteSpace(AutoExportMarkdownDirectory)
                ? "Nothing is exported until you choose a folder on this PC."
                : "A Markdown copy of each completed meeting is written here.";

    public void ReportFolderPickerFailure(string message) =>
        ShowStatus($"The folder could not be chosen: {message}", isError: true);

    // ---- Saving -----------------------------------------------------------------------------
    // As on macOS, there is no Save button: each control writes its own setting when it changes.
    // Toggles, pickers and number boxes save at once. Text saves after a short pause in typing and
    // when the field loses focus, so a half-typed value is not written on every keystroke. Every
    // write starts from the settings context's current snapshot and changes one field, so a
    // setting saved elsewhere (the Shortcuts or Models page) is never overwritten from here.

    /// <summary>How long a text field waits after the last keystroke before it is saved.</summary>
    private static readonly TimeSpan TextSaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly HashSet<string> _pendingText = [];
    private CancellationTokenSource? _debounce;
    private bool _tracking;
    private string? _errorField;

    /// <summary>Starts saving edits. Called once the initial load has filled every field.</summary>
    public void BeginTracking() => _tracking = true;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_tracking || e.PropertyName is not { } name) return;
        if (TextFields.ContainsKey(name))
        {
            _pendingText.Add(name);
            ScheduleFlush();
        }
        else if (name == nameof(StartAtLogin))
        {
            _ = ApplyStartAtLoginAsync(StartAtLogin);
        }
        else
        {
            CommitChoice(name);
        }
    }

    /// <summary>
    /// Saves text that is still waiting out its typing pause. Called when a field loses focus
    /// (<paramref name="syncText"/> then shows the value as it was stored, e.g. trimmed) and when
    /// the page is left.
    /// </summary>
    public void FlushPendingEdits(bool syncText = false)
    {
        _debounce?.Cancel();
        if (_pendingText.Count == 0) return;
        var names = _pendingText.ToArray();
        _pendingText.Clear();
        foreach (var name in names)
        {
            CommitText(name, syncText);
        }
    }

    private async void ScheduleFlush()
    {
        _debounce?.Cancel();
        var debounce = _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(TextSaveDelay, debounce.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        FlushPendingEdits();
    }

    private sealed record TextField(
        string Label,
        Func<SettingsPageViewModel, string> Get,
        Action<SettingsPageViewModel, string> Set,
        Func<MuesliSettings, string> Read,
        Func<MuesliSettings, string, MuesliSettings> Write,
        Func<string, string?>? Validate = null,
        string? SecretKey = null);

    /// <summary>
    /// Every free-text setting. Provider keys carry a <c>SecretKey</c>: they go to Windows
    /// Credential Manager one at a time and never into the settings file.
    /// </summary>
    private static readonly Dictionary<string, TextField> TextFields = new()
    {
        [nameof(UserName)] = new("Your name",
            vm => vm.UserName, (vm, value) => vm.UserName = value,
            settings => settings.UserName, (settings, value) => settings with { UserName = value }),
        [nameof(AutoExportMarkdownDirectory)] = new("Export folder",
            vm => vm.AutoExportMarkdownDirectory, (vm, value) => vm.AutoExportMarkdownDirectory = value,
            settings => settings.AutoExportMarkdownDirectory,
            (settings, value) => settings with { AutoExportMarkdownDirectory = value },
            SettingsFieldValidation.ExportDirectory),
        [nameof(PostMeetingHookExecutablePath)] = new("Program path",
            vm => vm.PostMeetingHookExecutablePath, (vm, value) => vm.PostMeetingHookExecutablePath = value,
            settings => settings.PostMeetingHookExecutablePath,
            (settings, value) => settings with { PostMeetingHookExecutablePath = value },
            SettingsFieldValidation.HookExecutable),
        [nameof(OllamaEndpoint)] = new("Ollama server",
            vm => vm.OllamaEndpoint, (vm, value) => vm.OllamaEndpoint = value,
            settings => settings.OllamaEndpoint, (settings, value) => settings with { OllamaEndpoint = value },
            SettingsFieldValidation.HttpEndpoint),
        [nameof(OllamaModel)] = new("Ollama model",
            vm => vm.OllamaModel, (vm, value) => vm.OllamaModel = value,
            settings => settings.OllamaModel, (settings, value) => settings with { OllamaModel = value }),
        [nameof(LmStudioEndpoint)] = new("LM Studio server",
            vm => vm.LmStudioEndpoint, (vm, value) => vm.LmStudioEndpoint = value,
            settings => settings.LmStudioEndpoint, (settings, value) => settings with { LmStudioEndpoint = value },
            SettingsFieldValidation.HttpEndpoint),
        [nameof(LmStudioModel)] = new("LM Studio model",
            vm => vm.LmStudioModel, (vm, value) => vm.LmStudioModel = value,
            settings => settings.LmStudioModel, (settings, value) => settings with { LmStudioModel = value }),
        [nameof(CustomLlmEndpoint)] = new("Custom endpoint",
            vm => vm.CustomLlmEndpoint, (vm, value) => vm.CustomLlmEndpoint = value,
            settings => settings.CustomLlmEndpoint, (settings, value) => settings with { CustomLlmEndpoint = value },
            SettingsFieldValidation.HttpEndpoint),
        [nameof(CustomLlmModel)] = new("Custom model",
            vm => vm.CustomLlmModel, (vm, value) => vm.CustomLlmModel = value,
            settings => settings.CustomLlmModel, (settings, value) => settings with { CustomLlmModel = value }),
        [nameof(ClaudeCodeExecutablePath)] = new("Claude Code path",
            vm => vm.ClaudeCodeExecutablePath, (vm, value) => vm.ClaudeCodeExecutablePath = value,
            settings => settings.ClaudeCodeExecutablePath, (settings, value) => settings with { ClaudeCodeExecutablePath = value }),
        [nameof(OpenAIModel)] = new("OpenAI model",
            vm => vm.OpenAIModel, (vm, value) => vm.OpenAIModel = value,
            settings => settings.OpenAIModel, (settings, value) => settings with { OpenAIModel = value }),
        [nameof(OpenRouterModel)] = new("OpenRouter model",
            vm => vm.OpenRouterModel, (vm, value) => vm.OpenRouterModel = value,
            settings => settings.OpenRouterModel, (settings, value) => settings with { OpenRouterModel = value }),
        [nameof(OpenAIApiKey)] = new("OpenAI API key",
            vm => vm.OpenAIApiKey, (vm, value) => vm.OpenAIApiKey = value,
            settings => settings.ResolvedOpenAIApiKey, (settings, _) => settings,
            SecretKey: SettingsStore.OpenAISecretKey),
        [nameof(OpenRouterApiKey)] = new("OpenRouter API key",
            vm => vm.OpenRouterApiKey, (vm, value) => vm.OpenRouterApiKey = value,
            settings => settings.ResolvedOpenRouterApiKey, (settings, _) => settings,
            SecretKey: SettingsStore.OpenRouterSecretKey),
        [nameof(AnthropicApiKey)] = new("Anthropic API key",
            vm => vm.AnthropicApiKey, (vm, value) => vm.AnthropicApiKey = value,
            settings => settings.ResolvedAnthropicApiKey, (settings, _) => settings,
            SecretKey: SettingsStore.AnthropicSecretKey),
        [nameof(OpenRouterDictationModel)] = new("OpenRouter dictation model",
            vm => vm.OpenRouterDictationModel, (vm, value) => vm.OpenRouterDictationModel = value,
            settings => settings.OpenRouterDictationModel,
            (settings, value) => settings with { OpenRouterDictationModel = value }),
        [nameof(CustomLlmApiKey)] = new("Custom API key",
            vm => vm.CustomLlmApiKey, (vm, value) => vm.CustomLlmApiKey = value,
            settings => settings.ResolvedCustomLlmApiKey, (settings, _) => settings,
            SecretKey: SettingsStore.CustomLlmSecretKey),
        [nameof(ComputerUsePlannerModel)] = new("Planner model ID",
            vm => vm.ComputerUsePlannerModel, (vm, value) => vm.ComputerUsePlannerModel = value,
            settings => settings.ComputerUsePlannerModel,
            (settings, value) => settings with { ComputerUsePlannerModel = value }),
        [nameof(ComputerUseAllowedApplications)] = new("Allowed applications",
            vm => vm.ComputerUseAllowedApplications, (vm, value) => vm.ComputerUseAllowedApplications = value,
            settings => settings.ComputerUseAllowedApplications,
            (settings, value) => settings with
            {
                ComputerUseAllowedApplications = ComputerUseConfiguration.NormalizeAllowlist(value)
            }),
        [nameof(ComputerUseAllowedBrowserDomains)] = new("Allowed HTTPS origins",
            vm => vm.ComputerUseAllowedBrowserDomains, (vm, value) => vm.ComputerUseAllowedBrowserDomains = value,
            settings => settings.ComputerUseAllowedBrowserDomains,
            (settings, value) => settings with
            {
                ComputerUseAllowedBrowserDomains = ComputerUseConfiguration.NormalizeAllowlist(value)
            }),
        [nameof(ComputerUseBrowserEndpoint)] = new("Loopback DevTools endpoint",
            vm => vm.ComputerUseBrowserEndpoint, (vm, value) => vm.ComputerUseBrowserEndpoint = value,
            settings => settings.ComputerUseBrowserEndpoint,
            (settings, value) => settings with { ComputerUseBrowserEndpoint = value },
            SettingsFieldValidation.LoopbackEndpoint)
    };

    private void CommitText(string name, bool syncText)
    {
        var field = TextFields[name];
        var value = (field.Get(this) ?? "").Trim();
        if (field.Validate?.Invoke(value) is { } problem)
        {
            // Nothing is written: the last saved value stays in effect until this one is valid.
            ShowFieldError(name, $"{field.Label} was not saved. {problem}");
            return;
        }

        try
        {
            var current = _settingsContext.Load();
            if (field.SecretKey is { } secretKey)
            {
                if (!string.Equals(field.Read(current), value, StringComparison.Ordinal))
                {
                    _settingsContext.SaveProviderKey(secretKey, value);
                }

                current = _settingsContext.Load();
            }

            Persist(name, current, field.Write(current, value));
        }
        catch (Exception exception)
        {
            // Still pending, so leaving the field or the page tries again.
            _pendingText.Add(name);
            ShowFieldError(name, $"{field.Label} could not be saved: {exception.Message}");
            return;
        }

        if (syncText)
        {
            Untracked(() => field.Set(this, field.Read(_settingsContext.Load())));
        }
    }

    private void CommitChoice(string name)
    {
        try
        {
            var current = _settingsContext.Load();
            if (ApplyChoice(name, current) is { } draft)
            {
                Persist(name, current, draft);
            }
        }
        catch (Exception exception)
        {
            // The control must not show a value that is not in effect.
            RestoreSavedValues();
            ShowFieldError(name, $"That change could not be saved: {exception.Message}");
        }
    }

    /// <summary>
    /// The current settings with one toggle, picker or number applied, or <c>null</c> when
    /// <paramref name="name"/> is not a setting or holds no real choice. A ComboBox can report an
    /// empty selection while its list is being assigned; that is not the user picking nothing.
    /// </summary>
    private MuesliSettings? ApplyChoice(string name, MuesliSettings settings) => name switch
    {
        nameof(OpenDashboardOnLaunch) => settings with { OpenDashboardOnLaunch = OpenDashboardOnLaunch },
        nameof(StartAtLogin) => settings with { StartAtLogin = StartAtLogin },
        nameof(AutoMeetingDetectionEnabled) => settings with { AutoMeetingDetectionEnabled = AutoMeetingDetectionEnabled },
        nameof(SaveMeetingRecordings) => settings with { SaveMeetingRecordings = SaveMeetingRecordings },
        nameof(AutoExportMarkdownEnabled) => settings with { AutoExportMarkdownEnabled = AutoExportMarkdownEnabled },
        nameof(AutoExportPdfEnabled) => settings with { AutoExportPdfEnabled = AutoExportPdfEnabled },
        nameof(PostMeetingHookEnabled) => settings with { PostMeetingHookEnabled = PostMeetingHookEnabled },
        nameof(PostMeetingHookTimeoutSeconds) =>
            settings with { PostMeetingHookTimeoutSeconds = Math.Clamp(PostMeetingHookTimeoutSeconds, 1, 600) },
        nameof(MeetingSummaryTemplate) when MeetingTemplates.Contains(MeetingSummaryTemplate) =>
            settings with { MeetingSummaryTemplate = MeetingSummaryTemplate },
        nameof(RemoveFillerWords) => settings with { RemoveFillerWords = RemoveFillerWords },
        nameof(EnableLocalCleanup) => settings with { EnableLocalCleanup = EnableLocalCleanup },
        nameof(ShareUsageAnalytics) => settings with { ShareUsageAnalytics = ShareUsageAnalytics },
        nameof(ShowFloatingIndicator) => settings with { ShowFloatingIndicator = ShowFloatingIndicator },
        nameof(SoundEnabled) => settings with { SoundEnabled = SoundEnabled },
        nameof(ShowLiveWaveformOnHover) => settings with { ShowLiveWaveformOnHover = ShowLiveWaveformOnHover },
        nameof(HotkeyTriggerThresholdMs) =>
            settings with { HotkeyTriggerThresholdMs = HotkeyTriggerTiming.ClampMilliseconds(HotkeyTriggerThresholdMs) },
        nameof(ThemeIndex) when ThemeIndex >= 0 =>
            settings with { Theme = ThemeIndex switch { 1 => "light", 2 => "dark", _ => "system" } },
        nameof(IndicatorStyleIndex) when IndicatorStyleIndex >= 0 =>
            settings with { IndicatorStyle = IndicatorStyleIndex == 1 ? "minimal" : "classic" },
        nameof(RecordingColorIndex) when RecordingColorIndex >= 0 && RecordingColorIndex < RecordingColors.Length =>
            settings with { RecordingColorHex = RecordingColors[RecordingColorIndex].Hex },
        nameof(SelectedMicrophone) when SelectedMicrophone is { Length: > 0 } microphone && Microphones.Contains(microphone) =>
            settings with { MicrophoneName = microphone == "Automatic" ? null : microphone },
        nameof(SelectedDictationModel) when TranscriptionModelId(SelectedDictationModel) is { } id =>
            settings with { DictationModelId = id },
        nameof(SelectedMeetingModel) when TranscriptionModelId(SelectedMeetingModel) is { } id =>
            settings with { FinalMeetingModelId = id },
        nameof(SelectedLiveModel) when SelectedLiveModel == "Off" => settings with { LiveMeetingModelId = null },
        nameof(SelectedLiveModel) when StreamingModelCatalog.Models
            .FirstOrDefault(model => model.DisplayName == SelectedLiveModel)?.Id is { } id =>
            settings with { LiveMeetingModelId = id },
        nameof(SelectedChatGptModel) when SelectedChatGptModel is { } model => settings with { ChatGptModel = model.Id },
        nameof(SelectedClaudeCodeModel) when SelectedClaudeCodeModel is { } model => settings with { ClaudeCodeModel = model.Id },
        nameof(SelectedAnthropicModel) when SelectedAnthropicModel is { } model => settings with { AnthropicModel = model.Id },
        nameof(SelectedDictationProvider) when SelectedDictationProvider is { } provider =>
            settings with { DictationProvider = provider.Id },
        nameof(SelectedOpenAIDictationModel) when SelectedOpenAIDictationModel is { } model =>
            settings with { OpenAIDictationModel = model.Id },
        nameof(SelectedSummaryProvider) when SelectedSummaryProvider is { } provider =>
            settings with { MeetingSummaryProvider = provider.Id },
        nameof(ComputerUseEnabled) => settings with { ComputerUseEnabled = ComputerUseEnabled },
        nameof(ComputerUseProviderIndex) when ComputerUseProviderIndex >= 0 =>
            settings with { ComputerUsePlannerProvider = ComputerUseProviderIndex == 1 ? "openai" : "none" },
        nameof(ComputerUsePlannerTimeoutSeconds) =>
            settings with { ComputerUsePlannerTimeoutSeconds = ComputerUsePlannerTimeoutSeconds },
        nameof(ComputerUsePerActionTimeoutSeconds) =>
            settings with { ComputerUsePerActionTimeoutSeconds = ComputerUsePerActionTimeoutSeconds },
        nameof(ComputerUseMaximumActionCount) =>
            settings with { ComputerUseMaximumActionCount = ComputerUseMaximumActionCount },
        nameof(ComputerUseBrowserInterfaceIndex) when ComputerUseBrowserInterfaceIndex >= 0 => settings with
        {
            ComputerUseBrowserInterface = ComputerUseBrowserInterfaceIndex == 1 ? "loopback-devtools" : "none"
        },
        _ => null
    };

    private static string? TranscriptionModelId(string? displayName) =>
        TranscriptionModelCatalog.Models.FirstOrDefault(model => model.DisplayName == displayName)?.Id;

    /// <summary>
    /// Writes <paramref name="draft"/> when it differs from <paramref name="current"/> and applies
    /// what depends on the setting that changed. Unchanged values write nothing, so a setting's
    /// side effects run once, when that setting changes.
    /// </summary>
    private void Persist(string name, MuesliSettings current, MuesliSettings draft)
    {
        string? computerUseProblem = null;
        if (name.StartsWith("ComputerUse", StringComparison.Ordinal) || name == nameof(OpenAIApiKey))
        {
            // The observation switches are not offered yet, so they stay off whatever the file says.
            draft = draft with
            {
                ComputerUseIncludeWindowText = false,
                ComputerUseIncludeBrowserPageText = false,
                ComputerUseIncludeScreenshots = false
            };
            if (draft.ComputerUseEnabled &&
                !ComputerUseConfiguration.TryValidate(draft, draft.ResolvedOpenAIApiKey, out var error))
            {
                draft = draft with { ComputerUseEnabled = false };
                computerUseProblem = $"Computer Use was turned off: {error}";
            }
        }

        if (draft != current)
        {
            _settingsContext.Save(draft);
            if (draft.AutoMeetingDetectionEnabled != current.AutoMeetingDetectionEnabled)
            {
                _applyDetection(draft.AutoMeetingDetectionEnabled);
            }
        }

        if (computerUseProblem is null)
        {
            ClearFieldError(name);
            return;
        }

        Untracked(() => ComputerUseEnabled = false);
        ShowFieldError(nameof(ComputerUseEnabled), computerUseProblem);
    }

    /// <summary>
    /// Windows owns the startup task and can refuse it (for example when it was disabled in Task
    /// Manager), so the task is changed first and the toggle is put back if Windows says no.
    /// </summary>
    private async Task ApplyStartAtLoginAsync(bool enabled)
    {
        if (!IsStartupAvailable) return;
        try
        {
            await _startup.SetEnabledAsync(enabled);
        }
        catch (Exception exception)
        {
            Untracked(() => StartAtLogin = !enabled);
            ShowFieldError(nameof(StartAtLogin), $"Launch at login could not be changed: {exception.Message}");
            return;
        }

        CommitChoice(nameof(StartAtLogin));
    }

    private void RestoreSavedValues()
    {
        _pendingText.Clear();
        Untracked(() =>
        {
            ApplySnapshot(_settingsContext.Load());
            ReapplyPickerSelections();
        });
    }

    private void Untracked(Action change)
    {
        var tracking = _tracking;
        _tracking = false;
        try
        {
            change();
        }
        finally
        {
            _tracking = tracking;
        }
    }

    private void ShowFieldError(string name, string message)
    {
        ShowStatus(message, isError: true);
        _errorField = name;
    }

    /// <summary>Closes the error once the field it was about has been saved.</summary>
    private void ClearFieldError(string name)
    {
        if (_errorField != name) return;
        _errorField = null;
        IsStatusOpen = false;
    }

    public async Task LoadAsync()
    {
        _tracking = false;
        var settings = _settingsContext.Load();
        ApplySnapshot(settings);
        try
        {
            StartAtLogin = await _startup.IsEnabledAsync();
            IsStartupAvailable = true;
            StartupAvailabilityNotice = "";
        }
        catch (Exception)
        {
            // The Windows startup task lives in the MSIX manifest, so it is only reachable
            // when the shell runs with package identity. Say so instead of leaving a dead toggle.
            StartAtLogin = false;
            IsStartupAvailable = false;
            StartupAvailabilityNotice = "Launch at login isn't available in this build.";
        }

        await RefreshMicrophonePermissionAsync();
        NotifySections();
    }

    /// <summary>Fills every field from saved settings. Launch at login comes from Windows, not from here.</summary>
    private void ApplySnapshot(MuesliSettings settings)
    {
        UserName = settings.UserName;
        OpenDashboardOnLaunch = settings.OpenDashboardOnLaunch;
        AutoMeetingDetectionEnabled = settings.AutoMeetingDetectionEnabled;
        SaveMeetingRecordings = settings.SaveMeetingRecordings;
        AutoExportMarkdownEnabled = settings.AutoExportMarkdownEnabled;
        AutoExportMarkdownDirectory = settings.AutoExportMarkdownDirectory;
        AutoExportPdfEnabled = settings.AutoExportPdfEnabled;
        PostMeetingHookEnabled = settings.PostMeetingHookEnabled;
        PostMeetingHookExecutablePath = settings.PostMeetingHookExecutablePath;
        PostMeetingHookTimeoutSeconds = settings.PostMeetingHookTimeoutSeconds;
        MeetingTemplates = MeetingSummaryService.BuiltInTemplateNames.Concat(_library.LoadMeetingTemplates().Select(item => item.Name)).Distinct().ToList();
        MeetingSummaryTemplate = MeetingSummaryService.NormalizeTemplateName(settings.MeetingSummaryTemplate);
        RemoveFillerWords = settings.RemoveFillerWords;
        EnableLocalCleanup = settings.EnableLocalCleanup;
        ComputerUseEnabled = settings.ComputerUseEnabled;
        ShareUsageAnalytics = settings.ShareUsageAnalytics;
        ShowFloatingIndicator = settings.ShowFloatingIndicator;
        SoundEnabled = settings.SoundEnabled;
        ShowLiveWaveformOnHover = settings.ShowLiveWaveformOnHover;
        HotkeyTriggerThresholdMs = settings.HotkeyTriggerThresholdMs;
        Hotkey = settings.Hotkey;
        ThemeIndex = settings.Theme.Trim().ToLowerInvariant() switch { "light" => 1, "dark" => 2, _ => 0 };
        IndicatorStyleIndex = settings.IndicatorStyle == "minimal" ? 1 : 0;
        RecordingColorIndex = Array.FindIndex(RecordingColors, color => color.Hex == settings.RecordingColorHex);
        Microphones = new[] { "Automatic" }.Concat(_dictation.ListMicrophones()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SelectedMicrophone = string.IsNullOrWhiteSpace(settings.MicrophoneName) ? "Automatic" : settings.MicrophoneName;
        // macOS lists only installed models; the Models page owns downloads. The saved selections
        // stay listed so the picker never shows blank (dictation then fails closed with guidance).
        DictationModels = TranscriptionModelCatalog.Models
            .Where(model => TranscriptionModelReadiness.IsVerified(model) ||
                model.Id == settings.DictationModelId || model.Id == settings.FinalMeetingModelId)
            .Select(model => model.DisplayName).ToList();
        MeetingModels = DictationModels;
        SelectedDictationModel = TranscriptionModelCatalog.Get(settings.DictationModelId).DisplayName;
        SelectedMeetingModel = TranscriptionModelCatalog.Get(settings.FinalMeetingModelId).DisplayName;
        LiveModels = new[] { "Off" }.Concat(StreamingModelCatalog.Models.Select(model => model.DisplayName)).ToList();
        SelectedLiveModel = string.IsNullOrWhiteSpace(settings.LiveMeetingModelId)
            ? "Off"
            : StreamingModelCatalog.Get(settings.LiveMeetingModelId)?.DisplayName ?? "Off";
        ComputerUseProviderIndex = ComputerUseConfiguration.ProviderLabel(settings.ComputerUsePlannerProvider) == "OpenAI" ? 1 : 0;
        ComputerUsePlannerModel = settings.ComputerUsePlannerModel;
        ComputerUsePlannerTimeoutSeconds = settings.ComputerUsePlannerTimeoutSeconds;
        ComputerUsePerActionTimeoutSeconds = settings.ComputerUsePerActionTimeoutSeconds;
        ComputerUseMaximumActionCount = settings.ComputerUseMaximumActionCount;
        ComputerUseAllowedApplications = settings.ComputerUseAllowedApplications;
        ComputerUseAllowedBrowserDomains = settings.ComputerUseAllowedBrowserDomains;
        ComputerUseBrowserInterfaceIndex = ComputerUseConfiguration.BrowserInterfaceLabel(settings.ComputerUseBrowserInterface) == "Loopback DevTools" ? 1 : 0;
        ComputerUseBrowserEndpoint = settings.ComputerUseBrowserEndpoint;
        ComputerUseStatus = _computerUse.Status;
        ComputerUseVoiceLabel = _computerUse.VoiceButtonText;
        ComputerUseStopEnabled = _computerUse.StopEnabled;
        _computerUse.RefreshDiagnostics();
        ComputerUseDiagnostics = _computerUse.Diagnostics;
        SelectedSummaryProvider = SummaryProviderDisclosure.For(settings.MeetingSummaryProvider);
        OllamaEndpoint = settings.OllamaEndpoint;
        OllamaModel = settings.OllamaModel;
        LmStudioEndpoint = settings.LmStudioEndpoint;
        LmStudioModel = settings.LmStudioModel;
        CustomLlmEndpoint = settings.CustomLlmEndpoint;
        CustomLlmModel = settings.CustomLlmModel;
        CustomLlmApiKey = settings.ResolvedCustomLlmApiKey;
        OpenAIModel = settings.OpenAIModel;
        SelectedChatGptModel = ChatGptModels.FirstOrDefault(choice => choice.Id.Equals(ChatGptResponsesClient.NormalizeModel(settings.ChatGptModel), StringComparison.OrdinalIgnoreCase)) ?? ChatGptModels[0];
        SelectedClaudeCodeModel = ClaudeCodeModels.FirstOrDefault(choice => choice.Id.Equals(settings.ClaudeCodeModel, StringComparison.OrdinalIgnoreCase)) ?? ClaudeCodeModels[0];
        ClaudeCodeExecutablePath = settings.ClaudeCodeExecutablePath;
        SelectedAnthropicModel = AnthropicModels.FirstOrDefault(choice => choice.Id.Equals(AnthropicMessagesClient.NormalizeModel(settings.AnthropicModel), StringComparison.OrdinalIgnoreCase)) ?? AnthropicModels[0];
        AnthropicApiKey = settings.ResolvedAnthropicApiKey;
        SelectedDictationProvider = DictationProviderChoices.First(choice => choice.Id == DictationProviders.Normalize(settings.DictationProvider));
        SelectedOpenAIDictationModel = OpenAIDictationModels.FirstOrDefault(choice => choice.Id.Equals(OpenAIRealtimeProtocol.NormalizeModel(settings.OpenAIDictationModel), StringComparison.OrdinalIgnoreCase)) ?? OpenAIDictationModels[0];
        OpenRouterDictationModel = settings.OpenRouterDictationModel;
        RefreshChatGptAccount();
        OpenRouterModel = settings.OpenRouterModel;
        OpenAIApiKey = settings.ResolvedOpenAIApiKey;
        OpenRouterApiKey = settings.ResolvedOpenRouterApiKey;
    }

    partial void OnSectionIndexChanged(int value) => NotifySections();

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
            "Permanently clear your dictation history?",
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
            "Permanently clear your meeting history? Saved recordings are not deleted.",
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
        var tracking = _tracking;
        _tracking = false;
        try
        {
            var live = string.IsNullOrWhiteSpace(SelectedLiveModel) ? "Off" : SelectedLiveModel;
            var microphone = string.IsNullOrWhiteSpace(SelectedMicrophone) ? "Automatic" : SelectedMicrophone;
            var dictation = SelectedDictationModel;
            var meeting = SelectedMeetingModel;
            var template = MeetingSummaryService.NormalizeTemplateName(MeetingSummaryTemplate);
            var summary = SelectedSummaryProvider ?? SummaryProviderDisclosure.For("local");
            var dictationProvider = SelectedDictationProvider;
            var openAIDictationModel = SelectedOpenAIDictationModel;
            var anthropicModel = SelectedAnthropicModel;
            SelectedLiveModel = "";
            SelectedMicrophone = "";
            SelectedDictationModel = "";
            SelectedMeetingModel = "";
            MeetingSummaryTemplate = "";
            SelectedSummaryProvider = null;
            SelectedDictationProvider = null;
            SelectedOpenAIDictationModel = null;
            SelectedAnthropicModel = null;
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
            // A cleared provider picker would show the local disclosure while a hosted provider is in use.
            SelectedDictationProvider = DictationProviderChoices.FirstOrDefault(choice => choice.Id == dictationProvider?.Id)
                ?? DictationProviderChoices[0];
            SelectedOpenAIDictationModel = OpenAIDictationModels.FirstOrDefault(choice => choice.Id == openAIDictationModel?.Id)
                ?? OpenAIDictationModels[0];
            SelectedAnthropicModel = AnthropicModels.FirstOrDefault(choice => choice.Id == anthropicModel?.Id)
                ?? AnthropicModels[0];
        }
        finally
        {
            _tracking = tracking;
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
        _errorField = null;
        StatusMessage = message;
        IsStatusError = isError;
        IsStatusOpen = true;
    }

    // ─── Account-backed notes providers (ChatGPT sign-in, Claude Code) ───────────────────────

    public bool ShowChatGptSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.ChatGpt;
    public bool ShowClaudeCodeSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.ClaudeCode;

    public IReadOnlyList<SettingsChoice> ChatGptModels { get; } =
        ChatGptResponsesClient.Models.Select(model => new SettingsChoice(model.Id, model.Label)).ToList();

    public IReadOnlyList<SettingsChoice> ClaudeCodeModels { get; } =
        ClaudeCodeSummarizer.Models.Select(model => new SettingsChoice(model.Id, model.Label)).ToList();

    [ObservableProperty] public partial SettingsChoice? SelectedChatGptModel { get; set; }
    [ObservableProperty] public partial SettingsChoice? SelectedClaudeCodeModel { get; set; }
    [ObservableProperty] public partial string ClaudeCodeExecutablePath { get; set; } = "";

    [NotifyPropertyChangedFor(nameof(IsChatGptSignedOut))]
    [ObservableProperty] public partial bool IsChatGptSignedIn { get; private set; }
    [ObservableProperty] public partial bool IsChatGptSigningIn { get; private set; }
    [ObservableProperty] public partial string ChatGptAccountStatus { get; private set; } = "Not signed in";
    [ObservableProperty] public partial string ClaudeCodeAccountStatus { get; private set; } = "Checking Claude Code…";

    public bool IsChatGptSignedOut => !IsChatGptSignedIn;

    private CancellationTokenSource? _chatGptSignIn;

    private void RefreshChatGptAccount(string? error = null)
    {
        IsChatGptSignedIn = ChatGptAuthManager.Shared?.IsSignedIn == true;
        ChatGptAccountStatus = error ?? (IsChatGptSigningIn ? "Signing in… finish in your browser."
            : IsChatGptSignedIn ? "Signed in" : "Not signed in");
    }

    [RelayCommand]
    private async Task SignInChatGptAsync()
    {
        if (ChatGptAuthManager.Shared is not { } auth || IsChatGptSigningIn) return;
        _chatGptSignIn = new CancellationTokenSource();
        IsChatGptSigningIn = true;
        RefreshChatGptAccount();
        string? error = null;
        try
        {
            await auth.SignInAsync(uri => global::Windows.System.Launcher.LaunchUriAsync(uri).AsTask(), _chatGptSignIn.Token);
            // As on macOS, a successful sign-in selects ChatGPT for notes; Save keeps the choice.
            SelectedSummaryProvider = SummaryProviders.FirstOrDefault(info => info.Id == SummaryProviderDisclosure.ChatGpt) ?? SelectedSummaryProvider;
        }
        catch (OperationCanceledException) { }
        catch (ChatGptAuthException exception) { error = exception.Message; }
        catch (Exception exception) { error = $"Sign-in failed: {exception.GetType().Name}"; }
        finally
        {
            IsChatGptSigningIn = false;
            _chatGptSignIn = null;
            RefreshChatGptAccount(error);
        }
    }

    [RelayCommand]
    private void SignOutChatGpt()
    {
        _chatGptSignIn?.Cancel();
        ChatGptAuthManager.Shared?.SignOut();
        RefreshChatGptAccount();
    }

    [RelayCommand]
    private async Task CheckClaudeCodeAsync()
    {
        ClaudeCodeAccountStatus = "Checking Claude Code…";
        var path = ClaudeCodeExecutablePath;
        var status = await Task.Run(() => ClaudeCodeSummarizer.CheckStatusAsync(path));
        ClaudeCodeAccountStatus = status switch
        {
            ClaudeCodeStatus.SignedIn => "Connected to Claude Code",
            ClaudeCodeStatus.SignedOut => "Claude Code is installed but not signed in. Run \"claude auth login\" in a terminal, then check again.",
            ClaudeCodeStatus.Unknown => "muesli could not check Claude Code's sign-in status.",
            _ => string.IsNullOrWhiteSpace(path)
                ? "Claude Code was not found on this PC. Install it, or enter the path to claude.exe below."
                : "Claude Code was not found at that path."
        };
    }

    partial void OnSelectedSummaryProviderChanged(SummaryProviderInfo? value)
    {
        OnPropertyChanged(nameof(ShowChatGptSettings));
        OnPropertyChanged(nameof(ShowClaudeCodeSettings));
        OnPropertyChanged(nameof(ShowAnthropicSettings));
        if (value?.Id == SummaryProviderDisclosure.ClaudeCode) _ = CheckClaudeCodeAsync();
        if (value?.Id == SummaryProviderDisclosure.ChatGpt) RefreshChatGptAccount();
    }
    // ─── Anthropic API notes provider ────────────────────────────────────────────────────────

    public bool ShowAnthropicSettings => SelectedSummaryProvider?.Id == SummaryProviderDisclosure.Anthropic;

    public IReadOnlyList<SettingsChoice> AnthropicModels { get; } =
        AnthropicMessagesClient.Models.Select(model => new SettingsChoice(model.Id, model.Label)).ToList();

    [ObservableProperty] public partial SettingsChoice? SelectedAnthropicModel { get; set; }
    [ObservableProperty] public partial string AnthropicApiKey { get; set; } = "";

    // ─── Dictation provider (macOS Speech Recognition → Provider) ────────────────────────────
    // Local is the default. Choosing OpenAI or OpenRouter is what sends dictation audio off this
    // PC, so the disclosure under the picker changes with the choice.

    public IReadOnlyList<SettingsChoice> DictationProviderChoices { get; } =
    [
        new(DictationProviders.Local, "Local (on this PC)"),
        new(DictationProviders.OpenAI, "OpenAI"),
        new(DictationProviders.OpenRouter, "OpenRouter"),
    ];

    public IReadOnlyList<SettingsChoice> OpenAIDictationModels { get; } =
        OpenAIRealtimeProtocol.ModelPresets.Select(model => new SettingsChoice(model, model)).ToList();

    [NotifyPropertyChangedFor(nameof(IsDictationHosted))]
    [NotifyPropertyChangedFor(nameof(ShowOpenAIDictationSettings))]
    [NotifyPropertyChangedFor(nameof(ShowOpenRouterDictationSettings))]
    [NotifyPropertyChangedFor(nameof(DictationDisclosure))]
    [NotifyPropertyChangedFor(nameof(DictationModelRowTitle))]
    [NotifyPropertyChangedFor(nameof(DictationModelHelp))]
    [ObservableProperty] public partial SettingsChoice? SelectedDictationProvider { get; set; }
    [ObservableProperty] public partial SettingsChoice? SelectedOpenAIDictationModel { get; set; }
    [ObservableProperty] public partial string OpenRouterDictationModel { get; set; } = "";

    public bool IsDictationHosted => DictationProviders.IsHosted(SelectedDictationProvider?.Id);
    public bool ShowOpenAIDictationSettings => SelectedDictationProvider?.Id == DictationProviders.OpenAI;
    public bool ShowOpenRouterDictationSettings => SelectedDictationProvider?.Id == DictationProviders.OpenRouter;
    public string DictationDisclosure =>
        DictationProviders.Disclosure(SelectedDictationProvider?.Id, IsFallbackModelInstalled);
    public string DictationModelRowTitle => IsDictationHosted ? "Fallback model" : "Dictation model";
    public string DictationModelHelp => IsFallbackModelInstalled
        ? $"Used on this PC if {DictationProviders.Label(SelectedDictationProvider?.Id)} transcription fails."
        : "Not downloaded, so there is no fallback. Download it in Models.";

    /// <summary>Whether the selected local model can take over when the hosted provider fails.</summary>
    private bool IsFallbackModelInstalled =>
        TranscriptionModelCatalog.Models.FirstOrDefault(model => model.DisplayName == SelectedDictationModel) is { } model &&
        TranscriptionModelReadiness.IsVerified(model);

    partial void OnSelectedDictationModelChanged(string value)
    {
        OnPropertyChanged(nameof(DictationDisclosure));
        OnPropertyChanged(nameof(DictationModelHelp));
    }
    // ─── Connect OpenRouter (browser sign-in, as on macOS) ───────────────────────────────────
    // Approving access in the browser gives muesli its own API key, stored where a pasted key
    // goes, so meeting summaries and dictation both use it.

    [NotifyPropertyChangedFor(nameof(IsOpenRouterDisconnected))]
    [ObservableProperty] public partial bool IsOpenRouterConnected { get; private set; }
    [ObservableProperty] public partial bool IsOpenRouterConnecting { get; private set; }
    [ObservableProperty] public partial string OpenRouterAccountStatus { get; private set; } = "Not connected";

    public bool IsOpenRouterDisconnected => !IsOpenRouterConnected;

    private CancellationTokenSource? _openRouterSignIn;

    partial void OnOpenRouterApiKeyChanged(string value) => RefreshOpenRouterAccount();

    private void RefreshOpenRouterAccount(string? message = null)
    {
        IsOpenRouterConnected = !string.IsNullOrWhiteSpace(OpenRouterApiKey);
        OpenRouterAccountStatus = message ?? (IsOpenRouterConnecting ? "Connecting… approve access in your browser."
            : IsOpenRouterConnected ? "OpenRouter connected" : "Not connected");
    }

    [RelayCommand]
    private async Task ConnectOpenRouterAsync()
    {
        if (IsOpenRouterConnecting) return;
        _openRouterSignIn = new CancellationTokenSource();
        IsOpenRouterConnecting = true;
        RefreshOpenRouterAccount();
        string? error = null;
        try
        {
            var key = await new OpenRouterAuthFlow().SignInAsync(
                uri => global::Windows.System.Launcher.LaunchUriAsync(uri).AsTask(), _openRouterSignIn.Token);
            _pendingText.Remove(nameof(OpenRouterApiKey));
            _settingsContext.SaveProviderKey(SettingsStore.OpenRouterSecretKey, key);
            Untracked(() => OpenRouterApiKey = key);
        }
        catch (OperationCanceledException) { }
        catch (OpenRouterAuthException exception) { error = exception.Message; }
        catch (Exception exception) { error = $"OpenRouter sign-in failed: {exception.GetType().Name}"; }
        finally
        {
            IsOpenRouterConnecting = false;
            _openRouterSignIn = null;
            RefreshOpenRouterAccount(error);
        }
    }

    [RelayCommand]
    private void DisconnectOpenRouter()
    {
        _openRouterSignIn?.Cancel();
        try
        {
            _pendingText.Remove(nameof(OpenRouterApiKey));
            _settingsContext.SaveProviderKey(SettingsStore.OpenRouterSecretKey, "");
            Untracked(() => OpenRouterApiKey = "");
            // As on macOS, only this PC's copy is removed.
            RefreshOpenRouterAccount("Disconnected. The key stays active at OpenRouter until you delete it there.");
        }
        catch (Exception exception)
        {
            RefreshOpenRouterAccount($"Could not remove the OpenRouter key: {exception.Message}");
        }
    }
}

/// <summary>An id with the label a picker shows for it.</summary>
public sealed record SettingsChoice(string Id, string Label)
{
    public override string ToString() => Label;
}
