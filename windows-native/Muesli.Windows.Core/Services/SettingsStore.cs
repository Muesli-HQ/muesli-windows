using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

public sealed class SettingsStore
{
    public const string OpenAISecretKey = "openai-api-key";
    public const string OpenRouterSecretKey = "openrouter-api-key";
    public const string CustomLlmSecretKey = "custom-llm-api-key";

    private readonly string _settingsPath;
    private readonly AtomicJsonFile _json;
    private readonly ISecretStore _secretStore;
    private bool _saveSuppressedToPreserveUnreadableOrFutureData;

    public SettingsStore(
        string? settingsPath = null,
        ISecretStore? secretStore = null,
        Action<string>? report = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath ??
            Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().SettingsPath);
        _secretStore = secretStore ?? throw new ArgumentNullException(
            nameof(secretStore),
            "A platform secret-store adapter is required.");
        _json = new AtomicJsonFile(report);
    }

    public string? LastWarning { get; private set; }

    public MuesliSettings Load()
    {
        _saveSuppressedToPreserveUnreadableOrFutureData = false;
        var result = _json.Load(_settingsPath, new MuesliSettings());
        LastWarning = result.Warning;
        var settings = result.Value;
        if (settings.SchemaVersion > MuesliSettings.CurrentSchemaVersion)
        {
            _saveSuppressedToPreserveUnreadableOrFutureData = true;
            LastWarning =
                $"Settings schema {settings.SchemaVersion} is newer than this app supports. The file was left unchanged and settings changes will not be saved by this version.";
            return new MuesliSettings();
        }
        // Single normalization boundary: explicit JSON nulls, wrong-typed enum-like strings, invalid
        // colours/endpoints and null nested collections must never leak into the product object.
        settings = NormalizeAfterDeserialization(settings);
        var settingsMigrated = settings.SchemaVersion < MuesliSettings.CurrentSchemaVersion ||
                               !string.IsNullOrWhiteSpace(settings.LegacyTranscriptionModelIdForMigration) ||
                               !string.IsNullOrWhiteSpace(settings.LegacyAsrEngineForMigration) ||
                               !string.IsNullOrWhiteSpace(settings.LegacyModelProfileForMigration) ||
                               !string.IsNullOrWhiteSpace(settings.LegacyDictationModelProfileForMigration);
        var legacyModelId = ResolveLegacyModelId(settings);
        var migratedModelId = TranscriptionModelCatalog.NormalizeId(legacyModelId);
        settings = settings with
        {
            SchemaVersion = MuesliSettings.CurrentSchemaVersion,
            DictationModelId = NormalizePersistedModelId(
                settingsMigrated && !string.IsNullOrWhiteSpace(legacyModelId) ? migratedModelId : settings.DictationModelId),
            FinalMeetingModelId = NormalizePersistedModelId(
                settingsMigrated && !string.IsNullOrWhiteSpace(legacyModelId) ? migratedModelId : settings.FinalMeetingModelId),
            LiveMeetingModelId = NormalizeStreamingModelId(settings.LiveMeetingModelId),
            LiveTranscriptOwnership = NormalizeOwnership(settings.LiveTranscriptOwnership),
            PostMeetingHookExecutablePath = settings.PostMeetingHookExecutablePath?.Trim() ?? "",
            PostMeetingHookTranscriptPolicy = NormalizeHookTranscriptPolicy(settings.PostMeetingHookTranscriptPolicy),
            PostMeetingHookTimeoutSeconds = Math.Clamp(settings.PostMeetingHookTimeoutSeconds, 1, 600),
            PostMeetingHookMaxAttempts = Math.Clamp(settings.PostMeetingHookMaxAttempts, 1, 3),
            AutoExportMarkdownDirectory = settings.AutoExportMarkdownDirectory?.Trim() ?? "",
            AutoExportMarkdownContent = NormalizeAutoExportContent(settings.AutoExportMarkdownContent),
            ComputerUsePlannerProvider = NormalizeComputerUseProvider(settings.ComputerUsePlannerProvider),
            ComputerUsePlannerModel = settings.ComputerUsePlannerModel?.Trim() ?? "",
            ComputerUsePlannerTimeoutSeconds = Math.Clamp(settings.ComputerUsePlannerTimeoutSeconds, 5, 120),
            ComputerUsePerActionTimeoutSeconds = Math.Clamp(settings.ComputerUsePerActionTimeoutSeconds, 1, 30),
            ComputerUseMaximumActionCount = Math.Clamp(settings.ComputerUseMaximumActionCount, 1, 20),
            ComputerUseAllowedApplications = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedApplications),
            ComputerUseAllowedBrowserDomains = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedBrowserDomains),
            ComputerUseBrowserInterface = NormalizeComputerUseBrowserInterface(settings.ComputerUseBrowserInterface),
            ComputerUseBrowserEndpoint = NormalizeLoopbackEndpoint(settings.ComputerUseBrowserEndpoint),
            LegacyTranscriptionModelIdForMigration = null,
            LegacyAsrEngineForMigration = null,
            LegacyModelProfileForMigration = null,
            LegacyDictationModelProfileForMigration = null
        };
        var plaintextOpenAIKey = settings.PlaintextOpenAIApiKeyForMigration;
        var plaintextOpenRouterKey = settings.PlaintextOpenRouterApiKeyForMigration;
        try
        {
            var migrated = settingsMigrated;
            if (!string.IsNullOrWhiteSpace(settings.PlaintextOpenAIApiKeyForMigration))
            {
                _secretStore.Write(OpenAISecretKey, settings.PlaintextOpenAIApiKeyForMigration);
                migrated = true;
            }
            if (!string.IsNullOrWhiteSpace(settings.PlaintextOpenRouterApiKeyForMigration))
            {
                _secretStore.Write(OpenRouterSecretKey, settings.PlaintextOpenRouterApiKeyForMigration);
                migrated = true;
            }

            settings = settings with
            {
                PlaintextOpenAIApiKeyForMigration = null,
                PlaintextOpenRouterApiKeyForMigration = null,
                ResolvedOpenAIApiKey = _secretStore.Read(OpenAISecretKey) ?? "",
                ResolvedOpenRouterApiKey = _secretStore.Read(OpenRouterSecretKey) ?? "",
                ResolvedCustomLlmApiKey = _secretStore.Read(CustomLlmSecretKey) ?? ""
            };
            if (migrated)
            {
                SaveSanitized(settings, AtomicJsonSaveMode.PrivacySensitive);
            }
            return settings;
        }
        catch (Exception)
        {
            _saveSuppressedToPreserveUnreadableOrFutureData = true;
            LastWarning = "Secure provider-key migration could not be completed. Existing plaintext keys were left in place so migration can retry after restart, and were not logged.";
            return settings with
            {
                ResolvedOpenAIApiKey = plaintextOpenAIKey ?? "",
                ResolvedOpenRouterApiKey = plaintextOpenRouterKey ?? ""
            };
        }
    }

    public void Save(MuesliSettings settings)
    {
        if (_saveSuppressedToPreserveUnreadableOrFutureData)
        {
            return;
        }
        SaveSanitized(settings, AtomicJsonSaveMode.Recoverable);
    }

    private void SaveSanitized(MuesliSettings settings, AtomicJsonSaveMode saveMode)
    {
        settings = NormalizeAfterDeserialization(settings);
        var sanitized = settings with
        {
            PlaintextOpenAIApiKeyForMigration = null,
            PlaintextOpenRouterApiKeyForMigration = null,
            LegacyTranscriptionModelIdForMigration = null,
            LegacyAsrEngineForMigration = null,
            LegacyModelProfileForMigration = null,
            LegacyDictationModelProfileForMigration = null,
            SchemaVersion = MuesliSettings.CurrentSchemaVersion,
            DictationModelId = NormalizePersistedModelId(settings.DictationModelId),
            FinalMeetingModelId = NormalizePersistedModelId(settings.FinalMeetingModelId),
            LiveMeetingModelId = NormalizeStreamingModelId(settings.LiveMeetingModelId),
            LiveTranscriptOwnership = NormalizeOwnership(settings.LiveTranscriptOwnership),
            PostMeetingHookExecutablePath = settings.PostMeetingHookExecutablePath?.Trim() ?? "",
            PostMeetingHookTranscriptPolicy = NormalizeHookTranscriptPolicy(settings.PostMeetingHookTranscriptPolicy),
            PostMeetingHookTimeoutSeconds = Math.Clamp(settings.PostMeetingHookTimeoutSeconds, 1, 600),
            PostMeetingHookMaxAttempts = Math.Clamp(settings.PostMeetingHookMaxAttempts, 1, 3),
            AutoExportMarkdownDirectory = settings.AutoExportMarkdownDirectory?.Trim() ?? "",
            AutoExportMarkdownContent = NormalizeAutoExportContent(settings.AutoExportMarkdownContent),
            ComputerUsePlannerProvider = NormalizeComputerUseProvider(settings.ComputerUsePlannerProvider),
            ComputerUsePlannerModel = settings.ComputerUsePlannerModel?.Trim() ?? "",
            ComputerUsePlannerTimeoutSeconds = Math.Clamp(settings.ComputerUsePlannerTimeoutSeconds, 5, 120),
            ComputerUsePerActionTimeoutSeconds = Math.Clamp(settings.ComputerUsePerActionTimeoutSeconds, 1, 30),
            ComputerUseMaximumActionCount = Math.Clamp(settings.ComputerUseMaximumActionCount, 1, 20),
            ComputerUseAllowedApplications = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedApplications),
            ComputerUseAllowedBrowserDomains = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedBrowserDomains),
            ComputerUseBrowserInterface = NormalizeComputerUseBrowserInterface(settings.ComputerUseBrowserInterface),
            ComputerUseBrowserEndpoint = NormalizeLoopbackEndpoint(settings.ComputerUseBrowserEndpoint),
            ResolvedOpenAIApiKey = "",
            ResolvedOpenRouterApiKey = "",
            ResolvedCustomLlmApiKey = ""
        };
        _json.Save(_settingsPath, sanitized, saveMode);
    }

    public void SaveSecret(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _secretStore.Delete(key);
        }
        else
        {
            _secretStore.Write(key, value.Trim());
        }
    }

    public string ReadSecret(string key) => _secretStore.Read(key) ?? "";
    public bool IsSecretConfigured(string key) => _secretStore.IsConfigured(key);

    /// <summary>
    /// The authoritative post-deserialization normalization boundary. Every non-nullable
    /// <see cref="MuesliSettings"/> field is coerced here, so explicit JSON <c>null</c>, malformed
    /// values and null nested collections cannot reach product code. Covered by a reflection contract
    /// test that fails when a new non-nullable property is added without normalization.
    /// </summary>
    internal static MuesliSettings NormalizeAfterDeserialization(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            UserName = settings.UserName ?? "",
            Hotkey = DefaultIfBlank(settings.Hotkey, "F8").Trim(),
            PasteBehavior = DefaultIfBlank(settings.PasteBehavior, "active-app").Trim(),
            DictationModelId = NormalizePersistedModelId(settings.DictationModelId),
            FinalMeetingModelId = NormalizePersistedModelId(settings.FinalMeetingModelId),
            LiveTranscriptOwnership = NormalizeOwnership(settings.LiveTranscriptOwnership),
            RecordingColorHex = NormalizeColorHex(settings.RecordingColorHex),
            MeetingSummaryProvider = DefaultIfBlank(settings.MeetingSummaryProvider, "local").Trim(),
            MeetingSummaryTemplate = DefaultIfBlank(settings.MeetingSummaryTemplate, "standard").Trim(),
            MeetingSummaryPromptOverride = settings.MeetingSummaryPromptOverride ?? "",
            PostMeetingHookExecutablePath = settings.PostMeetingHookExecutablePath?.Trim() ?? "",
            PostMeetingHookTranscriptPolicy = NormalizeHookTranscriptPolicy(settings.PostMeetingHookTranscriptPolicy),
            PostMeetingHookTimeoutSeconds = Math.Clamp(settings.PostMeetingHookTimeoutSeconds, 1, 600),
            PostMeetingHookMaxAttempts = Math.Clamp(settings.PostMeetingHookMaxAttempts, 1, 3),
            AutoExportMarkdownDirectory = settings.AutoExportMarkdownDirectory?.Trim() ?? "",
            AutoExportMarkdownContent = NormalizeAutoExportContent(settings.AutoExportMarkdownContent),
            ComputerUsePlannerProvider = NormalizeComputerUseProvider(settings.ComputerUsePlannerProvider),
            ComputerUsePlannerModel = settings.ComputerUsePlannerModel?.Trim() ?? "",
            ComputerUsePlannerTimeoutSeconds = Math.Clamp(settings.ComputerUsePlannerTimeoutSeconds, 5, 120),
            ComputerUsePerActionTimeoutSeconds = Math.Clamp(settings.ComputerUsePerActionTimeoutSeconds, 1, 30),
            ComputerUseMaximumActionCount = Math.Clamp(settings.ComputerUseMaximumActionCount, 1, 20),
            ComputerUseAllowedApplications = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedApplications),
            ComputerUseAllowedBrowserDomains = NormalizeDelimitedAllowlist(settings.ComputerUseAllowedBrowserDomains),
            ComputerUseBrowserInterface = NormalizeComputerUseBrowserInterface(settings.ComputerUseBrowserInterface),
            ComputerUseBrowserEndpoint = NormalizeLoopbackEndpoint(settings.ComputerUseBrowserEndpoint),
            IndicatorAnchor = DefaultIfBlank(settings.IndicatorAnchor, "Middle Right").Trim(),
            DictionarySuggestions = NormalizeDictionarySuggestions(settings.DictionarySuggestions),
            ModelLanguages = NormalizeModelLanguages(settings.ModelLanguages),
            ExecutionProvider = NormalizeExecutionProvider(settings.ExecutionProvider),
            CleanupModelId = CleanupModelCatalog.Normalize(settings.CleanupModelId),
            OpenAIModel = DefaultIfBlank(settings.OpenAIModel, "gpt-5.4-mini").Trim(),
            OpenRouterModel = DefaultIfBlank(settings.OpenRouterModel, "stepfun/step-3.5-flash:free").Trim(),
            OllamaEndpoint = NormalizeOllamaEndpoint(settings.OllamaEndpoint),
            OllamaModel = DefaultIfBlank(settings.OllamaModel, "llama3.1:8b").Trim(),
            LmStudioEndpoint = NormalizeOllamaEndpoint(settings.LmStudioEndpoint, "http://localhost:1234"),
            LmStudioModel = settings.LmStudioModel?.Trim() ?? "",
            CustomLlmEndpoint = NormalizeAbsoluteHttpEndpoint(settings.CustomLlmEndpoint, "http://localhost:8080/v1/chat/completions"),
            CustomLlmModel = settings.CustomLlmModel?.Trim() ?? "",
            UpdateManifestUrl = NormalizeOptionalHttpUrl(settings.UpdateManifestUrl),
            UpdateSignatureUrl = NormalizeOptionalHttpUrl(settings.UpdateSignatureUrl),
            UpdatePublisherPublicKeySha256 = NormalizeOptionalSha256(settings.UpdatePublisherPublicKeySha256),
            Theme = DefaultIfBlank(settings.Theme, "dark").Trim(),
            ResolvedOpenAIApiKey = settings.ResolvedOpenAIApiKey ?? "",
            ResolvedOpenRouterApiKey = settings.ResolvedOpenRouterApiKey ?? "",
            ResolvedCustomLlmApiKey = settings.ResolvedCustomLlmApiKey ?? "",
        };
    }

    private static string DefaultIfBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string NormalizeColorHex(string? value)
    {
        var candidate = (value ?? "").Trim().TrimStart('#');
        return candidate.Length == 6 && candidate.All(Uri.IsHexDigit) ? candidate.ToLowerInvariant() : "1e1e2e";
    }

    private static string NormalizeOllamaEndpoint(string? value, string fallback = "http://localhost:11434")
    {
        var candidate = (value ?? "").Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) &&
               (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
            ? endpoint.GetLeftPart(UriPartial.Authority)
            : fallback;
    }

    private static string NormalizeAbsoluteHttpEndpoint(string? value, string fallback)
    {
        var candidate = (value ?? "").Trim();
        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) &&
               (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
            ? candidate
            : fallback;
    }

    /// <summary>An empty value disables the update check; a malformed URL does the same, fail-closed.</summary>
    private static string NormalizeOptionalHttpUrl(string? value)
    {
        var candidate = (value ?? "").Trim();
        if (candidate.Length == 0)
        {
            return "";
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) &&
               (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
            ? candidate
            : "";
    }

    private static string NormalizeOptionalSha256(string? value)
    {
        var candidate = (value ?? "").Trim().ToLowerInvariant();
        return candidate.Length == 64 && candidate.All(Uri.IsHexDigit) ? candidate : "";
    }

    private static IReadOnlyList<DictionarySuggestion> NormalizeDictionarySuggestions(
        IReadOnlyList<DictionarySuggestion>? suggestions) =>
        suggestions is null
            ? []
            : suggestions
                .Where(item => item is not null)
                .Select(item => new DictionarySuggestion(item.Observed ?? "", item.Replacement ?? ""))
                .Where(item => !string.IsNullOrWhiteSpace(item.Observed))
                .ToArray();

    /// <summary>
    /// Keeps only known model ids with a language the model actually supports, coercing invalid or
    /// obsolete choices to that model's default.
    /// </summary>
    private static IReadOnlyDictionary<string, string> NormalizeModelLanguages(
        IReadOnlyDictionary<string, string>? languages)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (languages is null)
        {
            return result;
        }

        foreach (var pair in languages)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            if (!TranscriptionModelCatalog.TryGet(pair.Key, out var model)) continue;
            result[model.Id] = ModelLanguageSupport.Normalize(model, pair.Value);
        }

        return result;
    }

    private static string NormalizePersistedModelId(string? modelId) =>
        TranscriptionModelCatalog.TryGet(modelId, out var model)
            ? model.Id
            : TranscriptionModelCatalog.DefaultModelId;

    /// <summary>
    /// Coerces a persisted execution-provider string to a value the runtime understands. An
    /// obsolete or invalid value falls back to Automatic rather than selecting a different provider
    /// behind the user's back; <c>directml</c> is preserved so the UI can explain why it is not used.
    /// </summary>
    private static string NormalizeExecutionProvider(string? value) =>
        ExecutionProviderService.ToSettingValue(ExecutionProviderService.Parse(value));

    private static string? NormalizeStreamingModelId(string? modelId) =>
        StreamingModelCatalog.Get(modelId)?.Id;

    private static string NormalizeOwnership(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "unified-live-final" => "unified-live-final",
        _ => "preview-only"
    };

    private static string NormalizeHookTranscriptPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "inline" => "inline",
        "auto-export-path" => "auto-export-path",
        _ => "metadata-only"
    };

    private static string NormalizeAutoExportContent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "transcript" => "transcript",
        "full-meeting" => "full-meeting",
        _ => "notes"
    };

    private static string NormalizeComputerUseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "openai" => "openai",
        _ => "none"
    };

    private static string NormalizeComputerUseBrowserInterface(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "loopback-devtools" => "loopback-devtools",
        _ => "none"
    };

    private static string NormalizeDelimitedAllowlist(string? value) => string.Join("; ",
        (value ?? "")
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(entry => entry.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string NormalizeLoopbackEndpoint(string? value)
    {
        var candidate = value?.Trim() ?? "";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !endpoint.IsLoopback)
        {
            return "http://127.0.0.1:9222";
        }
        return endpoint.GetLeftPart(UriPartial.Authority);
    }

    private static string? ResolveLegacyModelId(MuesliSettings settings)
    {
        foreach (var candidate in new[]
                 {
                     settings.LegacyTranscriptionModelIdForMigration,
                     settings.LegacyAsrEngineForMigration
                 })
        {
            if (TranscriptionModelCatalog.TryGet(candidate, out var model))
            {
                return model.Id;
            }
        }

        var profile = settings.LegacyDictationModelProfileForMigration ?? settings.LegacyModelProfileForMigration;
        return profile?.Trim().ToLowerInvariant() switch
        {
            "tiny" or "tiny.en" => "whisper-tiny-en",
            "small" or "small.en" => "whisper-small-en",
            "medium" or "medium.en" or "large-v3" => "whisper-medium-en",
            _ => settings.LegacyTranscriptionModelIdForMigration ?? settings.LegacyAsrEngineForMigration
        };
    }
}

