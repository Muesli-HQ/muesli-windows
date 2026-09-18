using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void OpenAIApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
{
    if (_updatingSecretBoxes || sender is not PasswordBox passwordBox)
    {
        return;
    }
    SaveProtectedSecret(SettingsStore.OpenAISecretKey, passwordBox.Password, isOpenAi: true);
}

private void OpenRouterApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
{
    if (_updatingSecretBoxes || sender is not PasswordBox passwordBox)
    {
        return;
    }
    SaveProtectedSecret(SettingsStore.OpenRouterSecretKey, passwordBox.Password, isOpenAi: false);
}

private void ClearOpenAIApiKey_Click(object sender, RoutedEventArgs e) =>
    ClearProtectedSecret(SettingsStore.OpenAISecretKey, OpenAIApiKeyBox, isOpenAi: true);

private void ClearOpenRouterApiKey_Click(object sender, RoutedEventArgs e) =>
    ClearProtectedSecret(SettingsStore.OpenRouterSecretKey, OpenRouterApiKeyBox, isOpenAi: false);

private void BrowseHookExecutable_Click(object sender, RoutedEventArgs e)
{
    var dialog = new Microsoft.Win32.OpenFileDialog
    {
        Title = "Choose a post-meeting executable",
        Filter = "Windows executable (*.exe)|*.exe",
        CheckFileExists = true,
        Multiselect = false
    };
    if (dialog.ShowDialog(_shell.Window) == true)
    {
        PostMeetingHookExecutablePath = Path.GetFullPath(dialog.FileName);
        PostMeetingAutomationStatusText = "Hook executable selected. It remains disabled until you enable it.";
    }
}
private MuesliSettings CurrentSettingsSnapshot()
{
    return new MuesliSettings
    {
        Hotkey = SelectedHotkey,
        UserName = UserName,
        PasteBehavior = SelectedPasteBehavior,
        DictationModelId = SelectedTranscriptionModel.Id,
        FinalMeetingModelId = SelectedFinalMeetingModel.Id,
        LiveMeetingModelId = SelectedLiveMeetingModel.Id,
        LiveTranscriptOwnership = LiveTranscriptOwnershipDescriptor.SettingValueFor(SelectedOwnershipMode),
        ShowLiveWaveformOnHover = ShowLiveWaveformOnHover,
        OnboardingCompleted = _onboardingCompleted,
        LastCompletedFeatureTourVersion = _lastCompletedFeatureTourVersion,
        EnableDoubleTapDictation = EnableDoubleTapDictation,
        HotkeyTriggerThresholdMs = _hotkeyTriggerThresholdMs,
        RecordingColorHex = _recordingColorHex,
        RemoveFillerWords = RemoveFillerWords,
        EnableLocalCleanup = EnableLocalCleanup,
        StartAtLogin = StartAtLogin,
        AutoMeetingDetectionEnabled = AutoMeetingDetectionEnabled,
        MeetingSummaryProvider = SelectedSummaryProvider,
        OllamaEndpoint = _ollamaEndpoint,
        OllamaModel = _ollamaModel,
        MeetingSummaryTemplate = SelectedSummaryTemplate,
        MeetingSummaryPromptOverride = CustomMeetingTemplates.FirstOrDefault(template =>
            template.Name.Equals(SelectedSummaryTemplate, StringComparison.OrdinalIgnoreCase))?.Prompt ?? "",
        OpenDashboardOnLaunch = OpenDashboardOnLaunch,
        SaveMeetingRecordings = SaveMeetingRecordings,
        PostMeetingHookEnabled = PostMeetingHookEnabled,
        PostMeetingHookExecutablePath = PostMeetingHookExecutablePath,
        PostMeetingHookTranscriptPolicy = HookTranscriptPolicySetting(SelectedHookTranscriptPolicy),
        PostMeetingHookTimeoutSeconds = PostMeetingHookTimeoutSeconds,
        PostMeetingHookMaxAttempts = PostMeetingHookMaxAttempts,
        AutoExportMarkdownEnabled = AutoExportMarkdownEnabled,
        AutoExportMarkdownDirectory = AutoExportMarkdownDirectory,
        AutoExportMarkdownContent = AutoExportContentSetting(SelectedAutoExportContent),
        ComputerUseEnabled = ComputerUseEnabled,
        ComputerUsePlannerProvider = ComputerUseProviderSetting(SelectedComputerUsePlannerProvider),
        ComputerUsePlannerModel = ComputerUsePlannerModel,
        ComputerUsePlannerTimeoutSeconds = ComputerUsePlannerTimeoutSeconds,
        ComputerUsePerActionTimeoutSeconds = ComputerUsePerActionTimeoutSeconds,
        ComputerUseMaximumActionCount = ComputerUseMaximumActionCount,
        ComputerUseAllowedApplications = ComputerUseAllowedApplications,
        ComputerUseAllowedBrowserDomains = ComputerUseAllowedBrowserDomains,
        ComputerUseIncludeWindowText = ComputerUseIncludeWindowText,
        ComputerUseIncludeScreenshots = ComputerUseIncludeScreenshots,
        ComputerUseIncludeBrowserPageText = ComputerUseIncludeBrowserPageText,
        ComputerUseBrowserInterface = ComputerUseBrowserInterfaceSetting(SelectedComputerUseBrowserInterface),
        ComputerUseBrowserEndpoint = ComputerUseBrowserEndpoint,
        ShowFloatingIndicator = ShowFloatingIndicator,
        SoundEnabled = SoundEnabled,
        IndicatorAnchor = SelectedIndicatorPosition,
        DictionarySuggestions = DictionarySuggestions.ToList(),
        ResolvedOpenAIApiKey = OpenAIApiKey,
        OpenAIModel = OpenAIModel,
        ResolvedOpenRouterApiKey = OpenRouterApiKey,
        OpenRouterModel = OpenRouterModel,
        Theme = _theme,
        MicrophoneName = SelectedMicrophone,
        IndicatorLeft = _indicatorLeft,
        IndicatorTop = _indicatorTop
    };
}

