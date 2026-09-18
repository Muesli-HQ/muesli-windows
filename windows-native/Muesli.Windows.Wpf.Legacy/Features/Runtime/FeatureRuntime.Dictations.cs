using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Muesli.Windows.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void HoldToDictate_MouseDown(object sender, MouseButtonEventArgs e) =>
    _ = StartHoldToDictationAsync();

private Task StartHoldToDictationAsync() =>
    StartDictationAsync(shouldPasteToActiveApp: false);
private void ShowOnboardingIfNeeded(bool explicitResume = false)
{
    if (_onboardingCompleted)
    {
        return;
    }

    if (_onboardingWindow is { IsVisible: true })
    {
        _onboardingWindow.Activate();
        return;
    }

    var savedProgress = _onboardingProgressStore.Load();
    if (savedProgress.Deferred && !explicitResume)
    {
        return;
    }
    if (savedProgress.Deferred)
    {
        savedProgress = savedProgress with { Deferred = false, LastStatus = "Setup resumed. Continue from the saved step." };
        _onboardingProgressStore.Save(savedProgress);
    }
    var progress = OnboardingProgressReconciler.Reconcile(
        savedProgress,
        new OnboardingSelection(SelectedMicrophone, SelectedTranscriptionModel.Id, SelectedFinalMeetingModel.Id, SelectedLiveMeetingModel.Id, SelectedHotkey),
        id => _modelLifecycle.Snapshot(id).Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected,
        id => _streamingModelLifecycle.Snapshot(id).Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected);
    var context = new OnboardingContext(
        MicrophoneDevices, HotkeyOptions, TranscriptionModels, LiveMeetingModels,
        new OnboardingDraft(UserName, SelectedMicrophone, SelectedHotkey, SelectedTranscriptionModel.Id, SelectedFinalMeetingModel.Id, SelectedLiveMeetingModel.Id, StartAtLogin, ShowFloatingIndicator, SelectedIndicatorPosition, SelectedSummaryProvider), _ollamaEndpoint,
        microphone => _microphoneAccessService.ProbeAsync(microphone),
        (id, progress, token) => _modelLifecycle.PrepareAsync(id, progress, token),
        (id, progress, token) => _modelLifecycle.VerifyAsync(id, progress, token),
        id => _modelLifecycle.Cancel(id),
        (id, progress, token) => _modelLifecycle.RetryAsync(id, token),
        OnboardingOfflineModelSnapshot,
        (id, progress, token) => _streamingModelLifecycle.PrepareAsync(id, progress, token),
        (id, progress, token) => _streamingModelLifecycle.VerifyAsync(id, progress, token),
        id => _streamingModelLifecycle.Cancel(id),
        (id, progress, token) => _streamingModelLifecycle.RetryAsync(id, token),
        OnboardingLiveModelSnapshot,
        RunOnboardingPipelineTestForWindowAsync,
        gesture => TestOnboardingHotkey(gesture),
        !OpenAIApiKeyStatus.Equals("Not configured", StringComparison.OrdinalIgnoreCase),
        !OpenRouterApiKeyStatus.Equals("Not configured", StringComparison.OrdinalIgnoreCase),
        IndicatorPositions,
        PreviewOnboardingIndicator,
        ResetOnboardingIndicatorPosition,
        OpenOnboardingSummaryProviderSettings,
        ApplyOnboardingDraft,
        value => _onboardingProgressStore.Save(value),
        CompleteOnboarding);
    _onboardingWindow = new OnboardingWindow(context, progress) { Owner = _shell.Window };
    _onboardingWindow.Closed += (_, _) => { _onboardingWindow = null; _trayIconService.Refresh(); };
    _onboardingWindow.Show();
}

private OnboardingModelSnapshot OnboardingOfflineModelSnapshot(string modelId)
{
    var snapshot = _modelLifecycle.Snapshot(modelId);
    return new OnboardingModelSnapshot(snapshot.StatusText,
        snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected,
        snapshot.IsBusy, snapshot.CanPrepare, snapshot.CanCancel, snapshot.CanRetry, snapshot.CanVerify);
}

private OnboardingModelSnapshot OnboardingLiveModelSnapshot(string modelId)
{
    var snapshot = _streamingModelLifecycle.Snapshot(modelId);
    return new OnboardingModelSnapshot(snapshot.StatusText,
        snapshot.Status is TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected,
        snapshot.IsBusy, snapshot.CanPrepare, snapshot.CanCancel, snapshot.CanRetry, snapshot.CanVerify);
}