public sealed record MuesliSettings
{
    public const int CurrentSchemaVersion = 11;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string UserName { get; init; } = "";
    public string Hotkey { get; init; } = "F8";
    public string PasteBehavior { get; init; } = "active-app";
    public string DictationModelId { get; init; } = TranscriptionModelCatalog.DefaultModelId;
    public string FinalMeetingModelId { get; init; } = TranscriptionModelCatalog.DefaultModelId;
    public string? LiveMeetingModelId { get; init; }
    public string LiveTranscriptOwnership { get; init; } = "preview-only";

    /// <summary>
    /// Requested execution provider: <c>auto</c>, <c>cpu</c>, <c>cuda</c>, or <c>directml</c>.
    /// Changing between the CPU and CUDA native runtimes needs a restart because the two builds
    /// cannot be swapped safely inside a loaded process.
    /// </summary>
    public string ExecutionProvider { get; init; } = "auto";

    /// <summary>Selected local cleanup model id, or empty when none is chosen.</summary>
    public string CleanupModelId { get; init; } = "";

    public bool ShowLiveWaveformOnHover { get; init; }
    [JsonPropertyName("TranscriptionModelId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyTranscriptionModelIdForMigration { get; init; }
    [JsonPropertyName("AsrEngine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyAsrEngineForMigration { get; init; }
    [JsonPropertyName("ModelProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyModelProfileForMigration { get; init; }
    [JsonPropertyName("DictationModelProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyDictationModelProfileForMigration { get; init; }
    public bool OnboardingCompleted { get; init; }
    public int LastCompletedFeatureTourVersion { get; init; }
    public bool EnableDoubleTapDictation { get; init; }
    public int HotkeyTriggerThresholdMs { get; init; } = HotkeyTriggerTiming.DefaultThresholdMilliseconds;
    public string RecordingColorHex { get; init; } = "1e1e2e";
    public bool RemoveFillerWords { get; init; } = true;
    public bool EnableLocalCleanup { get; init; }
    public bool StartAtLogin { get; init; }
    public bool AutoMeetingDetectionEnabled { get; init; } = true;
    public string MeetingSummaryProvider { get; init; } = "local";
    public string MeetingSummaryTemplate { get; init; } = "standard";
    public string MeetingSummaryPromptOverride { get; init; } = "";
    public bool OpenDashboardOnLaunch { get; init; } = true;
    public bool SaveMeetingRecordings { get; init; } = true;
    public bool PostMeetingHookEnabled { get; init; }
    public string PostMeetingHookExecutablePath { get; init; } = "";
    public string PostMeetingHookTranscriptPolicy { get; init; } = "metadata-only";
    public int PostMeetingHookTimeoutSeconds { get; init; } = 30;
    public int PostMeetingHookMaxAttempts { get; init; } = 2;
    public bool AutoExportMarkdownEnabled { get; init; }
    public string AutoExportMarkdownDirectory { get; init; } = "";
    public string AutoExportMarkdownContent { get; init; } = "notes";

    /// <summary>
    /// Asks for an automatic PDF beside the Markdown copy. PDF still honors the EXP-01 QuestPDF
    /// release gate, so enabling this while the gate is closed fails closed and reports why.
    /// </summary>
    public bool AutoExportPdfEnabled { get; init; }
    public bool ComputerUseEnabled { get; init; }
    public string ComputerUsePlannerProvider { get; init; } = "none";
    public string ComputerUsePlannerModel { get; init; } = "";
    public int ComputerUsePlannerTimeoutSeconds { get; init; } = 30;
    public int ComputerUsePerActionTimeoutSeconds { get; init; } = 10;
    public int ComputerUseMaximumActionCount { get; init; } = 5;
    public string ComputerUseAllowedApplications { get; init; } = "";
    public string ComputerUseAllowedBrowserDomains { get; init; } = "";
    public bool ComputerUseIncludeWindowText { get; init; }
    public bool ComputerUseIncludeScreenshots { get; init; }
    public bool ComputerUseIncludeBrowserPageText { get; init; }
    public string ComputerUseBrowserInterface { get; init; } = "none";
    public string ComputerUseBrowserEndpoint { get; init; } = "http://127.0.0.1:9222";
    public bool ShowFloatingIndicator { get; init; } = true;
    public bool SoundEnabled { get; init; } = true;
    public string IndicatorAnchor { get; init; } = "Middle Right";
    public IReadOnlyList<DictionarySuggestion> DictionarySuggestions { get; init; } = [];

    /// <summary>Per-model language code selections, keyed by model id. Empty uses each model's default.</summary>
    public IReadOnlyDictionary<string, string> ModelLanguages { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("OpenAIApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PlaintextOpenAIApiKeyForMigration { get; init; }
    [JsonIgnore]
    public string ResolvedOpenAIApiKey { get; init; } = "";
    public string OpenAIModel { get; init; } = "gpt-5.4-mini";
    [JsonPropertyName("OpenRouterApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? PlaintextOpenRouterApiKeyForMigration { get; init; }
    [JsonIgnore]
    public string ResolvedOpenRouterApiKey { get; init; } = "";
    public string OpenRouterModel { get; init; } = "stepfun/step-3.5-flash:free";

    /// <summary>
    /// Ollama runs on the user's own machine, so it needs no credential and, on a loopback
    /// endpoint, no "transcript leaves this machine" disclosure. A non-loopback endpoint is a
    /// network destination and is disclosed as such — see <see cref="SummaryProviderDisclosure"/>.
    /// </summary>
    public string OllamaEndpoint { get; init; } = "http://localhost:11434";
    public string OllamaModel { get; init; } = "llama3.1:8b";

    /// <summary>
    /// LM Studio serves the OpenAI chat-completions contract on the user's own machine, so it needs
    /// no credential and, on a loopback endpoint, no off-machine disclosure.
    /// </summary>
    public string LmStudioEndpoint { get; init; } = "http://localhost:1234";
    public string LmStudioModel { get; init; } = "";

    /// <summary>
    /// User-supplied OpenAI-compatible chat-completions endpoint. The optional bearer token is kept
    /// in the credential store (<see cref="ResolvedCustomLlmApiKey"/>), never in this JSON.
    /// </summary>
    public string CustomLlmEndpoint { get; init; } = "http://localhost:8080/v1/chat/completions";
    public string CustomLlmModel { get; init; } = "";
    [JsonIgnore]
    public string ResolvedCustomLlmApiKey { get; init; } = "";
    public string Theme { get; init; } = "dark";
    public string? MicrophoneName { get; init; }
    public double? IndicatorLeft { get; init; }
    public double? IndicatorTop { get; init; }
    public bool CrashReportingEnabled { get; init; }
    public bool CrashReportingPromptShown { get; init; }

    /// <summary>
    /// App-side update channel (UPD-01). Empty manifest/signature URLs or an empty publisher pin
    /// disable the check entirely; a configured channel still fails closed unless the manifest
    /// verifies against this pinned RSA public-key SHA-256.
    /// </summary>
    public string UpdateManifestUrl { get; init; } = "";
    public string UpdateSignatureUrl { get; init; } = "";
    public string UpdatePublisherPublicKeySha256 { get; init; } = "";
}
