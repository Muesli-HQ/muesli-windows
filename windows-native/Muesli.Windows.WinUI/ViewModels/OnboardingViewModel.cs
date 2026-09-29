using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public sealed record OnboardingLiveChoice(string? Id, string Label)
{
    public static OnboardingLiveChoice Off { get; } = new(null, "Off");
    public override string ToString() => Label;
}

public sealed record OnboardingStepNavItem(int Index, string Number, string Title, bool IsCurrent, bool IsComplete)
{
    public bool ShowCurrentNumber => IsCurrent && !IsComplete;
    public bool ShowUpcomingNumber => !IsCurrent && !IsComplete;
}

public sealed record OnboardingPermissionItem(
    string Name,
    string Status,
    bool Granted,
    string Help,
    bool CanOpenSettings,
    bool IsLast)
{
    public bool ShowGrant => !Granted && CanOpenSettings;
    public bool ShowGranted => Granted;
    public bool ShowUnavailable => !Granted && !CanOpenSettings;
}

public sealed partial class OnboardingModelRoleItem : ObservableObject
{
    public OnboardingModelRoleItem(string role, string id, bool isLive)
    {
        Role = role;
        Id = id;
        IsLive = isLive;
    }

    public string Role { get; }
    public string Id { get; }
    public bool IsLive { get; }

    [ObservableProperty] public partial string Heading { get; set; } = "";
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsReady { get; set; }
    [ObservableProperty] public partial bool CanPrepare { get; set; }
    [ObservableProperty] public partial bool CanVerify { get; set; }
    [ObservableProperty] public partial bool CanRetry { get; set; }
    [ObservableProperty] public partial bool CanCancel { get; set; }
    [ObservableProperty] public partial int? ProgressPercent { get; set; }

    public double ProgressValue => ProgressPercent ?? 0;
    public bool IsProgressIndeterminate => IsBusy && ProgressPercent is null;
    public string ProgressLabel => ProgressPercent is int value ? $"{value}%" : StatusText;
    public bool HasActions => CanPrepare || CanVerify || CanRetry || CanCancel;
    public string AccessibleName => $"{Role} model {Id}. {StatusText}";

    public void NotifyProgressBindings()
    {
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressLabel));
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

public partial class OnboardingViewModel : ObservableObject, IDisposable
{
    private static readonly string[] HotkeyPresets =
    [
        "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Ctrl+Shift+Space", "Ctrl+Alt+Space", "Ctrl+Shift+D", "Ctrl+Alt+D"
    ];

    private static readonly string[] IndicatorPositions =
    [
        "Top Left", "Top Center", "Top Right",
        "Middle Left", "Middle Right",
        "Bottom Left", "Bottom Center", "Bottom Right", "Custom"
    ];

    private readonly WinUiSettingsContext _settings;
    private readonly IStartupRegistrationService _startup;
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiModelsContext _models;
    private readonly OnboardingProgressStore _progressStore;
    private readonly WindowsMicrophoneAccessService _microphone = new();
    private readonly StreamingModelLifecycleService _liveLifecycle;
    private readonly ObservableCollection<OnboardingModelRoleItem> _modelRoles = [];
    private OnboardingProgress _progress;
    private MuesliSettings _draft;
    private bool _isRendering;
    private bool _disposed;
    private (string Id, bool Live)? _activeModelOperation;
    private CancellationTokenSource? _pipelineCancellation;

    public OnboardingViewModel(
        WinUiSettingsContext settings,
        IStartupRegistrationService startup,
        WinUiDictationContext dictation,
        WinUiModelsContext models,
        OnboardingProgressStore progressStore)
    {
        _settings = settings;
        _startup = startup;
        _dictation = dictation;
        _models = models;
        _progressStore = progressStore;
        _draft = settings.Load();
        _progress = progressStore.Load();
        _liveLifecycle = new StreamingModelLifecycleService(
            () => SelectedLiveModel?.Id,
            _ => Task.CompletedTask);
        _models.ModelChanged += OnOfflineModelChanged;
        _liveLifecycle.ModelChanged += OnLiveModelChanged;

        Microphones = _dictation.ListMicrophones()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Microphones.Count == 0)
        {
            Microphones = [AudioCaptureService.SystemDefaultMicrophone];
        }

        DictationModels = TranscriptionModelCatalog.Models;
        MeetingModels = TranscriptionModelCatalog.Models;
        LiveModels = [OnboardingLiveChoice.Off, .. StreamingModelCatalog.Models.Select(model => new OnboardingLiveChoice(model.Id, model.PickerLabel))];
        Hotkeys = EnsureHotkeyPreset(_draft.Hotkey);
        SummaryProviders = SummaryProviderDisclosure.Available;
        IndicatorPositionChoices = IndicatorPositions;

