using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
public ObservableCollection<string> ComputerUsePlannerProviders { get; } = ["None", "OpenAI"];
public ObservableCollection<string> ComputerUseBrowserInterfaces { get; } = ["Disabled", "Loopback DevTools"];

private void ComputerUseVoice_Click(object sender, RoutedEventArgs e) => _ = ComputerUseVoiceAsync();

private async Task ComputerUseVoiceAsync()
{
    if (_computerUseVoiceCaptureActive)
    {
        await StopComputerUseVoiceCaptureAndRunAsync();
        return;
    }
    await StartComputerUseVoiceCaptureAsync();
}

private async Task StartComputerUseVoiceCaptureAsync()
{
    var configurationReady = ComputerUseConfigurationIsReady(out var error);
    if (!ComputerUseEnabled || !configurationReady)
    {
        ComputerUseStatusText = error.Length == 0 ? "Enable Computer Use before starting a planner session." : error;
        return;
    }
    if (!WorkflowEntryPoints.CanStartComputerUse(
            ComputerUseEnabled,
            configurationReady,
            _computerUseIsRunning,
            _dictationCoordinator.IsRecording,
            _dictationCoordinator.IsBusy,
            _isMeetingRecording))
    {
        ComputerUseStatusText = "Finish the active recording or planner operation first.";
        return;
    }

    _computerUseApprovedTarget = null;
    _computerUsePlannerService = CreateComputerUsePlannerService();
    _computerUseActivationToken = _computerUsePlannerService.BeginExplicitVoicePlannerActivation();
    _computerUseCancellation?.Dispose();
    _computerUseCancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationShutdownCancellation.Token);
    try
    {
        await _dictationCoordinator.StartAsync(SelectedMicrophone, DictationSessionKind.Auxiliary);
        _computerUseVoiceCaptureActive = _dictationCoordinator.IsRecording;
        NotifyComputerUseActivityChanged();
        if (!_computerUseVoiceCaptureActive)
        {
            throw new InvalidOperationException("The microphone did not enter the recording state.");
        }
        ComputerUseStatusText = "Planner listening. Focus an allowed target app, then use the floating stop control.";
        _toastNotificationService.ShowLive(IndicatorOwner.ComputerUse, "Computer Use listening", "Focus an allowed app, then click the square to plan", ToastState.Recording);
        _shell.Window.WindowState = WindowState.Minimized;
    }
    catch (Exception exception)
    {
        ResetComputerUseSession();
        ComputerUseStatusText = $"Could not start planner voice capture ({exception.GetType().Name}).";
        _logService.Info("Computer Use voice capture could not start; commandLogged=false.");
        _toastNotificationService.ReleaseLive(IndicatorOwner.ComputerUse);
        _toastNotificationService.Show("Computer Use unavailable", "Voice capture could not start", ToastState.Error, 4200);
    }
}