private PostMeetingAutomationOptions CurrentPostMeetingAutomationOptions() => new()
{
    HookEnabled = PostMeetingHookEnabled,
    HookExecutablePath = PostMeetingHookExecutablePath,
    AutoExportEnabled = AutoExportMarkdownEnabled,
    AutoExportDirectory = AutoExportMarkdownDirectory,
    AutoExportMode = AutoExportContentSetting(SelectedAutoExportContent) switch
    {
        "transcript" => MeetingExportMode.Transcript,
        "full-meeting" => MeetingExportMode.FullMeeting,
        _ => MeetingExportMode.Notes
    },
    TranscriptPolicy = HookTranscriptPolicySetting(SelectedHookTranscriptPolicy) switch
    {
        "inline" => PostMeetingTranscriptPolicy.Inline,
        "auto-export-path" => PostMeetingTranscriptPolicy.AutoExportPath,
        _ => PostMeetingTranscriptPolicy.MetadataOnly
    },
    Timeout = TimeSpan.FromSeconds(Math.Clamp(PostMeetingHookTimeoutSeconds, 1, 600)),
    RetryPolicy = new PostMeetingRetryPolicy
    {
        MaxAttempts = Math.Clamp(PostMeetingHookMaxAttempts, 1, 3),
        Delay = TimeSpan.FromMilliseconds(500)
    }
};

private static string DescribeAutomationResult(PostMeetingAutomationResult result)
{
    var export = result.Export.Completed
        ? "Markdown exported. "
        : result.Export.Requested
            ? "Markdown export failed. "
            : "";
    return result.Status switch
    {
        PostMeetingAutomationStatus.Disabled => "Automation is disabled; no process was launched and no export was written.",
        PostMeetingAutomationStatus.Succeeded => $"{export}Post-meeting automation completed safely.",
        PostMeetingAutomationStatus.TimedOut => $"{export}The hook timed out and its process tree was terminated.",
        PostMeetingAutomationStatus.Cancelled => $"{export}Post-meeting automation was cancelled and its process tree was terminated.",
        PostMeetingAutomationStatus.InvalidConfiguration => $"{export}{result.Error ?? "Automation is disabled until its path is fixed."}",
        _ => $"{export}{result.Error ?? "Post-meeting automation failed; the meeting remains saved."}"
    };
}
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
    SetBrush("ScrollBarThumbBrush", light ? "#C5C5CB" : "#3A3D42");
    SetBrush("ScrollBarThumbHoverBrush", light ? "#A8A8B0" : "#4E5258");
    SetBrush("WarningBannerBackgroundBrush", light ? "#FFFDF0D5" : "#332A1A");
    SetBrush("WarningBannerBorderBrush", light ? "#FFF4D03F" : "#66FF9F43");
    SetBrush("WarningBannerForegroundBrush", light ? "#FFB45309" : "#FF9F43");
    _shell.Window.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["BackgroundDeepBrush"];
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