private void PreviewOnboardingIndicator()
{
    _toastNotificationService.SetIndicatorAnchor(SelectedIndicatorPosition, clearCustomPosition: false);
    _toastNotificationService.ShowIdle(SelectedHotkey);
}

private void ResetOnboardingIndicatorPosition()
{
    SelectedIndicatorPosition = "Middle Right";
    _indicatorLeft = null;
    _indicatorTop = null;
    _toastNotificationService.SetSavedPosition(null, null);
    _toastNotificationService.SetIndicatorAnchor(SelectedIndicatorPosition, clearCustomPosition: true);
}

private void OpenOnboardingSummaryProviderSettings()
{
    _onboardingWindow?.Close();
    ShowPage(SettingsPage, SettingsNav);
}

private bool TestOnboardingHotkey(string gesture)
{
    var previous = SelectedHotkey;
    var advisoryAvailable = false;
    try { advisoryAvailable = HotkeyConflictProbe.IsAdvisoryRegistrationAvailable(gesture); }
    catch (Exception exception)
    {
        _logService.Error("Onboarding shortcut advisory probe failed; continuing with the decisive hook test.", exception);
    }

    // RegisterGlobalHotkey is the same low-level hook used by dictation.  The advisory
    // RegisterHotKey probe is deliberately supplemental: it cannot veto that real test.
    _selectedHotkey = gesture;
    OnPropertyChanged(nameof(SelectedHotkey));
    var result = HotkeyCandidateHookTest.Run(
        RegisterGlobalHotkey,
        () =>
        {
            _selectedHotkey = previous;
            OnPropertyChanged(nameof(SelectedHotkey));
            return RegisterGlobalHotkey();
        });

    if (!result.PreviousHookRestored)
    {
        var exception = result.RestorationException ?? new InvalidOperationException("The previous shortcut hook could not be restored.");
        _logService.Error("Onboarding shortcut hook restoration failed after candidate test.", exception);
    }
    if (result.CandidateRegistered)
        DictationStatus = advisoryAvailable
            ? $"Shortcut {gesture} passed the actual dictation-hook test; the advisory probe also found no common conflict."
            : $"Shortcut {gesture} passed the actual dictation-hook test. Windows advisory registration was unavailable, but did not veto the real hook.";
    return result.CandidateRegistered;
}

private async Task<string> RunOnboardingPipelineTestForWindowAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (_dictationCoordinator.IsBusy || _dictationCoordinator.IsRecording)
        throw new InvalidOperationException("Dictation is already active.");
    if (!_dictationCoordinator.IsModelReady)
        throw new InvalidOperationException($"{ActiveModelLabel} must be prepared before this test.");
    try
    {
        DictationStatus = "Recording a local setup test";
        await _dictationCoordinator.StartAsync(SelectedMicrophone, DictationSessionKind.SetupTest);
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var result = await _dictationCoordinator.StopForOnboardingTestAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Text?.Trim() ?? "";
    }
    catch
    {
        if (_dictationCoordinator.IsRecording) await _dictationCoordinator.CancelAsync();
        throw;
    }
}

private void ApplyOnboardingDraft(OnboardingDraft draft)
{
    UserName = draft.UserName;
    SelectedMicrophone = draft.Microphone;
    SelectedHotkey = draft.Hotkey;
    SelectedTranscriptionModel = TranscriptionModelCatalog.Get(draft.DictationModelId);
    SelectedFinalMeetingModel = TranscriptionModelCatalog.Get(draft.FinalModelId);
    SelectedLiveMeetingModel = LiveMeetingModels.FirstOrDefault(item => item.Id == draft.LiveModelId) ?? LiveModelChoice.Off;
    StartAtLogin = draft.StartAtLogin;
    ShowFloatingIndicator = draft.ShowIndicator;
    SelectedIndicatorPosition = draft.IndicatorPosition;
    SelectedSummaryProvider = draft.SummaryProvider;
    SaveSettings(); // Settings are authoritative; progress is saved after this delegate returns.
}

private void CompleteOnboarding()
{
    _onboardingCompleted = true;
    SaveSettings();
    _onboardingProgressStore.Clear();
    DictationStatus = "Setup completed";
    _trayIconService.Refresh();
    OnPropertyChanged(nameof(SetupNeedsResume));
    OnPropertyChanged(nameof(SetupResumeLabel));
}

