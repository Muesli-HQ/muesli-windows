using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// WinUI Computer Use host: settings validation, explicit voice capture, modeless confirmation,
/// status, and redacted trace diagnostics. UI Automation execution adapters remain in the WPF host.
/// </summary>
public sealed class WinUiComputerUseContext : IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiSettingsContext _settings;
    private readonly WinUiDictationContext _dictation;
    private readonly ComputerUseTraceStore _trace = new();
    private readonly AppLogService _log = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private ComputerUseWindowTarget? _target;
    private ComputerUsePlannerService? _planner;
    private ComputerUseActivationToken? _activation;
    private readonly GlobalHotkeyService _voiceHotkey = new();
    public Action? CaptureStarted { get; set; }
    public Func<ComputerUseAction, CancellationToken, Task<bool>>? ConfirmActionAsync { get; set; }

    public void RegisterVoiceShortcut(IUiDispatcher dispatcher)
    {
        _voiceHotkey.Register("Ctrl+Shift+F8", () => dispatcher.TryEnqueue(() => _ = ToggleVoiceAsync()),
            () => { }, () => StopEnabled, () => dispatcher.TryEnqueue(() => _ = CancelAsync()));
    }
    private CancellationTokenSource? _sessionCancellation;
    private bool _listening;
    private bool _running;
    private int _disposed;

    public WinUiComputerUseContext(
        WinUiLibraryContext library,
        WinUiSettingsContext settings,
        WinUiDictationContext dictation)
    {
        _library = library;
        _settings = settings;
        _dictation = dictation;
        Status = "Computer Use is disabled by default.";
    }

    public event EventHandler? Changed;

    public event EventHandler<ComputerUseConfirmationRequest>? ConfirmationRequested;

    public string Status { get; private set; }

    public bool IsListening => _listening;

    public bool IsRunning => _running;

    public bool StopEnabled => _listening || _running;

    public string VoiceButtonText => _listening ? "Stop listening" : "Speak planner command";

    public string Diagnostics { get; private set; } = "No Computer Use runs are recorded.";

    public string TraceFilePath => ComputerUseConfiguration.TracePath(_library.Profile.RootDirectory);

    public void RefreshDiagnostics()
    {
        Diagnostics = ComputerUseConfiguration.FormatDiagnostics(_trace.Load(TraceFilePath));
        RaiseChanged();
    }

    public void PreviewConfirmation(ComputerUseAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ConfirmationRequested?.Invoke(
            this,
            new ComputerUseConfirmationRequest(
                ComputerUseConfirmationPreview.Build(action),
                ComputerUseConfiguration.WinUiExecutionHostNotice));
    }

    public async Task ToggleVoiceAsync()
    {
        ThrowIfDisposed();
        if (_listening)
        {
            await StopListeningAsync();
            return;
        }

        await StartListeningAsync();
    }

    public async Task CancelAsync()
    {
        _sessionCancellation?.Cancel();
        if (_listening && _dictation.IsRecording)
        {
            await _dictation.CancelAsync();
        }
        ResetSession();
        Status = "Computer Use stopped; the planner session was discarded.";
        RaiseChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _http.Dispose();
        _voiceHotkey.Dispose();
    }

    private async Task StartListeningAsync()
    {
        var settings = _settings.Load();
        if (!settings.ComputerUseEnabled)
        {
            Status = "Enable Computer Use before starting a planner session.";
            RaiseChanged();
            return;
        }
        if (!ComputerUseConfiguration.TryValidate(settings, settings.ResolvedOpenAIApiKey, out var error))
        {
            Status = error;
            RaiseChanged();
            return;
        }
        if (_dictation.IsRecording || _dictation.IsBusy)
        {
            Status = "Finish the active dictation first.";
            RaiseChanged();
            return;
        }

        if (_running) return;
        _planner = CreatePlanner(settings);
        _activation = _planner.BeginExplicitVoicePlannerActivation();

        _sessionCancellation?.Dispose();
        _sessionCancellation = new CancellationTokenSource();
        try
        {
            await _dictation.StartAuxiliaryAsync(settings.MicrophoneName);
            _listening = _dictation.IsRecording;
            if (!_listening)
            {
                throw new InvalidOperationException("The microphone did not enter the recording state.");
            }
            Status = "Planner listening. Focus an allowed target app and press Ctrl+Shift+F8 to finish.";
            CaptureStarted?.Invoke();
        }
        catch (Exception exception)
        {
            ResetSession();
            Status = $"Could not start planner voice capture ({exception.GetType().Name}).";
            _log.Info("Computer Use voice capture could not start; commandLogged=false.");
        }
        RaiseChanged();
    }

    private async Task StopListeningAsync()
    {
        if (!_listening) return;
        var settings = _settings.Load();
        _target = CaptureAllowedTarget(settings);
        _listening = false;
        _running = true;
        RaiseChanged();
        try
        {
            Status = "Transcribing the explicit planner command locally.";
            var transcript = await _dictation.StopWithoutPersistingAsync(
                _sessionCancellation?.Token ?? CancellationToken.None);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Status = "No planner command was detected; nothing was executed.";
                return;
            }

            if (_target is null || _planner is null || _activation is null ||
                !_planner.TryCreateExplicitVoiceCommand(_activation, transcript, out var command) || command is null)
            {
                Status = "Focus an allowed application before stopping voice capture. Nothing was sent or executed.";
                return;
            }
            var result = await _planner.RunAsync(command, ComputerUseConfiguration.BuildOptions(settings),
                _sessionCancellation?.Token ?? CancellationToken.None);
            _trace.Append(TraceFilePath, result);
            Status = ComputerUseConfiguration.DescribeResult(result);
        }
        catch (OperationCanceledException)
        {
            Status = "Computer Use was stopped; no further actions were sent.";
        }
        catch (Exception)
        {
            Status = "Computer Use failed safely; no further actions were sent.";
            _log.Info("Computer Use failed safely; commandLogged=false; valuesLogged=false.");
        }
        finally
        {
            ResetSession();
            RaiseChanged();
        }
    }

    private void ResetSession()
    {
        _listening = false;
        _running = false;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        _target = null;
        _planner = null;
        _activation = null;
    }

    private ComputerUsePlannerService CreatePlanner(MuesliSettings settings)
    {
        var sessions = new List<KeyValuePair<string, IExplicitBrowserSession>>();
        var browserContexts = new List<LoopbackDevToolsBrowserSession>();
        if (settings.ComputerUseBrowserInterface == "loopback-devtools" &&
            Uri.TryCreate(settings.ComputerUseBrowserEndpoint, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback)
        {
            foreach (var domain in ComputerUseConfiguration.ParseAllowlist(settings.ComputerUseAllowedBrowserDomains))
            {
                var session = new LoopbackDevToolsBrowserSession(_http, endpoint.Port, domain);
                sessions.Add(new(domain, session));
                browserContexts.Add(session);
            }
        }
        async Task<string?> BrowserContextAsync(CancellationToken token)
        {
            if (_target?.ApplicationId is not ("chrome" or "msedge")) return null;
            foreach (var browser in browserContexts)
            {
                var fingerprint = await browser.CaptureContextFingerprintAsync(token);
                if (!string.IsNullOrEmpty(fingerprint)) return fingerprint;
            }
            return null;
        }
        return new ComputerUsePlannerService(
            new WindowsUiAutomationObservationSource(() => _target, browserContexts.Count == 0 ? null : BrowserContextAsync),
            new OpenAiResponsesPlannerProvider(_http, () =>
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } key ? key : _settings.Load().ResolvedOpenAIApiKey),
            new WindowsUiAutomationExecutor(new RegisteredBrowserAutomationAdapter(sessions), new WindowsUiAutomationLocalAdapter(() => _target)),
            new Confirmation(this));
    }

    private static ComputerUseWindowTarget? CaptureAllowedTarget(MuesliSettings settings)
    {
        try
        {
            var handle = GetForegroundWindow();
            _ = GetWindowThreadProcessId(handle, out var processId);
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            var application = process.ProcessName.ToLowerInvariant();
            return ComputerUseConfiguration.ParseAllowlist(settings.ComputerUseAllowedApplications)
                .Contains(application, StringComparer.OrdinalIgnoreCase) &&
                ComputerUseWindowTarget.TryCapture(handle, application, processId, out var target) ? target : null;
        }
        catch { return null; }
    }

    private sealed class Confirmation(WinUiComputerUseContext owner) : IComputerUseConfirmation
    {
        public Task<bool> ConfirmAsync(ComputerUseAction action, CancellationToken token) =>
            owner.ConfirmActionAsync?.Invoke(action, token) ?? Task.FromResult(false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed record ComputerUseConfirmationRequest(string Preview, string Notice);