private async Task StopComputerUseVoiceCaptureAndRunAsync()
{
    if (!_computerUseVoiceCaptureActive || _computerUsePlannerService is null || _computerUseActivationToken is null)
        return;

    // Capture only the single foreground target selected by the user while Muesli is minimized.
    // No clipboard, window enumeration, title, or field value is acquired here.
    var targetHandle = _activeAppPasteService.CaptureForegroundWindow();
    var targetInfo = _activeAppPasteService.DescribeWindow(targetHandle);
    var applicationId = targetInfo.ProcessName?.Trim().ToLowerInvariant() ?? "";
    var allowedApplicationsAtSelection = ParseAllowlist(ComputerUseAllowedApplications);
    var targetWasAllowlistedAtSelection = targetHandle != IntPtr.Zero && applicationId.Length > 0 &&
        allowedApplicationsAtSelection.Contains(applicationId, StringComparer.OrdinalIgnoreCase);
    ComputerUseWindowTarget? capturedTarget = null;
    var targetIdentityCapturedAtSelection = targetWasAllowlistedAtSelection &&
        ComputerUseWindowTarget.TryCapture(targetHandle, applicationId, targetInfo.ProcessId, out capturedTarget);
    _computerUseVoiceCaptureActive = false;
    _computerUseIsRunning = true;
    NotifyComputerUseActivityChanged();

    try
    {
        ComputerUseStatusText = "Transcribing the explicit planner command locally.";
        _toastNotificationService.ShowLive(IndicatorOwner.ComputerUse, "Computer Use", "Transcribing the explicit command", ToastState.Transcribing);
        var stopResult = await _dictationCoordinator.StopAsync(
            $"computer-use-{Guid.NewGuid():N}",
            _computerUseCancellation?.Token ?? _applicationShutdownCancellation.Token);
        var transcript = stopResult.Transcription.Text?.Trim() ?? "";
        if (transcript.Length == 0)
        {
            ComputerUseStatusText = "No planner command was detected; nothing was executed.";
            return;
        }
        if (!targetWasAllowlistedAtSelection)
        {
            ComputerUseStatusText = "The selected foreground application is not allowlisted; nothing was sent to the planner.";
            return;
        }
        if (!targetIdentityCapturedAtSelection || capturedTarget is null || !capturedTarget.IsCurrentOwner())
        {
            ComputerUseStatusText = "The selected application window changed before it could be approved; nothing was sent to the planner.";
            return;
        }
        _computerUseApprovedTarget = capturedTarget;
        if (!_computerUsePlannerService.TryCreateExplicitVoiceCommand(_computerUseActivationToken, transcript, out var command) || command is null)
        {
            ComputerUseStatusText = "The explicit planner activation expired or was already consumed; nothing was executed.";
            return;
        }

        ComputerUseStatusText = "Planning from the approved foreground window.";
        var result = await _computerUsePlannerService.RunAsync(
            command,
            CurrentComputerUseOptions(),
            _computerUseCancellation?.Token ?? _applicationShutdownCancellation.Token);
        try
        {
            _computerUseTraceStore.Append(ComputerUseTracePath(), result);
        }
        catch (Exception)
        {
            _logService.Info($"Computer Use trace persistence failed. run={result.RunId}; commandLogged=false; valuesLogged=false.");
        }
        ComputerUseStatusText = DescribeComputerUseResult(result);
        _logService.Info($"Computer Use completed. run={result.RunId}; status={result.Status}; actions={result.Trace.Count}; commandLogged=false; valuesLogged=false.");
    }
    catch (OperationCanceledException)
    {
        ComputerUseStatusText = "Computer Use was stopped; no further actions were sent.";
    }
    catch (Exception exception)
    {
        ComputerUseStatusText = $"Computer Use failed safely ({exception.GetType().Name}); no further actions were sent.";
        _logService.Info("Computer Use failed safely; commandLogged=false; valuesLogged=false.");
    }
    finally
    {
        ResetComputerUseSession();
        _toastNotificationService.ReleaseLive(IndicatorOwner.ComputerUse);
        if (_shell.Window.IsVisible)
        {
            _shell.Window.WindowState = WindowState.Normal;
            _shell.Window.Activate();
        }
    }
}

private void StopComputerUse_Click(object sender, RoutedEventArgs e) => _ = CancelComputerUseAsync();

private async Task CancelComputerUseAsync()
{
    _computerUseCancellation?.Cancel();
    if (_computerUseVoiceCaptureActive && _dictationCoordinator.IsRecording && !_dictationCoordinator.IsBusy)
    {
        await _dictationCoordinator.CancelAsync();
    }
    ResetComputerUseSession();
    ComputerUseStatusText = "Computer Use stopped; the planner session was discarded.";
    _toastNotificationService.ReleaseLive(IndicatorOwner.ComputerUse);
}

