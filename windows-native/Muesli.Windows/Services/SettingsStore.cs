using System.IO;
namespace Muesli.Windows.Services;

public sealed class SettingsStore
{
    private const string OpenAISecretKey = "OpenAI API Key";
    private const string OpenRouterSecretKey = "OpenRouter API Key";
    private const string CustomSummarySecretKey = "Custom Summary API Key";

    private readonly string _settingsPath;
    private readonly ISecretStore _secretStore;
    private readonly AtomicJsonFile _json;

    public SettingsStore() : this(new WindowsCredentialSecretStore(), null)
    {
    }

    public SettingsStore(ISecretStore secretStore, string? settingsPath = null)
    {
        _secretStore = secretStore;
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "windows-settings.json");
        _json = new AtomicJsonFile(warning => RecoveryWarnings.Add(warning));
    }

    public List<string> RecoveryWarnings { get; } = [];

    public MuesliSettings Load()
    {
        var loaded = _json.Load(_settingsPath, new MuesliSettings()).Value;
        var openAIKey = _secretStore.Read(OpenAISecretKey);
        var openRouterKey = _secretStore.Read(OpenRouterSecretKey);
        var customSummaryKey = _secretStore.Read(CustomSummarySecretKey);
        var hasPlaintextSecrets = !string.IsNullOrWhiteSpace(loaded.OpenAIApiKey) ||
                                  !string.IsNullOrWhiteSpace(loaded.OpenRouterApiKey);

        if (string.IsNullOrWhiteSpace(openAIKey) && !string.IsNullOrWhiteSpace(loaded.OpenAIApiKey))
        {
            _secretStore.Write(OpenAISecretKey, loaded.OpenAIApiKey);
            openAIKey = loaded.OpenAIApiKey;
        }
        if (string.IsNullOrWhiteSpace(openRouterKey) && !string.IsNullOrWhiteSpace(loaded.OpenRouterApiKey))
        {
            _secretStore.Write(OpenRouterSecretKey, loaded.OpenRouterApiKey);
            openRouterKey = loaded.OpenRouterApiKey;
        }

        if (hasPlaintextSecrets)
        {
            SaveSanitized(loaded);
        }

        return loaded with
        {
            OpenAIApiKey = openAIKey ?? "",
            OpenRouterApiKey = openRouterKey ?? "",
            CustomSummaryApiKey = customSummaryKey ?? ""
        };
    }

    public void Save(MuesliSettings settings)
    {
        _secretStore.Write(OpenAISecretKey, settings.OpenAIApiKey);
        _secretStore.Write(OpenRouterSecretKey, settings.OpenRouterApiKey);
        _secretStore.Write(CustomSummarySecretKey, settings.CustomSummaryApiKey);
        SaveSanitized(settings);
    }

    private void SaveSanitized(MuesliSettings settings)
    {
        _json.Save(
            _settingsPath,
            settings with { OpenAIApiKey = "", OpenRouterApiKey = "", CustomSummaryApiKey = "" },
            AtomicJsonSaveMode.PrivacySensitive);
    }
}

public sealed record MuesliSettings
{
    public string UserName { get; init; } = "";
    public string Hotkey { get; init; } = "F8";
    public string AsrEngine { get; init; } = "whisper";
    public string ModelProfile { get; init; } = "base";
    public string DictationLanguage { get; init; } = "en";
    public string MeetingAsrEngine { get; init; } = "whisper";
    public string MeetingModelProfile { get; init; } = "base";
    public string? MeetingMicrophoneName { get; init; }
    public string PasteBehavior { get; init; } = "active-app";
    public bool OnboardingCompleted { get; init; }
    public bool PostProcessingEnabled { get; init; }
    public bool RemoveFillerWords { get; init; } = true;
    public bool EnableDoubleTapDictation { get; init; }
    public string PostProcessingPrompt { get; init; } =
        "Clean up speech-to-text transcription. Only make changes when there is a clear error. If the text is already correct, output it exactly as-is.\n\nYou may fix obvious misspellings, remove filler words (um, uh, like), apply deletion commands, and format numbered or bullet lists when dictated.";
    public bool StartAtLogin { get; init; }
    public bool AutoMeetingDetectionEnabled { get; init; } = true;
    public string MeetingSummaryProvider { get; init; } = "local";
    public string MeetingSummaryTemplate { get; init; } = "standard";
    public string MeetingSummaryPromptOverride { get; init; } = "";
    public bool OpenDashboardOnLaunch { get; init; } = true;
    public bool SaveMeetingRecordings { get; init; } = true;
    public string RecordingSavePolicy { get; init; } = "";
    public bool AutoExportMeetings { get; init; }
    public string AutoExportDirectory { get; init; } = "";
    public bool ShowFloatingIndicator { get; init; } = true;
    public string IndicatorAnchor { get; init; } = "Top Center";
    public string OpenAIApiKey { get; init; } = "";
    public string OpenAIModel { get; init; } = "gpt-5.4-mini";
    public string OpenRouterApiKey { get; init; } = "";
    public string OpenRouterModel { get; init; } = "stepfun/step-3.5-flash:free";
    public string CustomSummaryEndpoint { get; init; } = "http://localhost:11434/v1/chat/completions";
    public string CustomSummaryModel { get; init; } = "llama3.2";
    public string CustomSummaryApiKey { get; init; } = "";
    public int MeetingSummaryRetryCount { get; init; } = 2;
    public string Theme { get; init; } = "dark";
    public string? MicrophoneName { get; init; }
    public double? IndicatorLeft { get; init; }
    public double? IndicatorTop { get; init; }
    public bool CrashReportingEnabled { get; init; }
    public bool CrashReportingPromptShown { get; init; }
}
