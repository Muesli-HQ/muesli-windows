using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>A row on the model step (macOS modelCard).</summary>
public sealed partial class OnboardingModelCard(TranscriptionModelDefinition model, bool recommended) : ObservableObject
{
    public TranscriptionModelDefinition Model { get; } = model;
    public string Title => Model.DisplayName;
    public string Size => Model.SizeLabel;
    public string Description => Model.Summary;
    public bool IsRecommended { get; } = recommended;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

/// <summary>A dot in the bottom bar; the current step is filled with the accent.</summary>
public sealed record OnboardingDot(bool IsCurrent);

/// <summary>
/// macOS OnboardingView on Windows: welcome and use case, model, dictation shortcut, microphone,
/// a test with the real shortcut, then meeting summaries. Calendar is left out because Windows has
/// no calendar source. Each choice is saved as it is made so setup can resume where it stopped.
/// </summary>
public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    /// <summary>macOS onboarding candidates: Parakeet Unified first, then the multilingual options.</summary>
    private static readonly string[] OnboardingModelIds =
    [
        "parakeet-unified-en-int8", TranscriptionModelCatalog.DefaultModelId, "whisper-tiny-multilingual",
        "whisper-small-multilingual", "cohere-transcribe-int8-en"
    ];

    private readonly WinUiSettingsContext _settings;
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiModelsContext _models;
    private readonly WinUiComputerUseContext _computerUse;
    private readonly OnboardingProgressStore _progressStore;
    private readonly WindowsMicrophoneAccessService _microphone = new();
    private OnboardingProgress _progress;
    private string? _downloadingModelId;
    private bool _testing;
    private bool _finished;
    private bool _disposed;
    private int _permissionGeneration;

    public OnboardingViewModel(
        WinUiSettingsContext settings,
        WinUiDictationContext dictation,
        WinUiModelsContext models,
        WinUiComputerUseContext computerUse,
        OnboardingProgressStore progressStore)
    {
        _settings = settings;
        _dictation = dictation;
        _models = models;
        _computerUse = computerUse;
        _progressStore = progressStore;
        _progress = progressStore.Load();
        var current = settings.Load();

        UserName = current.UserName;
        UseCase = OnboardingFlow.Parse(current.OnboardingUseCase);
        Hotkey = string.IsNullOrWhiteSpace(current.Hotkey) ? "F8" : current.Hotkey;
        var selectedId = OnboardingModelIds.Contains(current.DictationModelId, StringComparer.OrdinalIgnoreCase) && _progress.Step > 0
            ? current.DictationModelId
            : OnboardingModelIds[0];
        ModelCards = OnboardingModelIds
            .Select(id => TranscriptionModelCatalog.Get(id))
            .OfType<TranscriptionModelDefinition>()
            .Select((model, index) => new OnboardingModelCard(model, index == 0))
            .ToList();
        SelectModel(selectedId);
        ShowMoreModels = !ModelCards[0].IsSelected;
        SummaryBackend = current.MeetingSummaryProvider is SummaryProviderDisclosure.OpenAI or SummaryProviderDisclosure.OpenRouter or SummaryProviderDisclosure.Ollama
            ? current.MeetingSummaryProvider
            : SummaryProviderDisclosure.ChatGpt;
        ChatGptSignedIn = computerUse.ChatGpt.IsSignedIn;

        _models.ModelChanged += OnModelChanged;
        _dictation.Changed += OnDictationChanged;
        Step = OnboardingFlow.NormalizedStep(Math.Clamp(_progress.Step, 0, OnboardingProgress.LastStep), UseCase);
        EnterStep();
    }

    public event EventHandler<bool>? Completed;

    public IReadOnlyList<OnboardingModelCard> ModelCards { get; }
    public OnboardingModelCard RecommendedModel => ModelCards[0];
    public IReadOnlyList<OnboardingModelCard> OtherModels => ModelCards.Skip(1).ToList();