private ProductExperienceState CreateProductExperienceState()
{
    var progress = _onboardingProgressStore.Load();
    return new ProductExperienceState(
        !_onboardingCompleted || progress.Deferred,
        _onboardingCompleted ? "Setup complete" : "Resume setup",
        new StartupRegistrationState(
            StartupRegistrationService.IsEnabled(),
            StartupRegistrationService.IsRegisteredForBackgroundLaunch(),
            StartupRegistrationService.DescribeState()),
        Dictations.OrderByDescending(item => item.Timestamp).Take(6).Select(item => new ProductHistoryEntry("Dictation", item.Timestamp)).ToList(),
        Meetings.OrderByDescending(item => item.CreatedAt).Take(6).Select(item => new ProductHistoryEntry(item.Title, new DateTimeOffset(item.CreatedAt))).ToList(),
        _lastMeetingDetectionScan?.Found == true ? _lastMeetingDetectionScan.DetectedMeeting?.Platform ?? "Meeting detected" : "No meeting detected",
        _isMeetingRecording || _meetingPausedByUser,
        _meetingPausedByUser);
}

private void NavigateFromTray(string destination)
{
    switch (destination)
    {
        case "resume": ShowOnboardingIfNeeded(explicitResume: true); break;
        case "tour": ShowFeatureTour(); break;
        case "dictations": ShowPage(DictationsPage, DictationsNav); break;
        case "meetings": ShowPage(MeetingsPage, MeetingsNav); break;
        case "settings": ShowPage(SettingsPage, SettingsNav); break;
        case "about": ShowPage(AboutPage, AboutNav); break;
        case "pause-meeting": _ = PauseOrResumeMeetingFromIndicatorAsync(); break;
        case "stop-meeting": _ = StopMeetingFromTrayAsync(); break;
        case "discard-meeting":
            if (ConfirmDiscardMeeting())
            {
                _ = CancelMeetingRecordingAsync();
            }
            break;
    }
}

private void HoldToDictate_MouseUp(object sender, MouseButtonEventArgs e) =>
    _ = StopHoldToDictationAsync();

private Task StopHoldToDictationAsync() => StopDictationAsync();
private Task StartHotkeyDictationAsync()
{
    return StartDictationAsync(shouldPasteToActiveApp: true);
}

private Task ShowIndicatorFrameAsync(string title, string message, ToastState state, int durationMs = 2200, IndicatorOwner owner = IndicatorOwner.Transient)
{
    if (owner is IndicatorOwner.Dictation or IndicatorOwner.Meeting or IndicatorOwner.ComputerUse or IndicatorOwner.Background)
    {
        _toastNotificationService.ShowLive(owner, title, message, state);
    }
    else
    {
        _toastNotificationService.Show(title, message, state, durationMs);
    }
    return Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render).Task;
}
private bool RegisterGlobalHotkey()
{
    try
    {
        _globalHotkeyService.Register(
            SelectedHotkey,
            () => Dispatcher.InvokeAsync(HandleHotkeyDownAsync, DispatcherPriority.Send),
            () => Dispatcher.InvokeAsync(HandleHotkeyUpAsync, DispatcherPriority.Send),
            () => _dictationHotkeyState.IsLive
                || _dictationCoordinator.IsRecording
                || _dictationCoordinator.IsTranscribing
                || _dictationCoordinator.IsBusy
                || _dictationOperationCancellation is not null,
            () => Dispatcher.InvokeAsync(CancelDictationAsync, DispatcherPriority.Send),
            () => Dispatcher.InvokeAsync(HandleHotkeyOtherKeyAsync, DispatcherPriority.Send));
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
    try
    {
        var gesture = HotkeyGesture.Parse(value);
        if (gesture.Key == Key.Escape ||
            (gesture.Key is not (>= Key.F1 and <= Key.F24) && gesture.Modifiers == ModifierKeys.None))
        {
            return false;
        }
        normalized = gesture.Label;
        return true;
    }
    catch (InvalidOperationException)
    {
        return false;
    }
}
private async Task HandleHotkeyDownAsync()
{
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.KeyDown(EnableDoubleTapDictation));
}
private async Task HandleHotkeyUpAsync()
{
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.KeyUp(EnableDoubleTapDictation));
}

private async Task HandleHotkeyOtherKeyAsync()
{
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.OtherKeyWhileArmed());
}