private void OpenComputerUseDiagnostics_Click(object sender, RoutedEventArgs e)
{
    var document = _computerUseTraceStore.Load(ComputerUseTracePath());
    var rows = document.Runs.Count == 0
        ? "No Computer Use runs are recorded."
        : string.Join(Environment.NewLine, document.Runs.Reverse().Take(20).Select(run =>
            $"{run.CompletedAtUtc.LocalDateTime:g}  {run.Status}  actions={run.Actions.Count}  run={run.RunId}"));
    System.Windows.MessageBox.Show(
        $"Schema: {document.SchemaVersion}\nStored runs: {document.Runs.Count}\n\n{rows}\n\nCommands, typed values, titles, URLs, element names, screenshots, provider bodies, and errors are never stored.",
        "Computer Use diagnostics",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
}

private void ResetComputerUseSession()
{
    _computerUseVoiceCaptureActive = false;
    _computerUseIsRunning = false;
    _computerUseActivationToken = null;
    _computerUsePlannerService = null;
    _computerUseApprovedTarget = null;
    _computerUseCancellation?.Dispose();
    _computerUseCancellation = null;
    NotifyComputerUseActivityChanged();
}

private void NotifyComputerUseActivityChanged()
{
    OnPropertyChanged(nameof(ComputerUseVoiceButtonText));
    OnPropertyChanged(nameof(ComputerUseStopEnabled));
}
private static string ComputerUseProviderDisplay(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "openai" => "OpenAI",
    _ => "None"
};

private static string ComputerUseProviderSetting(string? value) => value switch
{
    "OpenAI" => "openai",
    _ => "none"
};

private static string ComputerUseBrowserInterfaceDisplay(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "loopback-devtools" => "Loopback DevTools",
    _ => "Disabled"
};

private static string ComputerUseBrowserInterfaceSetting(string? value) => value switch
{
    "Loopback DevTools" => "loopback-devtools",
    _ => "none"
};

private static string NormalizeAllowlistText(string? value) => string.Join("; ",
    (value ?? "")
    .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(entry => entry.ToLowerInvariant())
    .Distinct(StringComparer.OrdinalIgnoreCase));

private static string[] ParseAllowlist(string? value) => (value ?? "")
    .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(entry => entry.ToLowerInvariant())
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

private bool ComputerUseConfigurationIsReady(out string error)
{
    if (!SelectedComputerUsePlannerProvider.Equals("OpenAI", StringComparison.Ordinal))
    {
        error = "Choose the OpenAI planner provider explicitly.";
        return false;
    }
    if (string.IsNullOrWhiteSpace(ComputerUsePlannerModel))
    {
        error = "Choose a planner model explicitly.";
        return false;
    }
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) &&
        string.IsNullOrWhiteSpace(OpenAIApiKey))
    {
        error = "Configure the OpenAI key before enabling Computer Use.";
        return false;
    }
    if (ParseAllowlist(ComputerUseAllowedApplications).Length == 0)
    {
        error = "Allow at least one application before enabling Computer Use.";
        return false;
    }
    if (ComputerUseIncludeWindowText || ComputerUseIncludeBrowserPageText || ComputerUseIncludeScreenshots)
    {
        error = "Text, page-text, and screenshot observation remain unavailable until scoped masking is verified. Leave those privacy options off.";
        return false;
    }
    error = "";
    return true;
}

private void DisableComputerUseIfConfigurationBecameInvalid()
{
    if (!_computerUseEnabled || ComputerUseConfigurationIsReady(out var error)) return;
    _computerUseEnabled = false;
    OnPropertyChanged(nameof(ComputerUseEnabled));
    ComputerUseStatusText = error;
}