    [ObservableProperty] public partial int Step { get; private set; }
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial OnboardingCapabilities UseCase { get; private set; }
    [ObservableProperty] public partial bool ShowMoreModels { get; set; }
    [ObservableProperty] public partial OnboardingModelCard? SelectedModel { get; private set; }
    [ObservableProperty] public partial string Hotkey { get; private set; } = "F8";
    [ObservableProperty] public partial bool IsRecordingHotkey { get; private set; }
    [ObservableProperty] public partial string HotkeyError { get; private set; } = "";

    // Model download (macOS modelDownloadIndicator)
    [ObservableProperty] public partial bool IsDownloading { get; private set; }
    [ObservableProperty] public partial double? DownloadProgress { get; private set; }
    [ObservableProperty] public partial string DownloadStatus { get; private set; } = "";
    [ObservableProperty] public partial string DownloadError { get; private set; } = "";
    [ObservableProperty] public partial bool IsModelReady { get; private set; }

    // Permissions
    [ObservableProperty] public partial bool MicrophoneGranted { get; private set; }
    [ObservableProperty] public partial bool IsGrantingMicrophone { get; private set; }
    [ObservableProperty] public partial bool IsCheckingMicrophone { get; private set; }
    [ObservableProperty] public partial bool IsConfirmingGrant { get; private set; }
    [ObservableProperty] public partial string MicrophoneError { get; private set; } = "";

    // Dictation test
    [ObservableProperty] public partial bool IsTestRecording { get; private set; }
    [ObservableProperty] public partial bool IsTestTranscribing { get; private set; }
    [ObservableProperty] public partial string TestResult { get; private set; } = "";
    [ObservableProperty] public partial string TestError { get; private set; } = "";

    // Meeting summaries
    [ObservableProperty] public partial string SummaryBackend { get; private set; } = SummaryProviderDisclosure.ChatGpt;
    [ObservableProperty] public partial bool ChatGptSignedIn { get; private set; }
    [ObservableProperty] public partial bool IsSigningIn { get; private set; }
    [ObservableProperty] public partial string SignInError { get; private set; } = "";
    [ObservableProperty] public partial string ApiKey { get; set; } = "";

    public bool ShowWelcome => Step == OnboardingFlow.Welcome;
    public bool ShowModel => Step == OnboardingFlow.Model;
    public bool ShowHotkey => Step == OnboardingFlow.Hotkey;
    public bool ShowPermissions => Step == OnboardingFlow.Permissions;
    public bool ShowDictationTest => Step == OnboardingFlow.DictationTest;
    public bool ShowMeetingSummary => Step == OnboardingFlow.MeetingSummary;

    public bool VoiceNotesSelected => UseCase.HasFlag(OnboardingCapabilities.VoiceNotes);
    public bool DictationSelected => UseCase.HasFlag(OnboardingCapabilities.Dictation);
    public bool MeetingsSelected => UseCase.HasFlag(OnboardingCapabilities.Meetings);
    public bool EverythingSelected => UseCase == OnboardingCapabilities.Everything;
    private bool VoiceNotesOnly => VoiceNotesSelected && !DictationSelected;

    public IReadOnlyList<OnboardingDot> Dots =>
        OnboardingFlow.OrderedSteps(UseCase).Select(step => new OnboardingDot(step == Step)).ToList();
    public bool CanGoBack => OnboardingFlow.CanGoBack(Step, UseCase, TestResult.Length > 0);
    private bool IsLastStep => OnboardingFlow.OrderedSteps(UseCase)[^1] == Step;

    /// <summary>macOS primaryButton titles.</summary>
    public string PrimaryLabel => Step switch
    {
        OnboardingFlow.Model => SelectedModelIsReady ? "Continue" : "Download & Continue",
        _ when IsLastStep => "Finish",
        _ => "Continue"
    };
    public bool PrimaryEnabled => Step switch
    {
        OnboardingFlow.Welcome => UserName.Trim().Length > 0,
        OnboardingFlow.Permissions => MicrophoneGranted,
        OnboardingFlow.DictationTest => TestResult.Length > 0,
        _ => true
    };
    public bool ShowSkip => Step is OnboardingFlow.DictationTest && TestResult.Length == 0 || Step == OnboardingFlow.MeetingSummary;