private async Task ExecuteHotkeyActionAsync(DictationHotkeyAction action)
{
    // Planner voice capture has a separate explicit activation provenance. The ordinary dictation
    // hotkey must never stop, transcribe, persist, paste, or execute planner audio.
    if (!WorkflowEntryPoints.CanStartDictation(_computerUseVoiceCaptureActive, _computerUseIsRunning))
    {
        return;
    }
    switch (action)
    {
        case DictationHotkeyAction.Arm:
            StopHotkeyDelayTimers();
            DictationStatus = "Ready";
            _hotkeyPrepareTimer.Interval = HotkeyTriggerTiming.PrepareDelay(_hotkeyTriggerThresholdMs, EnableDoubleTapDictation);
            _hotkeyStartTimer.Interval = HotkeyTriggerTiming.StartDelay(_hotkeyTriggerThresholdMs, EnableDoubleTapDictation);
            _hotkeyPrepareTimer.Start();
            _hotkeyStartTimer.Start();
            break;
        case DictationHotkeyAction.ShowPreparing:
            await ShowIndicatorFrameAsync("Preparing", "Hold to dictate", ToastState.Preparing, 0, IndicatorOwner.Dictation);
            break;
        case DictationHotkeyAction.StartRecording:
            StopHotkeyDelayTimers();
            await StartHotkeyDictationAsync();
            if (!_dictationCoordinator.IsRecording)
            {
                ResetHotkeyDictationState();
                _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
            }
            break;
        case DictationHotkeyAction.StopRecording:
            StopHotkeyDelayTimers();
            await StopDictationAsync();
            break;
        case DictationHotkeyAction.Cancel:
            StopHotkeyDelayTimers();
            await CancelDictationAsync();
            break;
        case DictationHotkeyAction.StartDoubleTapTimer:
            StopHotkeyDelayTimers();
            _hotkeyReleaseTimer.Interval = HotkeyTriggerTiming.DoubleTapWindow;
            _hotkeyReleaseTimer.Start();
            break;
        case DictationHotkeyAction.EnterHandsFree:
            StopHotkeyDelayTimers();
            DictationStatus = "Hands-free dictation active";
            await StartHotkeyDictationAsync();
            if (_dictationCoordinator.IsRecording)
            {
                await ShowIndicatorFrameAsync("Recording", "Click the square or tap the shortcut to stop", ToastState.Recording, 0, IndicatorOwner.Dictation);
            }
            else
            {
                ResetHotkeyDictationState();
                _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
            }
            break;
    }
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
        _pasteTargetInfo = shouldPasteToActiveApp
            ? _activeAppPasteService.DescribeWindow(_pasteTargetWindow)
            : PasteTargetInfo.Unknown;
        DictationStatus = "Listening";
        await _dictationCoordinator.StartAsync(SelectedMicrophone);
        if (_dictationCoordinator.IsRecording)
        {
            await ShowIndicatorFrameAsync("Recording", $"Hold {SelectedHotkey} or the button while speaking", ToastState.Recording, 0, IndicatorOwner.Dictation);
        }
        DictationStatus = _dictationCoordinator.IsRecording ? "Listening" : "Ready";
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not start microphone: {exception.Message}";
        _logService.Error("Could not start dictation microphone.", exception);
        _toastNotificationService.Show("Microphone failed", exception.Message, ToastState.Error);
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
    }
}
private async Task StopDictationAsync()
{
    if (!WorkflowEntryPoints.CanStartDictation(_computerUseVoiceCaptureActive, _computerUseIsRunning))
    {
        return;
    }

    if (_dictationCoordinator.IsTranscribing || _dictationOperationCancellation is not null)
    {
        await CancelDictationAsync();
        return;
    }

    if (_dictationCoordinator.IsBusy && !_dictationCoordinator.IsRecording)
    {
        for (var attempt = 0; attempt < 40 && _dictationCoordinator.IsBusy && !_dictationCoordinator.IsRecording; attempt++)
        {
            await Task.Delay(25);
        }
    }

    if (!_dictationCoordinator.IsRecording)
    {
        ResetHotkeyDictationState();
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
        return;
    }

    ResetHotkeyDictationState();
    var traceId = Guid.NewGuid().ToString("N")[..12];
    var releaseToPasteStarted = Stopwatch.StartNew();
    DictationStopResult? stopResult = null;
    var operationCancellation = new CancellationTokenSource();
    _dictationOperationCancellation = operationCancellation;

    TranscriptionResult result;
    try
    {
        DictationStatus = "Transcribing";
        await ShowIndicatorFrameAsync("Transcribing", "Processing local audio", ToastState.Transcribing, 0, IndicatorOwner.Dictation);
        stopResult = await _dictationCoordinator.StopAsync(traceId, operationCancellation.Token);
        result = stopResult.Transcription;
        _transcriptionPipelineService.LogTranscriptionResult(
            "dictation", result, _dictationCoordinator.EngineId, _dictationCoordinator.ModelId);
    }
    catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
    {
        CompleteDictationOperationCancellation(operationCancellation);
        _pasteTargetWindow = IntPtr.Zero;
        _shouldPasteToActiveApp = false;
        DictationStatus = "Dictation cancelled";
        _logService.Info($"Dictation cancelled. trace={traceId}; stage=transcription; transcriptPersisted=false; transcriptDelivered=false");
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
        return;
    }
    catch (Exception exception)
    {
        CompleteDictationOperationCancellation(operationCancellation);
        if (_dictationCoordinator.IsRecording)
        {
            await _dictationCoordinator.CancelAsync();
        }

        DictationStatus = $"Dictation failed: {exception.Message}";
        _logService.Error("Dictation transcription failed.", exception);
        _toastNotificationService.Show("Dictation failed", exception.Message, ToastState.Error);
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
        return;
    }
    CompleteDictationOperationCancellation(operationCancellation);
    var textToUse = "";
    var blankAudioDetected = false;
    var cleanupStarted = Stopwatch.StartNew();
    if (!string.IsNullOrWhiteSpace(result.Text))
    {
        if (result.Text.Contains("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
            result.Text.Equals("BLANK_AUDIO", StringComparison.OrdinalIgnoreCase))
        {
            blankAudioDetected = true;
            _logService.Info($"Blank audio detected. Diagnostic: {result.Diagnostic}");
        }
        else
        {
            textToUse = await _transcriptionPipelineService.PrepareDictationTextAsync(
                result.Text,
                enableCleanup: false,
                removeFillerWords: RemoveFillerWords,
                DictionaryEntries.Select(entry => entry.Record));
        }
    }
    cleanupStarted.Stop();
    if (textToUse.Length > 0)
    {
        // Commit the transcript before interacting with another process or the clipboard.
        // A focus/input/clipboard failure can then never make successful ASR text unrecoverable.
        var persistenceStarted = Stopwatch.StartNew();
        Dictations.Insert(0, new DictationItem(
            $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            DateTime.Now,
            DateTime.Now.ToString("hh:mm tt"),
            textToUse,
            _dictationCoordinator.ModelId,
            result.DurationMs));
        var persistenceSucceeded = true;
        try
        {
            SaveDictations();
        }
        catch (Exception persistenceException)
        {
            persistenceSucceeded = false;
            _logService.Error("Dictation history save failed; keeping the transcript in the current dashboard and continuing delivery.", persistenceException);
        }
        OnPropertyChanged(nameof(DayStreak));
        OnPropertyChanged(nameof(WordsDictated));
        OnPropertyChanged(nameof(WordsDictatedDisplay));
        OnPropertyChanged(nameof(AverageWpm));
        OnPropertyChanged(nameof(InsightsSummary));
        RefreshSearchResults();
        persistenceStarted.Stop();

        var deliveryStarted = Stopwatch.StartNew();
        PasteOperationResult pasteResult;
        var deliveryMode = "clipboard";
        var activeAppDeliveryRequested = _shouldPasteToActiveApp && SelectedPasteBehavior == "active-app";
        var deliverySucceeded = true;
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
        try
        {
            if (activeAppDeliveryRequested)
            {
                deliveryMode = "active-app";
                pasteResult = await _activeAppPasteService.PasteTextAsync(textToUse, _pasteTargetWindow);
                DictationStatus = persistenceSucceeded ? "Pasted" : "Pasted; history save failed";
                _toastNotificationService.Show(
                    persistenceSucceeded ? "Pasted" : "Pasted; history save failed",
                    persistenceSucceeded ? "Transcript delivered to the original app" : "The delivered text remains visible in this dashboard",
                    persistenceSucceeded ? ToastState.Success : ToastState.Error);
            }
            else
            {
                var clipboardMs = await _activeAppPasteService.CopyTextAsync(textToUse);
                pasteResult = new PasteOperationResult(
                    clipboardMs,
                    clipboardMs,
                    0,
                    0,
                    false);
                DictationStatus = persistenceSucceeded ? "Copied" : "Copied; history save failed";
                _toastNotificationService.Show(
                    persistenceSucceeded ? "Copied" : "Copied; history save failed",
                    persistenceSucceeded ? "Transcript copied to clipboard" : "The copied text remains visible in this dashboard",
                    persistenceSucceeded ? ToastState.Success : ToastState.Error);
            }

            deliveryStarted.Stop();
        }
        catch (Exception exception)
        {
            deliveryStarted.Stop();
            deliverySucceeded = false;
            var fallbackCopied = false;
            long fallbackClipboardMs = 0;
            if (!activeAppDeliveryRequested)
            {
                try
                {
                    fallbackClipboardMs = await _activeAppPasteService.CopyTextAsync(textToUse);
                    fallbackCopied = true;
                }
                catch (Exception clipboardException)
                {
                    _logService.Error("Clipboard fallback failed; the transcript remains in dictation history.", clipboardException);
                }
            }
            else
            {
                // Active-app delivery is clipboard-safe. If focus or direct input fails,
                // keep the user's clipboard untouched and let dictation history be the
                // recovery path instead of replacing a deliberate copy with a fallback.
                _logService.Info("Active-app delivery failed; transcript remains in dictation history and the clipboard was left unchanged.");
            }
            pasteResult = new PasteOperationResult(
                deliveryStarted.ElapsedMilliseconds,
                fallbackClipboardMs,
                0,
                0,
                false);
            DictationStatus = activeAppDeliveryRequested
                ? persistenceSucceeded
                    ? "Paste failed; transcript saved in history; clipboard unchanged"
                    : "Paste failed; transcript remains visible in the dashboard; clipboard unchanged"
                : fallbackCopied
                    ? persistenceSucceeded
                        ? "Paste failed; transcript copied and saved in history"
                        : "Paste failed; transcript copied and visible in the dashboard"
                    : persistenceSucceeded
                        ? "Paste and clipboard failed; transcript saved in history"
                        : "Delivery and history save failed; transcript remains visible in the dashboard";
            _logService.Error("Active-app paste failed after successful dictation.", exception);
            _toastNotificationService.Show(
                "Dictation saved",
                activeAppDeliveryRequested
                    ? "Paste failed; clipboard unchanged. Open dictation history to recover the transcript."
                    : fallbackCopied ? "Paste failed; copied to clipboard" : "Open dictation history to recover it",
                ToastState.Error);
        }

        var releaseToPasteMs = releaseToPasteStarted.ElapsedMilliseconds;
        releaseToPasteStarted.Stop();

        var latency = stopResult?.Latency ??
                      new DictationLatencyMetrics(traceId, 0, 0, 0, 0, "unavailable", 0, 0);
        _logService.Info(
            $"Dictation latency trace. trace={traceId}; status={(deliverySucceeded && persistenceSucceeded ? "success" : !persistenceSucceeded ? "history-failed" : "paste-failed")}; engine={_dictationCoordinator.EngineId}; model={_dictationCoordinator.ModelId}; audioDurationMs={result.DurationMs}; chars={textToUse.Length}; releaseToPasteMs={releaseToPasteMs}; releaseToUiSettledMs={releaseToPasteStarted.ElapsedMilliseconds}; captureTotalMs={latency.CaptureTotalMs}; captureStopDisposeMs={latency.CaptureStopDisposeMs}; captureFlushWaitMs={latency.CaptureFlushWaitMs}; capturePreparationMs={latency.CapturePreparationMs}; capturePreparation={TraceValue(latency.CapturePreparation)}; transcriptionWallMs={latency.TranscriptionWallMs}; coordinatorTotalMs={latency.CoordinatorTotalMs}; cleanupDictionaryMs={cleanupStarted.ElapsedMilliseconds}; fillerRemoval={RemoveFillerWords}; deliveryMode={deliveryMode}; deliveryMs={deliveryStarted.ElapsedMilliseconds}; clipboardMs={pasteResult.ClipboardMs}; focusWaitMs={pasteResult.FocusWaitMs}; inputMs={pasteResult.InputMs}; targetForeground={pasteResult.TargetWasForeground}; targetProcess={TraceValue(_pasteTargetInfo.ProcessName)}; targetProcessId={_pasteTargetInfo.ProcessId}; historyPersisted={persistenceSucceeded}; persistenceUiMs={persistenceStarted.ElapsedMilliseconds}");
    }
    else
    {
        releaseToPasteStarted.Stop();
        if (result.DurationMs < HotkeyTriggerTiming.ShortDiscardMilliseconds
            || (result.Diagnostic ?? "").StartsWith("Short discard", StringComparison.Ordinal))
        {
            DictationStatus = "Ready";
            _logService.Info($"Discarded short recording. Diagnostic: {result.Diagnostic}");
            _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
            return;
        }

        var diagnostic = blankAudioDetected
            ? "Switch microphone to System default or your headset mic."
            : FirstDiagnosticLine(result.Diagnostic);
        DictationStatus = blankAudioDetected
            ? "No voice detected. Check microphone input."
            : string.IsNullOrWhiteSpace(diagnostic) ? "No speech detected" : $"No speech detected. {diagnostic}";
        _logService.Info($"No speech detected. Diagnostic: {result.Diagnostic}");
        _logService.Info(
            $"Dictation latency trace. trace={traceId}; status=no-speech; engine={_dictationCoordinator.EngineId}; model={_dictationCoordinator.ModelId}; audioDurationMs={result.DurationMs}; chars=0; releaseToPasteMs=0; releaseToUiSettledMs={releaseToPasteStarted.ElapsedMilliseconds}; captureTotalMs={stopResult?.Latency.CaptureTotalMs ?? 0}; transcriptionWallMs={stopResult?.Latency.TranscriptionWallMs ?? 0}; cleanupDictionaryMs={cleanupStarted.ElapsedMilliseconds}");
        _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
        _toastNotificationService.Show(blankAudioDetected ? "No voice detected" : "No speech detected", diagnostic, ToastState.Error, 3600);
    }
}
private async Task StopActiveRecordingFromIndicatorAsync()
{
    await Dispatcher.InvokeAsync(async () =>
    {
        if (_computerUseVoiceCaptureActive)
        {
            await StopComputerUseVoiceCaptureAndRunAsync();
            return;
        }
        if (_isMeetingRecording)
        {
            await ToggleMeetingRecordingAsync(null);
            return;
        }
        await StopDictationAsync();
    }).Task.Unwrap();
}
private async Task CancelActiveRecordingFromIndicatorAsync()
{
    await Dispatcher.InvokeAsync(async () =>
    {
        if (_computerUseVoiceCaptureActive || _computerUseIsRunning)
        {
            await CancelComputerUseAsync();
            return;
        }
        if (_isMeetingRecording || _meetingPausedByUser)
        {
            if (!ConfirmDiscardMeeting())
            {
                return;
            }

            await CancelMeetingRecordingAsync();
            return;
        }
        if (_meetingRecordingCoordinator.State is MeetingSessionState.Stopping or MeetingSessionState.Finalizing)
        {
            _meetingOperationCancellation?.Cancel();
            return;
        }
        await CancelDictationAsync();
    }).Task.Unwrap();
}
private async Task CancelMeetingRecordingAsync()
{
    StopMeetingAutoStopMonitor();
    _meetingOperationCancellation?.Cancel();
    await _meetingRecordingCoordinator.CancelAsync();
    _currentMeetingTitle = null;
    _isMeetingRecording = false;
    _liveTranscriptWindow?.Hide();
    _meetingAutoStopTracker = null;
    OnPropertyChanged(nameof(MeetingRecordingButtonText));
    DictationStatus = "Meeting recording cancelled";
    _meetingPausedByUser = false;
    _toastNotificationService.SetMeetingPaused(false);
    _toastNotificationService.ReleaseLive(IndicatorOwner.Meeting);
    RefreshRecoverableMeetingSessions();
}

private async Task CancelDictationAsync()
{
    if (_computerUseVoiceCaptureActive || _computerUseIsRunning)
    {
        await CancelComputerUseAsync();
        return;
    }
    var operationCancellation = _dictationOperationCancellation;
    if (operationCancellation is not null)
    {
        operationCancellation.Cancel();
    }

    await _dictationCoordinator.CancelAsync();

    ResetHotkeyDictationState();
    _pasteTargetWindow = IntPtr.Zero;
    _pasteTargetInfo = PasteTargetInfo.Unknown;
    _shouldPasteToActiveApp = false;
    DictationStatus = "Dictation cancelled";
    _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
}

private void CompleteDictationOperationCancellation(CancellationTokenSource cancellation)
{
    if (ReferenceEquals(_dictationOperationCancellation, cancellation))
    {
        _dictationOperationCancellation = null;
    }
    cancellation.Dispose();
}

private void HotkeyReleaseTimer_Tick(object? sender, EventArgs e) =>
    _ = HandleHotkeyReleaseTimerTickAsync();

private async Task HandleHotkeyReleaseTimerTickAsync()
{
    _hotkeyReleaseTimer.Stop();
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.DoubleTapWindowElapsed());
}
private void HotkeyPrepareTimer_Tick(object? sender, EventArgs e)
{
    _hotkeyPrepareTimer.Stop();
    _ = HandleHotkeyPrepareTimerTickAsync();
}