private ComputerUsePlannerService CreateComputerUsePlannerService()
{
    Func<ComputerUseWindowTarget?> target = () => _computerUseApprovedTarget;
    var browserSessions = new List<KeyValuePair<string, IExplicitBrowserSession>>();
    var browserContextSessions = new List<LoopbackDevToolsBrowserSession>();
    if (SelectedComputerUseBrowserInterface == "Loopback DevTools" &&
        Uri.TryCreate(ComputerUseBrowserEndpoint, UriKind.Absolute, out var devToolsEndpoint) &&
        devToolsEndpoint.Scheme == Uri.UriSchemeHttp && devToolsEndpoint.IsLoopback)
    {
        foreach (var domain in ParseAllowlist(ComputerUseAllowedBrowserDomains))
        {
            try
            {
                var session = new LoopbackDevToolsBrowserSession(_computerUseHttpClient, devToolsEndpoint.Port, domain);
                browserSessions.Add(new KeyValuePair<string, IExplicitBrowserSession>(
                    domain,
                    session));
                browserContextSessions.Add(session);
            }
            catch (ArgumentException)
            {
                // Invalid domains are omitted from the capability registry and fail closed.
            }
        }
    }
    async Task<string?> CaptureBrowserContextAsync(CancellationToken cancellationToken)
    {
        var approvedTarget = target();
        if (approvedTarget is null || approvedTarget.ApplicationId is not ("chrome" or "msedge")) return null;
        foreach (var session in browserContextSessions)
        {
            var fingerprint = await session.CaptureContextFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fingerprint)) return fingerprint;
        }
        return null;
    }
    var observations = new WindowsUiAutomationObservationSource(
        target,
        browserContextSessions.Count == 0 ? null : CaptureBrowserContextAsync);
    var local = new WindowsUiAutomationLocalAdapter(target);
    IBrowserAutomationAdapter browser = new RegisteredBrowserAutomationAdapter(browserSessions);
    var executor = new WindowsUiAutomationExecutor(browser, local);
    var planner = new OpenAiResponsesPlannerProvider(
        _computerUseHttpClient,
        () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } environmentKey
            ? environmentKey
            : OpenAIApiKey);
    var status = new DelegateComputerUseStatusSink(value => Dispatcher.BeginInvoke(() =>
    {
        ComputerUseStatusText = value.ActionCount > 0
            ? $"{value.Message} Action {value.ActionNumber} of {value.ActionCount}."
            : value.Message;
    }));
    var confirmation = new DelegateComputerUseConfirmation((action, cancellationToken) =>
        Dispatcher.InvokeAsync(() => ComputerUseConfirmationPrompt.ShowAsync(_shell.Window, action, cancellationToken)).Task.Unwrap());
    var visualizer = new DelegateComputerUseActionVisualizer((action, targetElement, cancellationToken) =>
        Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = targetElement is null
                ? ""
                : $" at ({targetElement.Left}, {targetElement.Top})";
            ComputerUseStatusText = $"Proposed {action.Kind}{location}; validating policy and confirmation.";
            _toastNotificationService.ShowLive(IndicatorOwner.ComputerUse, "Computer Use action", $"{action.Kind}{location}", ToastState.Transcribing);
        }).Task);
    return new ComputerUsePlannerService(observations, planner, executor, confirmation, status, visualizer);
}

private ComputerUseOptions CurrentComputerUseOptions()
{
    var plannerTimeout = TimeSpan.FromSeconds(Math.Clamp(ComputerUsePlannerTimeoutSeconds, 5, 120));
    var perActionTimeout = TimeSpan.FromSeconds(Math.Clamp(ComputerUsePerActionTimeoutSeconds, 1, 30));
    var maximumActions = Math.Clamp(ComputerUseMaximumActionCount, 1, 20);
    var overallSeconds = Math.Clamp(
        (int)plannerTimeout.TotalSeconds + maximumActions * (int)perActionTimeout.TotalSeconds + 30,
        10,
        300);
    return new ComputerUseOptions
    {
        Enabled = ComputerUseEnabled,
        Provider = ComputerUsePlannerProvider.OpenAI,
        Model = ComputerUsePlannerModel,
        PlannerTimeout = plannerTimeout,
        OverallTimeout = TimeSpan.FromSeconds(overallSeconds),
        PerActionTimeout = perActionTimeout,
        MaximumActionCount = maximumActions,
        AllowedApplications = ParseAllowlist(ComputerUseAllowedApplications),
        AllowedBrowserDomains = ParseAllowlist(ComputerUseAllowedBrowserDomains),
        Privacy = new ComputerUsePrivacyOptions
        {
            IncludeWindowText = ComputerUseIncludeWindowText,
            IncludeBrowserPageText = ComputerUseIncludeBrowserPageText,
            IncludeScreenshots = false,
            UserApprovedScreenCapture = false
        }
    };
}

private static string ComputerUseTracePath() => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "muesli",
    "computer-use",
    "trace.json");

private static string DescribeComputerUseResult(ComputerUseRunResult result) => result.Status switch
{
    ComputerUseRunStatus.Completed => $"Computer Use completed {result.Trace.Count} validated action(s).",
    ComputerUseRunStatus.Disabled => "Computer Use is disabled; nothing was planned or executed.",
    ComputerUseRunStatus.Rejected => result.Error ?? "The planner response or context was rejected.",
    ComputerUseRunStatus.Cancelled => "Computer Use was stopped or a required confirmation was declined.",
    ComputerUseRunStatus.TimedOut => "Computer Use timed out; no further actions were sent.",
    ComputerUseRunStatus.StaleObservation => "The target changed; no further actions were sent.",
    _ => result.Error ?? "Computer Use failed safely; no further actions were sent."
};