private void BrowseAutoExportDirectory_Click(object sender, RoutedEventArgs e)
{
    using var dialog = new System.Windows.Forms.FolderBrowserDialog
    {
        Description = "Choose the user-owned folder for automatic Markdown exports",
        UseDescriptionForTitle = true,
        ShowNewFolderButton = true,
        SelectedPath = IsValidAutoExportDirectory(AutoExportMarkdownDirectory)
            ? AutoExportMarkdownDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
    };
    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
    {
        AutoExportMarkdownDirectory = Path.GetFullPath(dialog.SelectedPath);
        PostMeetingAutomationStatusText = "Export destination selected. It remains disabled until you enable it.";
    }
}

private void TestPostMeetingHook_Click(object sender, RoutedEventArgs e) => _ = TestPostMeetingHookAsync();

private async Task TestPostMeetingHookAsync()
{
    var validation = PostMeetingAutomationService.ValidateExecutable(PostMeetingHookExecutablePath);
    if (validation is not null)
    {
        PostMeetingAutomationStatusText = validation;
        System.Windows.MessageBox.Show(_shell.Window, validation, "Post-meeting hook", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    PostMeetingAutomationStatusText = "Running a metadata-only synthetic hook test…";
    try
    {
        var result = await _postMeetingAutomationService.TestHookAsync(
            CurrentPostMeetingAutomationOptions(),
            _applicationShutdownCancellation.Token);
        PostMeetingAutomationStatusText = DescribeAutomationResult(result);
        var detail = $"Status: {result.Status}\nAttempts: {result.Attempts}\nExit code: {result.ExitCode?.ToString() ?? "none"}";
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            detail += $"\n\nstdout (redacted, bounded):\n{result.StandardOutput}";
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            detail += $"\n\nstderr (redacted, bounded):\n{result.StandardError}";
        if (!string.IsNullOrWhiteSpace(result.Error)) detail += $"\n\n{result.Error}";
        System.Windows.MessageBox.Show(
            _shell.Window,
            detail,
            "Post-meeting hook test",
            MessageBoxButton.OK,
            result.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
    catch (Exception exception)
    {
        PostMeetingAutomationStatusText = $"Hook test failed ({exception.GetType().Name}).";
        System.Windows.MessageBox.Show(
            _shell.Window,
            PostMeetingAutomationStatusText,
            "Post-meeting hook test",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}

private void SaveProtectedSecret(string key, string value, bool isOpenAi)
{
    try
    {
        _settingsStore.SaveSecret(key, value);
        if (isOpenAi)
        {
            _openAIApiKey = value;
            OnPropertyChanged(nameof(OpenAIApiKeyStatus));
        }
        else
        {
            _openRouterApiKey = value;
            OnPropertyChanged(nameof(OpenRouterApiKeyStatus));
        }
        SaveSettings();
    }
    catch (Exception exception)
    {
        _logService.Error("Could not update a protected summary-provider credential.", exception);
        _toastNotificationService.Show("Key not saved", "Windows Credential Manager was unavailable.", ToastState.Error, 4200);
    }
}

private void ClearProtectedSecret(string key, PasswordBox passwordBox, bool isOpenAi)
{
    _updatingSecretBoxes = true;
    try
    {
        passwordBox.Clear();
        _settingsStore.SaveSecret(key, null);
        if (isOpenAi)
        {
            _openAIApiKey = "";
            OnPropertyChanged(nameof(OpenAIApiKeyStatus));
        }
        else
        {
            _openRouterApiKey = "";
            OnPropertyChanged(nameof(OpenRouterApiKeyStatus));
        }
        SaveSettings();
        DictationStatus = "Stored API key cleared";
    }
    catch (Exception exception)
    {
        _logService.Error("Could not clear a protected summary-provider credential.", exception);
        _toastNotificationService.Show("Key not cleared", "Windows Credential Manager was unavailable.", ToastState.Error, 4200);
    }
    finally
    {
        _updatingSecretBoxes = false;
    }
}

public ObservableCollection<string> SummaryProviders { get; } = [.. SummaryProviderDisclosure.AvailableIds];
public ObservableCollection<string> IndicatorPositions { get; } = ["Top Left", "Top Center", "Top Right", "Middle Left", "Middle Right", "Bottom Left", "Bottom Center", "Bottom Right", "Custom"];
public ObservableCollection<string> ThemeOptions { get; } = ["Light", "Dark"];
public ObservableCollection<string> HookTranscriptPolicies { get; } = ["Metadata only", "Inline transcript", "Auto-export path"];
public ObservableCollection<string> AutoExportContentOptions { get; } = ["Notes", "Transcript", "Full meeting"];

/// <summary>Shown next to the provider picker so the user knows before recording whether the transcript leaves the machine.</summary>
public string SummaryProviderDisclosureText =>
    SummaryProviderDisclosure.DisclosureFor(SelectedSummaryProvider, _ollamaEndpoint);

public string OllamaEndpoint
{
    get => _ollamaEndpoint;
    set
    {
        if (!SetField(ref _ollamaEndpoint, string.IsNullOrWhiteSpace(value) ? "http://localhost:11434" : value.Trim())) return;
        OnPropertyChanged(nameof(SummaryProviderDisclosureText));
        SaveSettings();
    }
}

public string OllamaModel
{
    get => _ollamaModel;
    set
    {
        if (SetField(ref _ollamaModel, string.IsNullOrWhiteSpace(value) ? "llama3.1:8b" : value.Trim()))
        {
            SaveSettings();
        }
    }
}

public ObservableCollection<string> SummaryTemplates { get; } = new(MeetingSummaryService.BuiltInTemplateNames);

public string SelectedSummaryProvider
{
    get => _selectedSummaryProvider;
    set
    {
        if (SetField(ref _selectedSummaryProvider, value))
        {
            OnPropertyChanged(nameof(SummaryProviderDisclosureText));
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
        if (SetField(ref _selectedSummaryTemplate, normalized)) SaveSettings();
    }
}

public bool OpenDashboardOnLaunch
{
    get => _openDashboardOnLaunch;
    set { if (SetField(ref _openDashboardOnLaunch, value)) SaveSettings(); }
}

public bool SaveMeetingRecordings
{
    get => _saveMeetingRecordings;
    set { if (SetField(ref _saveMeetingRecordings, value)) SaveSettings(); }
}

public bool PostMeetingHookEnabled
{
    get => _postMeetingHookEnabled;
    set
    {
        if (value && !IsValidHookExecutable(PostMeetingHookExecutablePath))
        {
            PostMeetingAutomationStatusText = "Choose an existing .exe before enabling the hook.";
            value = false;
        }
        if (SetField(ref _postMeetingHookEnabled, value)) SaveSettings();
    }
}

public string PostMeetingHookExecutablePath
{
    get => _postMeetingHookExecutablePath;
    set
    {
        var normalized = value?.Trim() ?? "";
        if (!SetField(ref _postMeetingHookExecutablePath, normalized)) return;
        if (_postMeetingHookEnabled && !IsValidHookExecutable(normalized))
        {
            _postMeetingHookEnabled = false;
            OnPropertyChanged(nameof(PostMeetingHookEnabled));
            PostMeetingAutomationStatusText = "The hook was disabled because its executable is unavailable.";
        }
        SaveSettings();
    }
}

public string SelectedHookTranscriptPolicy
{
    get => _selectedHookTranscriptPolicy;
    set
    {
        var normalized = HookTranscriptPolicies.Contains(value) ? value : "Metadata only";
        if (SetField(ref _selectedHookTranscriptPolicy, normalized)) SaveSettings();
    }
}

public int PostMeetingHookTimeoutSeconds
{
    get => _postMeetingHookTimeoutSeconds;
    set { if (SetField(ref _postMeetingHookTimeoutSeconds, Math.Clamp(value, 1, 600))) SaveSettings(); }
}

public int PostMeetingHookMaxAttempts
{
    get => _postMeetingHookMaxAttempts;
    set { if (SetField(ref _postMeetingHookMaxAttempts, Math.Clamp(value, 1, 3))) SaveSettings(); }
}

public bool AutoExportMarkdownEnabled
{
    get => _autoExportMarkdownEnabled;
    set
    {
        if (value && !IsValidAutoExportDirectory(AutoExportMarkdownDirectory))
        {
            PostMeetingAutomationStatusText = "Choose an absolute export folder before enabling automatic Markdown export.";
            value = false;
        }
        if (SetField(ref _autoExportMarkdownEnabled, value)) SaveSettings();
    }
}

public string AutoExportMarkdownDirectory
{
    get => _autoExportMarkdownDirectory;
    set
    {
        var normalized = value?.Trim() ?? "";
        if (!SetField(ref _autoExportMarkdownDirectory, normalized)) return;
        if (_autoExportMarkdownEnabled && !IsValidAutoExportDirectory(normalized))
        {
            _autoExportMarkdownEnabled = false;
            OnPropertyChanged(nameof(AutoExportMarkdownEnabled));
            PostMeetingAutomationStatusText = "Automatic Markdown export was disabled because its destination is invalid.";
        }
        SaveSettings();
    }
}

public string SelectedAutoExportContent
{
    get => _selectedAutoExportContent;
    set
    {
        var normalized = AutoExportContentOptions.Contains(value) ? value : "Notes";
        if (SetField(ref _selectedAutoExportContent, normalized)) SaveSettings();
    }
}

public string PostMeetingAutomationStatusText
{
    get => _postMeetingAutomationStatus;
    private set => SetField(ref _postMeetingAutomationStatus, value);
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

public bool SoundEnabled
{
    get => _soundEnabled;
    set
    {
        if (SetField(ref _soundEnabled, value))
        {
            _dictationCoordinator.SoundFeedback.Enabled = value;
            SaveSettings();
        }
    }
}

public int HotkeyTriggerThresholdMs
{
    get => _hotkeyTriggerThresholdMs;
    set
    {
        var next = HotkeyTriggerTiming.ClampMilliseconds(value);
        if (SetField(ref _hotkeyTriggerThresholdMs, next))
        {
            SaveSettings();
        }
    }
}

public string RecordingColorHex
{
    get => _recordingColorHex;
    set
    {
        var next = string.IsNullOrWhiteSpace(value) ? "1e1e2e" : value.Trim().TrimStart('#');
        if (!SetField(ref _recordingColorHex, next))
        {
            return;
        }

        _toastNotificationService.SetRecordingAccent(next);
        SaveSettings();
    }
}

public string SelectedIndicatorPosition
{
    get => _selectedIndicatorPosition;
    set
    {
        var next = IndicatorPositions.Contains(value) ? value : "Middle Right";
        if (!SetField(ref _selectedIndicatorPosition, next)) return;
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

public string OpenAIApiKey => _openAIApiKey;

public string OpenAIApiKeyStatus => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
    ? "Configured via environment"
    : string.IsNullOrWhiteSpace(_openAIApiKey) ? "Not configured" : "Configured securely";

public string OpenAIModel
{
    get => _openAIModel;
    set { if (SetField(ref _openAIModel, value)) SaveSettings(); }
}

public string OpenRouterApiKey => _openRouterApiKey;

public string OpenRouterApiKeyStatus => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"))
    ? "Configured via environment"
    : string.IsNullOrWhiteSpace(_openRouterApiKey) ? "Not configured" : "Configured securely";

public string OpenRouterModel
{
    get => _openRouterModel;
    set { if (SetField(ref _openRouterModel, value)) SaveSettings(); }
}

public bool AutoMeetingDetectionEnabled
{
    get => _autoMeetingDetectionEnabled;
    set
    {
        if (!SetField(ref _autoMeetingDetectionEnabled, value)) return;
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