private async Task HandleHotkeyPrepareTimerTickAsync() =>
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.PrepareDelayElapsed());

private void HotkeyStartTimer_Tick(object? sender, EventArgs e)
{
    _hotkeyStartTimer.Stop();
    _ = HandleHotkeyStartTimerTickAsync();
}

private async Task HandleHotkeyStartTimerTickAsync() =>
    await ExecuteHotkeyActionAsync(_dictationHotkeyState.StartDelayElapsed());

private void StopHotkeyDelayTimers()
{
    _hotkeyPrepareTimer.Stop();
    _hotkeyStartTimer.Stop();
    _hotkeyReleaseTimer.Stop();
}

private void ResetHotkeyDictationState()
{
    StopHotkeyDelayTimers();
    _dictationHotkeyState.Reset();
}

private void OnDictationDeviceListChanged(object? sender, EventArgs e)
{
    Dispatcher.BeginInvoke(() =>
    {
        var selected = SelectedMicrophone;
        var devices = _dictationCoordinator.ListMicrophones();
        MicrophoneDevices.Clear();
        foreach (var device in devices)
        {
            MicrophoneDevices.Add(device);
        }
        if (!string.IsNullOrWhiteSpace(selected) && !MicrophoneDevices.Contains(selected))
        {
            MicrophoneDevices.Add(selected);
        }
        _selectedMicrophone = selected ?? AudioCaptureService.SystemDefaultMicrophone;
        OnPropertyChanged(nameof(SelectedMicrophone));
    });
}