public bool ComputerUseEnabled
{
    get => _computerUseEnabled;
    set
    {
        if (value && !ComputerUseConfigurationIsReady(out var error))
        {
            ComputerUseStatusText = error;
            value = false;
        }
        if (SetField(ref _computerUseEnabled, value))
        {
            ComputerUseStatusText = value
                ? "Computer Use is enabled for explicit planner voice sessions only."
                : "Computer Use is disabled.";
            SaveSettings();
        }
    }
}

public string SelectedComputerUsePlannerProvider
{
    get => _selectedComputerUsePlannerProvider;
    set
    {
        var normalized = ComputerUsePlannerProviders.Contains(value) ? value : "None";
        if (!SetField(ref _selectedComputerUsePlannerProvider, normalized)) return;
        DisableComputerUseIfConfigurationBecameInvalid();
        SaveSettings();
    }
}

public string ComputerUsePlannerModel
{
    get => _computerUsePlannerModel;
    set
    {
        if (!SetField(ref _computerUsePlannerModel, value?.Trim() ?? "")) return;
        DisableComputerUseIfConfigurationBecameInvalid();
        SaveSettings();
    }
}

public int ComputerUsePlannerTimeoutSeconds
{
    get => _computerUsePlannerTimeoutSeconds;
    set { if (SetField(ref _computerUsePlannerTimeoutSeconds, Math.Clamp(value, 5, 120))) SaveSettings(); }
}

public int ComputerUsePerActionTimeoutSeconds
{
    get => _computerUsePerActionTimeoutSeconds;
    set { if (SetField(ref _computerUsePerActionTimeoutSeconds, Math.Clamp(value, 1, 30))) SaveSettings(); }
}

public int ComputerUseMaximumActionCount
{
    get => _computerUseMaximumActionCount;
    set { if (SetField(ref _computerUseMaximumActionCount, Math.Clamp(value, 1, 20))) SaveSettings(); }
}

public string ComputerUseAllowedApplications
{
    get => _computerUseAllowedApplications;
    set
    {
        if (!SetField(ref _computerUseAllowedApplications, NormalizeAllowlistText(value))) return;
        DisableComputerUseIfConfigurationBecameInvalid();
        SaveSettings();
    }
}

public string ComputerUseAllowedBrowserDomains
{
    get => _computerUseAllowedBrowserDomains;
    set { if (SetField(ref _computerUseAllowedBrowserDomains, NormalizeAllowlistText(value))) SaveSettings(); }
}

public bool ComputerUseIncludeWindowText
{
    get => _computerUseIncludeWindowText;
    set { if (SetField(ref _computerUseIncludeWindowText, value)) SaveSettings(); }
}

public bool ComputerUseIncludeScreenshots
{
    get => _computerUseIncludeScreenshots;
    set { if (SetField(ref _computerUseIncludeScreenshots, value)) SaveSettings(); }
}

public bool ComputerUseIncludeBrowserPageText
{
    get => _computerUseIncludeBrowserPageText;
    set { if (SetField(ref _computerUseIncludeBrowserPageText, value)) SaveSettings(); }
}

public string SelectedComputerUseBrowserInterface
{
    get => _selectedComputerUseBrowserInterface;
    set
    {
        var normalized = ComputerUseBrowserInterfaces.Contains(value) ? value : "Disabled";
        if (SetField(ref _selectedComputerUseBrowserInterface, normalized)) SaveSettings();
    }
}

public string ComputerUseBrowserEndpoint
{
    get => _computerUseBrowserEndpoint;
    set { if (SetField(ref _computerUseBrowserEndpoint, value?.Trim() ?? "")) SaveSettings(); }
}

public string ComputerUseStatusText
{
    get => _computerUseStatus;
    private set => SetField(ref _computerUseStatus, value);
}

public string ComputerUseVoiceButtonText => _computerUseVoiceCaptureActive
    ? "Stop listening and plan"
    : "Speak planner command";

public bool ComputerUseStopEnabled => _computerUseVoiceCaptureActive || _computerUseIsRunning;
}