    // Download indicator
    public bool ShowDownloadIndicator => IsDownloading || DownloadError.Length > 0;
    public string DownloadIndicatorTitle => DownloadError.Length > 0
        ? "Download failed"
        : DownloadProgress is >= 1 ? $"Preparing {SelectedModel?.Title}" : $"Downloading {SelectedModel?.Title}";
    public string DownloadIndicatorDetail => DownloadError.Length > 0
        ? DownloadError
        : DownloadStatus.Length > 0 ? DownloadStatus : "Downloading…";
    public double DownloadPercent => (DownloadProgress ?? 0) * 100;
    public bool IsDownloadIndeterminate => DownloadProgress is null;

    // Hotkey step
    public string HotkeyLabel => string.Join(" + ", Hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    public string HotkeyButtonLabel => IsRecordingHotkey ? "Press a key…" : "Change Shortcut";

    // Permission step
    public string PermissionButtonLabel => IsConfirmingGrant || MicrophoneGranted
        ? "Granted"
        : IsCheckingMicrophone ? "Checking…" : IsGrantingMicrophone ? "Open Settings" : "Grant Permission";
    public bool PermissionButtonEnabled => !IsConfirmingGrant && !MicrophoneGranted && !IsCheckingMicrophone;
    public bool ShowMicrophoneRequest => !MicrophoneGranted || IsConfirmingGrant;
    public bool ShowAllGranted => MicrophoneGranted && !IsConfirmingGrant;

    // Dictation test step
    public string TestTitle => VoiceNotesOnly ? "Test Voice Note" : "Test Dictation";
    public bool ShowTestPreparing => !IsModelReady;
    public bool ShowTestReady => IsModelReady;
    public string TestSubtitle => !IsModelReady
        ? $"Preparing {SelectedModel?.Title} for your first test.\n{(VoiceNotesOnly ? "Voice note test" : "Dictation")} will unlock when the model is ready."
        : VoiceNotesOnly
            ? $"Hold {HotkeyLabel} to record a voice note, then release.\nYour words should appear below."
            : $"Hold {HotkeyLabel} and say something, then release.\nYour words should appear below.";
    public string TestPlaceholder => TestResult.Length > 0 ? TestResult : "Your transcription will appear here...";
    public bool HasTestResult => TestResult.Length > 0;
    public bool ShowTestHint => !IsTestRecording && !IsTestTranscribing && TestResult.Length == 0;
    public string TestHint => $"Hold {HotkeyLabel} to start";
    public string ListeningText => IsTestTranscribing ? "Transcribing…" : $"Listening... release {HotkeyLabel} when done";
    public bool ShowListening => IsTestRecording || IsTestTranscribing;
    public string TestPreparingStatus => DownloadError.Length > 0
        ? DownloadError
        : DownloadStatus.Length > 0 ? DownloadStatus : $"Preparing {SelectedModel?.Title}...";

    // Meeting summary step
    public bool ChatGptSelected => SummaryBackend == SummaryProviderDisclosure.ChatGpt;
    public bool OpenAISelected => SummaryBackend == SummaryProviderDisclosure.OpenAI;
    public bool OpenRouterSelected => SummaryBackend == SummaryProviderDisclosure.OpenRouter;
    public bool OllamaSelected => SummaryBackend == SummaryProviderDisclosure.Ollama;
    public bool ShowKeyEntry => OpenAISelected || OpenRouterSelected;
    public bool ShowChatGptSignIn => ChatGptSelected && !ChatGptSignedIn && !IsSigningIn;
    public bool ShowChatGptSignedIn => ChatGptSelected && ChatGptSignedIn;
    public bool ShowChatGptSigningIn => ChatGptSelected && IsSigningIn;
    public string KeyPlaceholder => OpenRouterSelected ? "sk-or-..." : "sk-...";
    public string KeyCaption => OpenRouterSelected
        ? "Paste an OpenRouter API key. It is kept in Windows Credential Manager."
        : "Paste an OpenAI API key. It is kept in Windows Credential Manager.";
    public string KeyStatus => ApiKey.Trim().Length == 0 ? "No API key" : "Key entered";
    public bool HasKey => ApiKey.Trim().Length > 0;

    partial void OnUserNameChanged(string value)
    {
        Save(settings => settings with { UserName = value.Trim() });
        OnPropertyChanged(nameof(PrimaryEnabled));
    }

    partial void OnApiKeyChanged(string value)
    {
        OnPropertyChanged(nameof(KeyStatus));
        OnPropertyChanged(nameof(HasKey));
    }

    [RelayCommand]
    private void ToggleCapability(string name)
    {
        var capability = Enum.Parse<OnboardingCapabilities>(name);
        var updated = capability == OnboardingCapabilities.Everything
            ? UseCase == OnboardingCapabilities.Everything ? OnboardingCapabilities.Dictation : OnboardingCapabilities.Everything
            : OnboardingFlow.Toggle(UseCase, capability);
        UseCase = updated;
        Save(settings => settings with { OnboardingUseCase = OnboardingFlow.Format(updated) });
        foreach (var property in new[] { nameof(VoiceNotesSelected), nameof(DictationSelected), nameof(MeetingsSelected), nameof(EverythingSelected), nameof(Dots) })
            OnPropertyChanged(property);
    }

    [RelayCommand]
    private void ChooseModel(OnboardingModelCard? card)
    {
        if (card is null) return;
        SelectModel(card.Model.Id);
    }

    [RelayCommand]
    private void ToggleMoreModels() => ShowMoreModels = !ShowMoreModels;

    [RelayCommand]
    private void Back()
    {
        var steps = OnboardingFlow.OrderedSteps(UseCase);
        var index = steps.IndexOf(Step);
        if (index <= 0 || !CanGoBack) return;
        GoTo(steps[index - 1]);
    }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (!PrimaryEnabled) return;
        switch (Step)
        {
            case OnboardingFlow.Model:
                StartDownload();
                break;
            case OnboardingFlow.Hotkey:
                ApplyHotkey();
                break;
            case OnboardingFlow.MeetingSummary:
                SaveSummaryChoice();
                break;
        }
        if (IsLastStep) await FinishAsync();
        else Next();
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (Step == OnboardingFlow.MeetingSummary && !ChatGptSignedIn && ChatGptSelected)
        {
            // macOS falls back to its default; Windows keeps summaries on this PC until a provider is set up.
            Save(settings => settings with { MeetingSummaryProvider = SummaryProviderDisclosure.Local });
        }
        else if (Step == OnboardingFlow.MeetingSummary)
        {
            SaveSummaryChoice();
        }
        if (IsLastStep) await FinishAsync();
        else Next();
    }