private void OnDictationRouteChanged(object? sender, AudioRouteChangedEventArgs e)
{
    Dispatcher.BeginInvoke(() =>
    {
        DictationStatus = e.Message;
        if (e.Kind == AudioRouteChangeKind.Failed)
        {
            _logService.Error(
                $"Dictation microphone route recovery failed. previous={TraceValue(e.PreviousDevice)}; current={TraceValue(e.CurrentDevice)}",
                e.Exception ?? new InvalidOperationException(e.Message));
            _toastNotificationService.ReleaseLive(IndicatorOwner.Dictation);
            _toastNotificationService.Show("Microphone disconnected", "Recording stopped; release the shortcut to recover captured audio", ToastState.Error, 3600);
            return;
        }

        _logService.Info(
            $"Dictation microphone route changed. kind={e.Kind}; previous={TraceValue(e.PreviousDevice)}; current={TraceValue(e.CurrentDevice)}");
            _toastNotificationService.Show("Microphone route changed", e.Message, ToastState.Success, 2200);
    });
}

private void OnDictationLevelChanged(object? sender, AudioLevelEventArgs e) =>
    _toastNotificationService.UpdateRecordingLevel(e.Peak);

private void TestMic_Click(object sender, RoutedEventArgs e) => _ = TestMicAsync();