        ApplyDraftToFields();
        _progress = OnboardingProgressReconciler.Reconcile(
            _progress,
            ToSelection(_draft),
            id => OfflineReady(id),
            id => LiveReady(id));
        _ = RefreshStartupAvailabilityAsync();
        Render();
    }

    [ObservableProperty] public partial int Step { get; private set; }
    [ObservableProperty] public partial string StepLabel { get; private set; } = "";
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Description { get; private set; } = "";
    [ObservableProperty] public partial string Status { get; private set; } = "";
    [ObservableProperty] public partial bool StatusIsError { get; private set; }
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> Microphones { get; private set; } = [];
    [ObservableProperty] public partial string? SelectedMicrophone { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TranscriptionModelDefinition> DictationModels { get; private set; } = [];
    [ObservableProperty] public partial TranscriptionModelDefinition? SelectedDictationModel { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TranscriptionModelDefinition> MeetingModels { get; private set; } = [];
    [ObservableProperty] public partial TranscriptionModelDefinition? SelectedMeetingModel { get; set; }
    [ObservableProperty] public partial IReadOnlyList<OnboardingLiveChoice> LiveModels { get; private set; } = [];
    [ObservableProperty] public partial OnboardingLiveChoice SelectedLiveModel { get; set; } = OnboardingLiveChoice.Off;
    [ObservableProperty] public partial IReadOnlyList<string> Hotkeys { get; private set; } = [];
    [ObservableProperty] public partial string SelectedHotkey { get; set; } = "F8";
    [ObservableProperty] public partial IReadOnlyList<SummaryProviderInfo> SummaryProviders { get; private set; } = [];
    [ObservableProperty] public partial SummaryProviderInfo? SelectedSummaryProvider { get; set; }
    [ObservableProperty] public partial IReadOnlyList<string> IndicatorPositionChoices { get; private set; } = [];
    [ObservableProperty] public partial string SelectedIndicatorPosition { get; set; } = "Middle Right";
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    [ObservableProperty] public partial bool ShowIndicator { get; set; } = true;
    [ObservableProperty] public partial bool IsStartupAvailable { get; private set; } = true;
    [ObservableProperty] public partial string StartupAvailabilityNotice { get; private set; } = "";
    [ObservableProperty] public partial bool CanGoBack { get; private set; }
    [ObservableProperty] public partial bool CanGoNext { get; private set; }
    [ObservableProperty] public partial bool CanFinish { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsPipelineRunning { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<OnboardingStepNavItem> StepNavItems { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<OnboardingPermissionItem> Permissions { get; private set; } = [];
    [ObservableProperty] public partial string MicrophonePolicyHint { get; private set; } = "";
    [ObservableProperty] public partial string CloudKeyStatus { get; private set; } = "";

    public ObservableCollection<OnboardingModelRoleItem> ModelRoles => _modelRoles;
    public int StepCount => OnboardingProgress.LastStep + 1;
    public int ProgressMaximum => OnboardingProgress.LastStep;
    public double ProgressValue => Step;
    public string SummaryDisclosure => SummaryProviderDisclosure.DisclosureFor(SelectedSummaryProvider?.Id, _draft.OllamaEndpoint);
    public bool ShowStartupNotice => !IsStartupAvailable && !string.IsNullOrWhiteSpace(StartupAvailabilityNotice);
    public bool ShowStatus => !string.IsNullOrWhiteSpace(Status);
    public bool ShowName => Step == 0;
    public bool ShowMicrophone => Step == 1;
    public bool ShowModels => Step == 2;
    public bool ShowHotkey => Step == 3;
    public bool ShowPipeline => Step == 4;
    public bool ShowOptional => Step == 5;
    public bool ShowReady => Step == 6;
    public bool MicrophoneReady => _progress.MicrophoneVerified;
    public bool ModelsReady => _progress.ModelsVerified;
    public bool HotkeyReady => _progress.HotkeyVerified;
    public bool PipelineReady => _progress.PipelineVerified;
    public string MicrophoneGateLabel => _progress.MicrophoneVerified ? "ready" : "needs test";
    public string ModelsGateLabel => _progress.ModelsVerified ? "ready" : "needs preparation";
    public string HotkeyGateLabel => _progress.HotkeyVerified ? "ready" : "needs check";
    public string PipelineGateLabel => _progress.PipelineVerified ? "passed" : "needs successful transcript";
    public string PipelineActionLabel => IsPipelineRunning ? "Cancel dictation test" : "Record 3-second dictation test";

    /// <summary>
    /// P8-05: the live-dictation card used to open by repeating the step subtitle verbatim. These
    /// two read back the choices the test will actually use, so the card states the real
    /// configuration instead of restating the heading above it.
    /// </summary>
    public string PipelineMicrophoneLabel =>
        string.IsNullOrWhiteSpace(SelectedMicrophone)
            ? AudioCaptureService.SystemDefaultMicrophone
            : SelectedMicrophone;

    /// <inheritdoc cref="PipelineMicrophoneLabel" />
    public string PipelineModelLabel =>
        SelectedDictationModel?.DisplayName
        ?? (string.IsNullOrWhiteSpace(_draft.DictationModelId) ? "not selected" : _draft.DictationModelId);

    [RelayCommand]
    private void Back()
    {
        if (_progress.Step <= 0) return;
        PersistDraft();
        CancelActiveOperations();
        _progress = _progress with { Step = _progress.Step - 1 };
        PersistProgress();
        Render();
    }

    [RelayCommand]
    private void Next()
    {
        if (_progress.Step >= OnboardingProgress.LastStep) return;
        PersistDraft();
        CancelActiveOperations();
        _progress = _progress with { Step = _progress.Step + 1 };
        PersistProgress();
        Render();
    }

    [RelayCommand]
    private void Skip()
    {
        PersistDraft();
        CancelActiveOperations();
        _progress = _progress with { Deferred = true, LastStatus = "Setup paused. Resume anytime from About or the tray." };
        PersistProgress();
        Completed?.Invoke(this, false);
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        PersistDraft();
        if (!(_progress.MicrophoneVerified && _progress.ModelsVerified && _progress.HotkeyVerified && _progress.PipelineVerified))
        {
            ApplyStatus("Complete each required check before finishing setup.", isError: true);
            return;
        }

        CancelActiveOperations();
        _progress = _progress with { Completed = true, Deferred = false, LastStatus = "Setup finished." };
        PersistProgress();
        var current = _settings.Load() with
        {
            OnboardingCompleted = true,
            LastCompletedFeatureTourVersion = 0,
            UserName = _draft.UserName,
            MicrophoneName = NormalizeMicrophone(_draft.MicrophoneName),
            DictationModelId = _draft.DictationModelId,
            FinalMeetingModelId = _draft.FinalMeetingModelId,
            LiveMeetingModelId = _draft.LiveMeetingModelId,
            Hotkey = _draft.Hotkey,
            StartAtLogin = StartAtLogin,
            ShowFloatingIndicator = ShowIndicator,
            IndicatorAnchor = SelectedIndicatorPosition,
            MeetingSummaryProvider = SelectedSummaryProvider?.Id ?? _draft.MeetingSummaryProvider
        };
        _settings.Save(current);
        if (IsStartupAvailable)
        {
            try { await _startup.SetEnabledAsync(StartAtLogin); }
            catch (Exception exception) { ApplyStatus(exception.Message, isError: true); }
        }
        Completed?.Invoke(this, true);
    }

    [RelayCommand]
    private async Task TestMicrophoneAsync()
    {
        IsBusy = true;
        try
        {
            var probe = await _microphone.ProbeAsync(ProbeMicrophoneName());
            _progress = _progress with
            {
                MicrophoneVerified = probe.IsUsable,
                VerifiedMicrophone = probe.IsUsable ? SelectedMicrophone ?? "" : "",
                LastStatus = probe.IsUsable
                    ? "Microphone capture verified. No audio was retained."
                    : $"Microphone test failed: {probe.Failure}"
            };
            PersistProgress();
            MicrophonePolicyHint = string.Join(" ", new[] { probe.PolicyHint, probe.Message }.Where(text => !string.IsNullOrWhiteSpace(text)));
            ApplyStatus(probe.IsUsable
                ? $"{probe.PolicyHint} {probe.Message}".Trim()
                : MicrophonePolicyHint,
                isError: !probe.IsUsable);
            RefreshPermissions(microphoneChecked: true, microphoneGranted: probe.IsUsable, probe.IsUsable ? "Capture verified" : probe.Failure.ToString(), MicrophonePolicyHint);
            NotifyReadyBindings();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenMicrophonePrivacy()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
            ApplyStatus("Windows microphone privacy opened. Re-run the capture test after changing access; the policy hint is not a grant.");
        }
        catch (Exception exception)
        {
            ApplyStatus(exception.Message, isError: true);
        }
    }

    [RelayCommand]
    private void OpenPermissionSettings(OnboardingPermissionItem? item)
    {
        if (item?.CanOpenSettings == true)
        {
            OpenMicrophonePrivacy();
        }
    }

    [RelayCommand]
    private void CheckHotkey()
    {
        try
        {
            var parsed = HotkeyGestureParser.Parse(SelectedHotkey);
            if (HotkeyGestureParser.IsEscape(parsed.VirtualKey))
            {
                _progress = _progress with { HotkeyVerified = false, VerifiedHotkey = "", LastStatus = "Escape is reserved for pausing setup." };
                ApplyStatus(_progress.LastStatus, isError: true);
                PersistProgress();
                NotifyReadyBindings();
                return;
            }

            var previous = _settings.Load();
            var advisoryAvailable = false;
            try { advisoryAvailable = HotkeyConflictProbe.IsAdvisoryRegistrationAvailable(parsed.DisplayName); }
            catch (Exception exception)
            {
                advisoryAvailable = false;
                ApplyStatus($"Advisory shortcut probe failed ({exception.Message}); the live dictation hook remains decisive.");
            }

            _settings.Save(previous with { Hotkey = parsed.DisplayName });
            var usable = false;
            try
            {
                _dictation.RegisterHotkey();
                usable = _dictation.IsHotkeyRegistered;
            }
            finally
            {
                if (!usable)
                {
                    _settings.Save(previous);
                    _dictation.RegisterHotkey();
                }
            }

            SelectedHotkey = parsed.DisplayName;
            _progress = _progress with
            {
                HotkeyVerified = usable,
                VerifiedHotkey = usable ? parsed.DisplayName : "",
                LastStatus = usable
                    ? advisoryAvailable
                        ? $"Shortcut {parsed.DisplayName} passed the dictation-hook test; the advisory probe also found no common conflict."
                        : $"Shortcut {parsed.DisplayName} passed the dictation-hook test. Windows advisory registration was unavailable, but did not veto the real hook."
                    : "Windows reports this shortcut is currently in use. Choose another preset and retry."
            };
            ApplyStatus(_progress.LastStatus, isError: !usable);
        }
        catch (Exception exception)
        {
            _progress = _progress with { HotkeyVerified = false, LastStatus = exception.Message };
            ApplyStatus(exception.Message, isError: true);
        }
        PersistProgress();
        NotifyReadyBindings();
    }

    [RelayCommand]
    private async Task RunPipelineTestAsync()
    {
        if (IsPipelineRunning)
        {
            _pipelineCancellation?.Cancel();
            ApplyStatus("Cancelling the local setup test…");
            return;
        }

        if (_dictation.IsRecording || _dictation.IsBusy)
        {
            ApplyStatus("Dictation is already active. Finish or cancel it before running the setup test.", isError: true);
            return;
        }

        var dictationId = SelectedDictationModel?.Id ?? _draft.DictationModelId;
        var snapshot = _models.Snapshots().FirstOrDefault(item => item.Model.Id.Equals(dictationId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null || snapshot.Status is not (TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected))
        {
            ApplyStatus($"{SelectedDictationModel?.DisplayName ?? dictationId} must be prepared before this test.", isError: true);
            return;
        }

        PersistDraft();
        _pipelineCancellation = new CancellationTokenSource();
        var token = _pipelineCancellation.Token;
        IsPipelineRunning = true;
        IsBusy = true;
        OnPropertyChanged(nameof(PipelineActionLabel));
        ApplyStatus("Recording a local setup test…");
        try
        {
            await _dictation.StartAuxiliaryAsync(ProbeMicrophoneName());
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            var text = await _dictation.StopWithoutPersistingAsync(token);
            var passed = !string.IsNullOrWhiteSpace(text);
            _progress = _progress with
            {
                PipelineVerified = passed,
                LastStatus = passed ? "Pipeline test passed" : "Pipeline test needs another attempt"
            };
            PersistProgress();
            ApplyStatus(passed ? $"Test transcript (not saved): {text}" : "No speech was detected. Try again.", isError: !passed);
        }
        catch (OperationCanceledException)
        {
            if (_dictation.IsRecording) await _dictation.CancelAsync();
            _progress = _progress with { PipelineVerified = false, LastStatus = "Pipeline test cancelled. Record another non-retained test when ready." };
            PersistProgress();
            ApplyStatus(_progress.LastStatus);
        }
        catch (Exception exception)
        {
            if (_dictation.IsRecording) await _dictation.CancelAsync();
            ApplyStatus(exception.Message, isError: true);
        }
        finally
        {
            _pipelineCancellation?.Dispose();
            _pipelineCancellation = null;
            IsPipelineRunning = false;
            IsBusy = false;
            OnPropertyChanged(nameof(PipelineActionLabel));
            NotifyReadyBindings();
        }
    }

    [RelayCommand]
    private async Task PrepareModelAsync(OnboardingModelRoleItem? item) =>
        await RunModelOperationAsync(item, "Prepare", (role, progress, token) =>
            role.IsLive
                ? _liveLifecycle.PrepareAsync(role.Id, progress, token)
                : _models.PrepareAsync(role.Id, progress, token));

    [RelayCommand]
    private async Task VerifyModelAsync(OnboardingModelRoleItem? item) =>
        await RunModelOperationAsync(item, "Verify", (role, progress, token) =>
            role.IsLive
                ? _liveLifecycle.VerifyAsync(role.Id, progress, token)
                : _models.VerifyAsync(role.Id, progress, token));

    [RelayCommand]
    private async Task RetryModelAsync(OnboardingModelRoleItem? item) =>
        await RunModelOperationAsync(item, "Retry", (role, progress, token) =>
            role.IsLive
                ? _liveLifecycle.RetryAsync(role.Id, token)
                : _models.RetryAsync(role.Id, token));

    [RelayCommand]
    private void CancelModel(OnboardingModelRoleItem? item)
    {
        if (item is null) return;
        if (_activeModelOperation is { } active &&
            active.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
            active.Live == item.IsLive)
        {
            if (item.IsLive) _liveLifecycle.Cancel(item.Id);
            else _models.Cancel(item.Id);
            item.StatusText = "Cancelling model operation…";
            item.NotifyProgressBindings();
            ApplyStatus($"Cancelling {item.Role} model operation…");
        }
    }

    [RelayCommand]
    private void OpenSummaryProviderSettings()
    {
        PersistDraft();
        if (App.Window is MainWindow mainWindow)
        {
            mainWindow.ShowDashboard("settings");
            ApplyStatus("Settings is open for summary-provider keys. Return here to continue setup. Keys can only be added in Settings.");
        }
        else
        {
            ApplyStatus("Open Settings to configure summary-provider keys. Setup stays on this step.");
        }
    }

    /// <summary>
    /// Saves the chosen anchor. It does not open a mock indicator: the real
    /// <c>DictationIndicatorWindow</c> is driven by dictation state and by the "Show floating
    /// indicator" setting, and setup must not stage a window that is not the live one.
    /// </summary>
    /// <remarks>
    /// The button used to read "Preview floating indicator" while the status it produced said no
    /// preview exists — the control promised something the command deliberately refuses to fake.
    /// The label now states what actually happens (P8-05's status-language class).
    /// </remarks>
    [RelayCommand]
    private void PreviewIndicator()
    {
        PersistDraft();
        ApplyStatus($"Indicator position saved as {SelectedIndicatorPosition}. Turn on “Show floating indicator” above to see the real indicator; it also appears while you dictate.");
    }

    [RelayCommand]
    private void ResetIndicatorPosition()
    {
        SelectedIndicatorPosition = "Middle Right";
        _draft = _draft with { IndicatorAnchor = "Middle Right", IndicatorLeft = null, IndicatorTop = null };
        _settings.Save(_draft);
        ApplyStatus("Custom indicator position cleared. Anchor is Middle Right.");
    }

    public void NotifyActivated()
    {
        if (Step == 1)
        {
            ApplyStatus("Microphone permissions may have changed; run the non-retained capture test to refresh this gate.");
        }
    }

    public event EventHandler<bool>? Completed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActiveOperations();
        _models.ModelChanged -= OnOfflineModelChanged;
        _liveLifecycle.ModelChanged -= OnLiveModelChanged;
        _liveLifecycle.Dispose();
        _pipelineCancellation?.Dispose();
    }

    partial void OnSelectedMicrophoneChanged(string? value)
    {
        if (_isRendering) return;
        ApplySelectionChange(_draft with { MicrophoneName = NormalizeMicrophone(value) });
    }

    partial void OnSelectedDictationModelChanged(TranscriptionModelDefinition? value)
    {
        if (_isRendering || value is null) return;
        ApplySelectionChange(_draft with { DictationModelId = value.Id });
    }

    partial void OnSelectedMeetingModelChanged(TranscriptionModelDefinition? value)
    {
        if (_isRendering || value is null) return;
        ApplySelectionChange(_draft with { FinalMeetingModelId = value.Id });
    }

    partial void OnSelectedLiveModelChanged(OnboardingLiveChoice value)
    {
        if (_isRendering) return;
        ApplySelectionChange(_draft with { LiveMeetingModelId = value.Id });
    }

    partial void OnSelectedHotkeyChanged(string value)
    {
        if (_isRendering || string.IsNullOrWhiteSpace(value)) return;
        ApplySelectionChange(_draft with { Hotkey = value });
    }

    partial void OnSelectedSummaryProviderChanged(SummaryProviderInfo? value)
    {
        if (_isRendering || value is null) return;
        _draft = _draft with { MeetingSummaryProvider = value.Id };
        _settings.Save(_draft);
        OnPropertyChanged(nameof(SummaryDisclosure));
        RefreshCloudKeyStatus();
    }

    partial void OnSelectedIndicatorPositionChanged(string value)
    {
        if (_isRendering || string.IsNullOrWhiteSpace(value)) return;
        _draft = _draft with { IndicatorAnchor = value };
        _settings.Save(_draft);
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_isRendering) return;
        _draft = _draft with { StartAtLogin = value };
        _settings.Save(_draft);
    }

    partial void OnShowIndicatorChanged(bool value)
    {
        if (_isRendering) return;
        _draft = _draft with { ShowFloatingIndicator = value };
        _settings.Save(_draft);
    }

    partial void OnUserNameChanged(string value)
    {
        if (_isRendering) return;
        _draft = _draft with { UserName = value.Trim() };
        _settings.Save(_draft);
    }

    private void ApplyDraftToFields()
    {
        _isRendering = true;
        try
        {
            UserName = _draft.UserName;
            SelectedMicrophone = string.IsNullOrWhiteSpace(_draft.MicrophoneName)
                ? AudioCaptureService.SystemDefaultMicrophone
                : _draft.MicrophoneName;
            if (!Microphones.Contains(SelectedMicrophone, StringComparer.OrdinalIgnoreCase))
            {
                Microphones = Microphones.Append(SelectedMicrophone).ToList();
            }
            SelectedDictationModel = TranscriptionModelCatalog.Get(_draft.DictationModelId);
            SelectedMeetingModel = TranscriptionModelCatalog.Get(_draft.FinalMeetingModelId);
            SelectedLiveModel = LiveModels.FirstOrDefault(choice => string.Equals(choice.Id, _draft.LiveMeetingModelId, StringComparison.OrdinalIgnoreCase))
                ?? OnboardingLiveChoice.Off;
            SelectedHotkey = string.IsNullOrWhiteSpace(_draft.Hotkey) ? "F8" : _draft.Hotkey;
            SelectedSummaryProvider = SummaryProviderDisclosure.For(_draft.MeetingSummaryProvider);
            SelectedIndicatorPosition = IndicatorPositions.Contains(_draft.IndicatorAnchor) ? _draft.IndicatorAnchor : "Middle Right";
            StartAtLogin = _draft.StartAtLogin;
            ShowIndicator = _draft.ShowFloatingIndicator;
            RefreshCloudKeyStatus();
        }
        finally { _isRendering = false; }
    }

    private void PersistDraft()
    {
        _draft = _draft with
        {
            UserName = UserName.Trim(),
            MicrophoneName = NormalizeMicrophone(SelectedMicrophone),
            DictationModelId = SelectedDictationModel?.Id ?? _draft.DictationModelId,
            FinalMeetingModelId = SelectedMeetingModel?.Id ?? _draft.FinalMeetingModelId,
            LiveMeetingModelId = SelectedLiveModel.Id,
            Hotkey = SelectedHotkey,
            StartAtLogin = StartAtLogin,
            ShowFloatingIndicator = ShowIndicator,
            IndicatorAnchor = SelectedIndicatorPosition,
            MeetingSummaryProvider = SelectedSummaryProvider?.Id ?? _draft.MeetingSummaryProvider
        };
        _settings.Save(_draft);
    }

    private void PersistProgress() => _progressStore.Save(_progress);

    private void Render()
    {
        Step = _progress.Step;
        var definition = OnboardingStepCatalog.Get(Step);
        StepLabel = $"SETUP · {Step + 1} OF {StepCount}";
        Title = definition.Title;
        Description = definition.Description;
        Status = _progress.LastStatus;
        StatusIsError = LooksLikeError(Status);
        CanGoBack = Step > 0;
        CanGoNext = Step < OnboardingProgress.LastStep;
        CanFinish = Step == OnboardingProgress.LastStep;
        StepNavItems = OnboardingStepCatalog.Steps
            .Select(step => new OnboardingStepNavItem(
                step.Step,
                $"{step.Step + 1}",
                step.Title,
                step.Step == Step,
                step.Step < Step))
            .ToList();
        RefreshPermissionsFromProgress();
        RefreshModelRoles();
        RefreshCloudKeyStatus();
        OnPropertyChanged(nameof(ShowName));
        OnPropertyChanged(nameof(ShowMicrophone));
        OnPropertyChanged(nameof(ShowModels));
        OnPropertyChanged(nameof(ShowHotkey));
        OnPropertyChanged(nameof(ShowPipeline));
        OnPropertyChanged(nameof(ShowOptional));
        OnPropertyChanged(nameof(ShowReady));
        OnPropertyChanged(nameof(ShowStatus));
        OnPropertyChanged(nameof(ShowStartupNotice));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(PipelineActionLabel));
        OnPropertyChanged(nameof(SummaryDisclosure));
        NotifyReadyBindings();
    }

    private void NotifyReadyBindings()
    {
        OnPropertyChanged(nameof(MicrophoneReady));
        OnPropertyChanged(nameof(ModelsReady));
        OnPropertyChanged(nameof(HotkeyReady));
        OnPropertyChanged(nameof(PipelineReady));
        OnPropertyChanged(nameof(MicrophoneGateLabel));
        OnPropertyChanged(nameof(ModelsGateLabel));
        OnPropertyChanged(nameof(HotkeyGateLabel));
        OnPropertyChanged(nameof(PipelineGateLabel));
        OnPropertyChanged(nameof(PipelineMicrophoneLabel));
        OnPropertyChanged(nameof(PipelineModelLabel));
    }

    private void ApplySelectionChange(MuesliSettings draft)
    {
        var before = ToSelection(_draft);
        var after = ToSelection(draft);
        if (before == after) return;
        _draft = draft;
        _settings.Save(_draft);
        _progress = OnboardingProgressReconciler.InvalidateForSelectionChange(_progress, before, after);
        PersistProgress();
        RefreshModelRoles();
        ApplyStatus(_progress.LastStatus);
        NotifyReadyBindings();
    }

    private static OnboardingSelection ToSelection(MuesliSettings draft) =>
        new(draft.MicrophoneName, draft.DictationModelId, draft.FinalMeetingModelId, draft.LiveMeetingModelId, draft.Hotkey);

    private void RefreshModelRoles()
    {
        var desired = SelectedModelRoles().ToList();
        for (var index = _modelRoles.Count - 1; index >= 0; index--)
        {
            var existing = _modelRoles[index];
            if (!desired.Any(role => role.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase) && role.Live == existing.IsLive))
            {
                _modelRoles.RemoveAt(index);
            }
        }

        foreach (var role in desired)
        {
            var item = _modelRoles.FirstOrDefault(existing =>
                existing.Id.Equals(role.Id, StringComparison.OrdinalIgnoreCase) && existing.IsLive == role.Live);
            if (item is null)
            {
                item = new OnboardingModelRoleItem(role.Role, role.Id, role.Live);
                _modelRoles.Add(item);
            }

            ApplySnapshot(item);
        }

        RefreshCurrentModelGate(announce: false);
    }

    private IEnumerable<(string Id, bool Live, string Role)> SelectedModelRoles()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dictationId = SelectedDictationModel?.Id ?? _draft.DictationModelId;
        var meetingId = SelectedMeetingModel?.Id ?? _draft.FinalMeetingModelId;
        var liveId = SelectedLiveModel?.Id;
        if (ids.Add(dictationId)) yield return (dictationId, false, "Dictation");
        if (ids.Add(meetingId)) yield return (meetingId, false, "Final meeting/import");
        if (!string.IsNullOrWhiteSpace(liveId) && ids.Add($"live:{liveId}"))
            yield return (liveId!, true, "Live preview");
    }

    private void ApplySnapshot(OnboardingModelRoleItem item)
    {
        if (item.IsLive)
        {
            var snapshot = _liveLifecycle.Snapshot(item.Id);
            item.Heading = $"{item.Role}: {snapshot.Model.PickerLabel}";
            item.StatusText = snapshot.StatusText;
            item.IsBusy = snapshot.IsBusy || (_activeModelOperation?.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) == true && _activeModelOperation?.Live == true);
            item.IsReady = snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
            item.CanPrepare = snapshot.CanPrepare && _activeModelOperation is null;
            item.CanVerify = snapshot.CanVerify && _activeModelOperation is null;
            item.CanRetry = snapshot.CanRetry && _activeModelOperation is null;
            item.CanCancel = snapshot.CanCancel || (_activeModelOperation?.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) == true && _activeModelOperation?.Live == true);
            if (!item.IsBusy) item.ProgressPercent = null;
        }
        else
        {
            var snapshot = _models.Snapshots().First(entry => entry.Model.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
            item.Heading = $"{item.Role}: {snapshot.Model.PickerLabel}";
            item.StatusText = snapshot.StatusText;
            item.IsBusy = snapshot.IsBusy || (_activeModelOperation?.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) == true && _activeModelOperation?.Live == false);
            item.IsReady = snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
            item.CanPrepare = snapshot.CanPrepare && _activeModelOperation is null;
            item.CanVerify = snapshot.CanVerify && _activeModelOperation is null;
            item.CanRetry = snapshot.CanRetry && _activeModelOperation is null;
            item.CanCancel = snapshot.CanCancel || (_activeModelOperation?.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) == true && _activeModelOperation?.Live == false);
            if (!item.IsBusy) item.ProgressPercent = null;
        }

        item.NotifyProgressBindings();
    }

    private void RefreshCurrentModelGate(bool announce)
    {
        var roles = SelectedModelRoles().ToList();
        var ready = roles.Count > 0 && roles.All(role => role.Live ? LiveReady(role.Id) : OfflineReady(role.Id));
        _progress = _progress with
        {
            ModelsVerified = ready,
            VerifiedDictationModelId = ready ? SelectedDictationModel?.Id ?? "" : "",
            VerifiedFinalModelId = ready ? SelectedMeetingModel?.Id ?? "" : "",
            VerifiedLiveModelId = ready ? SelectedLiveModel?.Id : null,
            LastStatus = announce
                ? ready
                    ? "Selected model roles verified"
                    : "A selected model role is not ready. Prepare, verify, or retry it."
                : _progress.LastStatus
        };
        PersistProgress();
        NotifyReadyBindings();
    }

    private bool OfflineReady(string id)
    {
        var snapshot = _models.Snapshots().FirstOrDefault(entry => entry.Model.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return snapshot is not null && snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
    }

    private bool LiveReady(string id)
    {
        var snapshot = _liveLifecycle.Snapshot(id);
        return snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected;
    }

    private async Task RunModelOperationAsync(
        OnboardingModelRoleItem? item,
        string action,
        Func<OnboardingModelRoleItem, IProgress<ModelDownloadProgress>, CancellationToken, Task> operation)
    {
        if (item is null) return;
        if (_activeModelOperation is not null)
        {
            ApplyStatus("Another model operation is already in progress. Cancel it or wait for it to finish.", isError: true);
            return;
        }

        PersistDraft();
        _activeModelOperation = (item.Id, item.IsLive);
        IsBusy = true;
        item.IsBusy = true;
        item.CanPrepare = item.CanVerify = item.CanRetry = false;
        item.CanCancel = true;
        item.NotifyProgressBindings();
        RefreshModelRoles();
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            App.UiDispatcher.TryEnqueue(() =>
            {
                item.StatusText = value.DisplayText;
                item.ProgressPercent = value.Percent;
                item.IsBusy = true;
                item.NotifyProgressBindings();
                ApplyStatus($"{item.Role}: {value.DisplayText}");
            });
        });
        try
        {
            await operation(item, progress, CancellationToken.None);
            RefreshCurrentModelGate(announce: true);
            ApplySnapshot(item);
            ApplyStatus(item.StatusText, isError: !item.IsReady);
        }
        catch (OperationCanceledException)
        {
            item.StatusText = "Cancelled. You can retry safely.";
            ApplyStatus(item.StatusText);
        }
        catch (Exception exception)
        {
            item.StatusText = exception.Message;
            ApplyStatus(exception.Message, isError: true);
        }
        finally
        {
            _activeModelOperation = null;
            IsBusy = false;
            RefreshModelRoles();
            PersistProgress();
        }
    }

    private void RefreshPermissionsFromProgress()
    {
        RefreshPermissions(
            microphoneChecked: _progress.MicrophoneVerified,
            microphoneGranted: _progress.MicrophoneVerified,
            _progress.MicrophoneVerified ? "Capture verified" : "Not checked yet",
            string.IsNullOrWhiteSpace(MicrophonePolicyHint)
                ? "A short non-retained WASAPI capture is the real microphone check."
                : MicrophonePolicyHint);
    }

    private void RefreshPermissions(bool microphoneChecked, bool microphoneGranted, string microphoneStatus, string microphoneHelp)
    {
        var rows = WindowsPermissionStatus.Build(microphoneChecked, microphoneGranted, microphoneStatus, microphoneHelp);
        Permissions = rows.Select((row, index) => new OnboardingPermissionItem(
            row.Name,
            row.Status,
            row.Granted,
            row.Help,
            CanOpenSettings: string.Equals(row.Name, "Microphone", StringComparison.OrdinalIgnoreCase),
            IsLast: index == rows.Count - 1)).ToList();
    }

    private void RefreshCloudKeyStatus()
    {
        var current = _settings.Load();
        var openAi = string.IsNullOrWhiteSpace(current.ResolvedOpenAIApiKey) ? "not configured" : "configured securely";
        var openRouter = string.IsNullOrWhiteSpace(current.ResolvedOpenRouterApiKey) ? "not configured" : "configured securely";
        CloudKeyStatus = $"OpenAI key: {openAi}. OpenRouter key: {openRouter}. Keys can only be added in Settings.";
    }

    private async Task RefreshStartupAvailabilityAsync()
    {
        try
        {
            StartAtLogin = await _startup.IsEnabledAsync();
            IsStartupAvailable = true;
            StartupAvailabilityNotice = "";
        }
        catch (Exception exception)
        {
            IsStartupAvailable = false;
            StartAtLogin = false;
            StartupAvailabilityNotice =
                "Launch at sign-in is unavailable: this build is running without package identity, " +
                $"so the Windows startup task cannot be read ({exception.GetType().Name}). " +
                "Install the packaged build to manage it here.";
        }
        OnPropertyChanged(nameof(ShowStartupNotice));
    }

    private void ApplyStatus(string message, bool isError = false)
    {
        Status = message;
        StatusIsError = isError || LooksLikeError(message);
        OnPropertyChanged(nameof(ShowStatus));
    }

    private static bool LooksLikeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        return message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not ready", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("could not", StringComparison.OrdinalIgnoreCase);
    }

    private string? ProbeMicrophoneName()
    {
        var selected = SelectedMicrophone;
        if (string.IsNullOrWhiteSpace(selected) ||
            selected.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ||
            selected.Equals(AudioCaptureService.SystemDefaultMicrophone, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return selected;
    }

    private static string? NormalizeMicrophone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Equals("Automatic", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(AudioCaptureService.SystemDefaultMicrophone, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }

    private IReadOnlyList<string> EnsureHotkeyPreset(string hotkey)
    {
        var list = HotkeyPresets.ToList();
        if (!string.IsNullOrWhiteSpace(hotkey) &&
            !list.Any(option => option.Equals(hotkey, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(hotkey);
        }

        return list;
    }

    private void CancelActiveOperations()
    {
        if (_activeModelOperation is { } active)
        {
            if (active.Live) _liveLifecycle.Cancel(active.Id);
            else _models.Cancel(active.Id);
            _activeModelOperation = null;
        }

        if (IsPipelineRunning)
        {
            _pipelineCancellation?.Cancel();
        }
    }

    private void OnOfflineModelChanged(object? sender, string modelId) =>
        App.UiDispatcher.TryEnqueue(RefreshModelRoles);

    private void OnLiveModelChanged(object? sender, string modelId) =>
        App.UiDispatcher.TryEnqueue(RefreshModelRoles);
}