    [RelayCommand]
    private void ToggleHotkeyRecording()
    {
        IsRecordingHotkey = !IsRecordingHotkey;
        HotkeyError = "";
    }

    /// <summary>Called by the window with the captured gesture ("Ctrl+Shift+D", "F8", …).</summary>
    public void ApplyCapturedHotkey(string gesture)
    {
        IsRecordingHotkey = false;
        try
        {
            var parsed = HotkeyGestureParser.Parse(gesture);
            if (HotkeyGestureParser.IsEscape(parsed.VirtualKey))
                throw new InvalidOperationException("Escape is reserved for cancelling an active dictation.");
            var computerUse = HotkeyGestureParser.Parse(_settings.Load().ComputerUseHotkey).DisplayName;
            if (parsed.DisplayName.Equals(computerUse, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{computerUse} is the Computer Use shortcut. Choose a different one.");
            if (!HotkeyConflictProbe.IsAdvisoryRegistrationAvailable(parsed.DisplayName))
                throw new InvalidOperationException("Windows reports that this shortcut is already registered by another app.");
            Hotkey = parsed.DisplayName;
            HotkeyError = "";
            Save(settings => settings with { Hotkey = parsed.DisplayName });
            _dictation.RegisterHotkey();
        }
        catch (Exception exception)
        {
            HotkeyError = exception.Message;
        }
    }

    public void CancelHotkeyRecording() => IsRecordingHotkey = false;

    /// <summary>
    /// Windows asks for microphone access the first time an app records, so "Grant Permission"
    /// records a short, discarded sample. If access is off, the button opens Windows Settings.
    /// </summary>
    [RelayCommand]
    private async Task GrantMicrophoneAsync()
    {
        if (IsGrantingMicrophone && !MicrophoneGranted)
        {
            OpenMicrophoneSettings();
            return;
        }
        IsGrantingMicrophone = true;
        await CheckMicrophoneAsync();
    }

    [RelayCommand]
    private void OpenMicrophoneSettings()
    {
        IsGrantingMicrophone = true;
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch (Exception exception) { MicrophoneError = exception.Message; }
        NotifyPermission();
    }

    /// <summary>macOS polls permissions; Windows re-checks when the user comes back to setup.</summary>
    public void NotifyActivated()
    {
        if (Step == OnboardingFlow.Permissions && IsGrantingMicrophone && !MicrophoneGranted && !IsCheckingMicrophone)
            _ = CheckMicrophoneAsync();
    }

    [RelayCommand]
    private async Task SignInWithChatGptAsync()
    {
        IsSigningIn = true;
        SignInError = "";
        NotifySummary();
        await _computerUse.SignInToChatGptAsync();
        ChatGptSignedIn = _computerUse.ChatGpt.IsSignedIn;
        if (!ChatGptSignedIn) SignInError = "ChatGPT sign-in did not finish. Try again.";
        IsSigningIn = false;
        NotifySummary();
    }

    [RelayCommand]
    private void ChooseSummaryBackend(string backend)
    {
        SummaryBackend = backend;
        ApiKey = "";
        NotifySummary();
    }

    [RelayCommand]
    private void RetryDownload()
    {
        DownloadError = "";
        StartDownload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _models.ModelChanged -= OnModelChanged;
        _dictation.Changed -= OnDictationChanged;
        StopTest();
    }

    private void Next()
    {
        var steps = OnboardingFlow.OrderedSteps(UseCase);
        var index = steps.IndexOf(Step);
        if (index >= 0 && index < steps.Count - 1) GoTo(steps[index + 1]);
    }

    private void GoTo(int step)
    {
        StopTest();
        IsRecordingHotkey = false;
        Step = step;
        _progress = _progress with { Step = step, Deferred = false };
        _progressStore.Save(_progress);
        EnterStep();
    }

    private void EnterStep()
    {
        if (Step == OnboardingFlow.Permissions) _ = PreflightMicrophoneAsync();
        if (Step == OnboardingFlow.DictationTest)
        {
            if (!SelectedModelIsReady && _downloadingModelId is null) StartDownload();
            StartTestIfReady();
        }
        if (Step == OnboardingFlow.MeetingSummary) ChatGptSignedIn = _computerUse.ChatGpt.IsSignedIn;
        foreach (var property in new[]
                 {
                     nameof(ShowWelcome), nameof(ShowModel), nameof(ShowHotkey), nameof(ShowPermissions),
                     nameof(ShowDictationTest), nameof(ShowMeetingSummary), nameof(Dots), nameof(CanGoBack)
                 })
            OnPropertyChanged(property);
        NotifyPrimary();
        NotifyTest();
        NotifySummary();
    }

    private async Task FinishAsync()
    {
        if (_finished) return;
        _finished = true;
        StopTest();
        _progress = _progress with { Completed = true, Deferred = false, LastStatus = "" };
        _progressStore.Save(_progress);
        Save(settings => settings with
        {
            OnboardingCompleted = true,
            OnboardingUseCase = OnboardingFlow.Format(UseCase),
            UserName = UserName.Trim()
        });
        _dictation.PrepareMicrophone();
        await Task.CompletedTask;
        Completed?.Invoke(this, true);
    }

    private void SelectModel(string id)
    {
        foreach (var card in ModelCards) card.IsSelected = card.Model.Id.Equals(id, StringComparison.OrdinalIgnoreCase);
        SelectedModel = ModelCards.FirstOrDefault(card => card.IsSelected) ?? ModelCards[0];
        SelectedModel.IsSelected = true;
        DownloadError = "";
        RefreshModelReady();
        NotifyPrimary();
    }

    private bool SelectedModelIsReady =>
        SelectedModel is { } card &&
        _models.Snapshots().FirstOrDefault(snapshot => snapshot.Model.Id.Equals(card.Model.Id, StringComparison.OrdinalIgnoreCase)) is { } snapshot &&
        snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;

    /// <summary>macOS startDownload: select the model for dictation (and meetings), then download in the background.</summary>
    private void StartDownload()
    {
        if (SelectedModel is not { } card) return;
        var id = card.Model.Id;
        Save(settings => settings with { DictationModelId = id, FinalMeetingModelId = id });
        if (SelectedModelIsReady || _downloadingModelId is not null) return;
        _downloadingModelId = id;
        IsDownloading = true;
        DownloadProgress = null;
        DownloadStatus = "";
        DownloadError = "";
        NotifyDownload();
        var progress = new Progress<ModelDownloadProgress>(value => App.UiDispatcher.TryEnqueue(() =>
        {
            if (_downloadingModelId != id) return;
            DownloadProgress = value.Percent is int percent ? percent / 100d : null;
            DownloadStatus = value.DisplayText;
            NotifyDownload();
        }));
        _ = Task.Run(async () =>
        {
            string error = "";
            try { await _models.PrepareAsync(id, progress, CancellationToken.None); }
            catch (Exception exception) { error = exception.Message; }
            App.UiDispatcher.TryEnqueue(() =>
            {
                _downloadingModelId = null;
                IsDownloading = false;
                DownloadError = error;
                RefreshModelReady();
                NotifyDownload();
                StartTestIfReady();
            });
        });
    }

    private void RefreshModelReady()
    {
        IsModelReady = SelectedModelIsReady;
        NotifyTest();
    }

    private void ApplyHotkey()
    {
        Save(settings => settings with { Hotkey = Hotkey });
        _dictation.RegisterHotkey();
    }

    private void SaveSummaryChoice()
    {
        var backend = SummaryBackend;
        Save(settings => settings with { MeetingSummaryProvider = backend });
        var key = ApiKey.Trim();
        if (key.Length == 0) return;
        var current = _settings.Load();
        try
        {
            _settings.SaveProviderKeys(
                backend == SummaryProviderDisclosure.OpenAI ? key : current.ResolvedOpenAIApiKey,
                backend == SummaryProviderDisclosure.OpenRouter ? key : current.ResolvedOpenRouterApiKey);
        }
        catch (Exception exception)
        {
            SignInError = exception.Message;
        }
    }

    private async Task PreflightMicrophoneAsync()
    {
        // A grant from an earlier run shows straight away, as macOS reads the current status.
        if (MicrophoneGranted || !_settings.Load().OnboardingCompleted && !IsGrantingMicrophone) return;
        await CheckMicrophoneAsync();
    }

    private async Task CheckMicrophoneAsync()
    {
        IsCheckingMicrophone = true;
        MicrophoneError = "";
        NotifyPermission();
        var generation = ++_permissionGeneration;
        try
        {
            var current = _settings.Load();
            var probe = await _microphone.ProbeAsync(current.MicrophoneName);
            if (generation != _permissionGeneration) return;
            if (probe.IsUsable)
            {
                MicrophoneGranted = true;
                IsConfirmingGrant = true;
                NotifyPermission();
                await Task.Delay(900);
                IsConfirmingGrant = false;
                NotifyPermission();
                if (Step == OnboardingFlow.Permissions && generation == _permissionGeneration)
                {
                    if (IsLastStep) await FinishAsync();
                    else Next();
                }
                return;
            }
            MicrophoneError = probe.Failure == MicrophoneProbeFailure.Denied
                ? "Microphone access is off for Muesli. Turn it on in Windows Settings, then come back."
                : string.IsNullOrWhiteSpace(probe.Message) ? "Muesli could not record from the microphone." : probe.Message;
        }
        catch (Exception exception)
        {
            MicrophoneError = exception.Message;
        }
        finally
        {
            IsCheckingMicrophone = false;
            NotifyPermission();
        }
    }

    private void StartTestIfReady()
    {
        if (Step != OnboardingFlow.DictationTest || !IsModelReady || _testing) return;
        _testing = true;
        _dictation.BeginShortcutTest((text, error) =>
        {
            IsTestTranscribing = false;
            if (error is not null) TestError = error;
            else if (text.Length == 0) TestError = "No speech detected. Try again.";
            else
            {
                TestError = "";
                TestResult = text;
                // macOS moves on to meeting summaries by itself after a successful test.
                if (MeetingsSelected)
                {
                    var heard = text;
                    _ = Task.Delay(900).ContinueWith(_ => App.UiDispatcher.TryEnqueue(() =>
                    {
                        if (Step == OnboardingFlow.DictationTest && TestResult == heard) Next();
                    }));
                }
            }
            NotifyTest();
            NotifyPrimary();
            OnPropertyChanged(nameof(CanGoBack));
        });
    }

    private void StopTest()
    {
        if (!_testing) return;
        _testing = false;
        _dictation.EndShortcutTest();
        IsTestRecording = false;
        IsTestTranscribing = false;
    }

    private void OnDictationChanged(object? sender, EventArgs e)
    {
        if (!_testing) return;
        var wasRecording = IsTestRecording;
        IsTestRecording = _dictation.IsRecording;
        if (wasRecording && !IsTestRecording) IsTestTranscribing = true;
        if (IsTestRecording) TestError = "";
        NotifyTest();
    }

    private void OnModelChanged(object? sender, string modelId) => App.UiDispatcher.TryEnqueue(() =>
    {
        RefreshModelReady();
        NotifyPrimary();
        StartTestIfReady();
    });

    private void Save(Func<MuesliSettings, MuesliSettings> change) => _settings.Save(change(_settings.Load()));

    private void NotifyPrimary()
    {
        OnPropertyChanged(nameof(PrimaryLabel));
        OnPropertyChanged(nameof(PrimaryEnabled));
        OnPropertyChanged(nameof(ShowSkip));
    }

    private void NotifyDownload()
    {
        foreach (var property in new[]
                 {
                     nameof(ShowDownloadIndicator), nameof(DownloadIndicatorTitle), nameof(DownloadIndicatorDetail),
                     nameof(DownloadPercent), nameof(IsDownloadIndeterminate), nameof(TestPreparingStatus)
                 })
            OnPropertyChanged(property);
    }

    private void NotifyPermission()
    {
        foreach (var property in new[]
                 {
                     nameof(PermissionButtonLabel), nameof(PermissionButtonEnabled), nameof(ShowMicrophoneRequest),
                     nameof(ShowAllGranted)
                 })
            OnPropertyChanged(property);
        NotifyPrimary();
    }

    private void NotifyTest()
    {
        foreach (var property in new[]
                 {
                     nameof(TestTitle), nameof(ShowTestPreparing), nameof(ShowTestReady), nameof(TestSubtitle),
                     nameof(TestPlaceholder), nameof(HasTestResult), nameof(ShowTestHint), nameof(TestHint),
                     nameof(ListeningText), nameof(ShowListening), nameof(TestPreparingStatus)
                 })
            OnPropertyChanged(property);
    }

    private void NotifySummary()
    {
        foreach (var property in new[]
                 {
                     nameof(ChatGptSelected), nameof(OpenAISelected), nameof(OpenRouterSelected), nameof(OllamaSelected),
                     nameof(ShowKeyEntry), nameof(ShowChatGptSignIn), nameof(ShowChatGptSignedIn), nameof(ShowChatGptSigningIn),
                     nameof(KeyPlaceholder), nameof(KeyCaption), nameof(KeyStatus), nameof(HasKey)
                 })
            OnPropertyChanged(property);
    }

    partial void OnHotkeyChanged(string value)
    {
        OnPropertyChanged(nameof(HotkeyLabel));
        NotifyTest();
    }

    partial void OnIsRecordingHotkeyChanged(bool value) => OnPropertyChanged(nameof(HotkeyButtonLabel));
}