private async Task TestMicAsync()
{
    if (_dictationCoordinator.IsBusy || _dictationCoordinator.IsRecording)
    {
        return;
    }
    try
    {
        DictationStatus = "Testing microphone for 2 seconds";
        _toastNotificationService.Show("Testing microphone", SelectedMicrophone ?? "Selected microphone", ToastState.Recording, 2200);
        await _dictationCoordinator.StartAsync(SelectedMicrophone);
        await Task.Delay(2000);
        var result = await _dictationCoordinator.StopAsync();
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
private void DictationText_LostFocus(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.TextBox box || box.DataContext is not DictationItem item)
    {
        return;
    }

    var edited = box.Text?.Trim() ?? "";
    if (string.Equals(edited, item.Text, StringComparison.Ordinal))
    {
        return;
    }

    var suggestions = DictionarySuggestionDetector.FromEdit(item.Text, edited);
    var index = Dictations.IndexOf(item);
    if (index >= 0)
    {
        Dictations[index] = item with { Text = edited };
        SaveDictations();
        FilteredDictations.Refresh();
        OnPropertyChanged(nameof(InsightsSummary));
    }

    var suggestion = suggestions.FirstOrDefault();
    if (suggestion is null)
    {
        return;
    }

    QueueDictionarySuggestion(suggestion);
    _dictionarySuggestionPrompt.Show(suggestion, () => AcceptDictionarySuggestion(suggestion));
}

private void DictationRow_Click(object sender, MouseButtonEventArgs e) => _ = CopyDictationRowAsync(sender, e);

private async Task CopyDictationRowAsync(object sender, MouseButtonEventArgs e)
{
    // Ignore clicks on buttons (Copy/Delete icons)
    if (e.OriginalSource is System.Windows.Controls.Button or System.Windows.Controls.Image or System.Windows.Controls.TextBox)
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
        SaveDictations(afterExplicitDeletion: true);
        OnPropertyChanged(nameof(DayStreak));
        OnPropertyChanged(nameof(WordsDictated));
        OnPropertyChanged(nameof(WordsDictatedDisplay));
        OnPropertyChanged(nameof(AverageWpm));
        OnPropertyChanged(nameof(InsightsSummary));
        RefreshSearchResults();
        DictationStatus = "Deleted dictation";
    }
}
}
