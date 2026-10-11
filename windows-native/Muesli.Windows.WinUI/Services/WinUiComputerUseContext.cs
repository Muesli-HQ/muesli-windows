using Muesli.Windows.ComputerUse;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// WinUI Computer Use host: Ctrl+Shift+F8 starts voice capture, a second press transcribes the
/// command locally and runs it with <see cref="CuaRuntime"/> on the desktop the user was working in.
/// </summary>
public sealed class WinUiComputerUseContext : IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiSettingsContext _settings;
    private readonly WinUiDictationContext _dictation;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly GlobalHotkeyService _voiceHotkey = new();
    private CancellationTokenSource? _sessionCancellation;
    private bool _listening;
    private bool _running;
    private int _disposed;

    public WinUiComputerUseContext(WinUiLibraryContext library, WinUiSettingsContext settings, WinUiDictationContext dictation, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _library = library;
        _settings = settings;
        _dictation = dictation;
        ChatGpt = new ChatGptAuth(settings.Secrets, _http);
        Status = "Press Ctrl+Shift+F8, say what to do, then press it again.";
        if (File.Exists(TraceFilePath)) Diagnostics = File.ReadAllText(TraceFilePath);
    }

    public ChatGptAuth ChatGpt { get; }
    public Action? CaptureStarted { get; set; }
    /// <summary>Asks the user to approve a risky step; returns false when declined.</summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmActionAsync { get; set; }
    public event EventHandler? Changed;
    public string Status { get; private set; }
    public bool IsListening => _listening;
    public bool IsRunning => _running;
    public bool StopEnabled => _listening || _running;
    public string VoiceButtonText => _listening ? "Stop listening and run" : "Speak a command";
    public string Diagnostics { get; private set; } = "No Computer Use runs yet.";
    /// <summary>How the last run (or attempt) ended, for the floating pill; null once a new one starts.</summary>
    public ComputerUseOutcome? Outcome { get; private set; }
    public string TraceFilePath => ComputerUseConfiguration.TracePath(_library.Profile.RootDirectory);

    public void RegisterVoiceShortcut() =>
        _voiceHotkey.Register("Ctrl+Shift+F8", () => _dispatcher.TryEnqueue(() => _ = ToggleVoiceAsync()),
            () => { }, () => StopEnabled, () => _dispatcher.TryEnqueue(() => _ = CancelAsync()));

    public async Task SignInToChatGptAsync()
    {
        try
        {
            Report("Finish signing in to ChatGPT in your browser.");
            await ChatGpt.SignInAsync(CancellationToken.None);
            Report("Signed in to ChatGPT.");
        }
        catch (Exception exception) when (exception is ChatGptAuthException or HttpRequestException)
        {
            Report(exception.Message);
        }
    }

    public void SignOutOfChatGpt()
    {
        ChatGpt.SignOut();
        Report("Signed out of ChatGPT.");
    }

    public async Task ToggleVoiceAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listening) await StopListeningAndRunAsync();
        else await StartListeningAsync();
    }

    public async Task CancelAsync()
    {
        _sessionCancellation?.Cancel();
        if (_listening && _dictation.IsRecording) await _dictation.CancelAsync();
        _listening = false;
        Report("Computer Use stopped.");
    }

    /// <summary>Runs a typed command (Settings page test box and automation).</summary>
    public Task RunCommandAsync(string command) => _running ? Task.CompletedTask : RunAsync(command, _settings.Load());

    private async Task StartListeningAsync()
    {
        Outcome = null;
        var settings = _settings.Load();
        if (!settings.ComputerUseEnabled) { Report("Turn on Computer Use in Settings first."); return; }
        if (!ComputerUseConfiguration.TryValidate(settings, ChatGpt.IsSignedIn, out var error)) { Report(error); return; }
        if (_running) return;
        if (_dictation.IsRecording || _dictation.IsBusy) { Report("Finish the active dictation first."); return; }
        try
        {
            await _dictation.StartAuxiliaryAsync(settings.MicrophoneName);
            _listening = _dictation.IsRecording;
            if (!_listening) throw new InvalidOperationException("The microphone did not start.");
            CaptureStarted?.Invoke();
            Report("Listening. Press Ctrl+Shift+F8 again when you are done.");
        }
        catch (Exception exception)
        {
            _listening = false;
            Report($"Could not start the microphone ({exception.GetType().Name}).");
        }
    }

    private async Task StopListeningAndRunAsync()
    {
        _listening = false;
        _running = true;
        Report("Transcribing your command…");
        string? command;
        try { command = await _dictation.StopWithoutPersistingAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            _running = false;
            Fail($"Could not transcribe the command ({exception.GetType().Name}).");
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            _running = false;
            Fail("No command was heard; nothing was done.");
            return;
        }
        await RunAsync(command.Trim(), _settings.Load());
    }

    private async Task RunAsync(string command, MuesliSettings settings)
    {
        Outcome = null;
        if (!ComputerUseConfiguration.TryValidate(settings, ChatGpt.IsSignedIn, out var error)) { Fail(error); return; }
        _running = true;
        _sessionCancellation?.Dispose();
        var cancellation = _sessionCancellation = new CancellationTokenSource();
        Report($"Working on: {command}");
        var model = string.IsNullOrWhiteSpace(settings.ComputerUsePlannerModel) ? null : settings.ComputerUsePlannerModel;
        ICuaPlanner planner = ComputerUseConfiguration.UsesApiKey(settings)
            ? ResponsesPlanner.WithApiKey(_http, Environment.GetEnvironmentVariable("OPENAI_API_KEY") is { Length: > 0 } key ? key : settings.ResolvedOpenAIApiKey, model)
            : ResponsesPlanner.WithChatGpt(_http, ChatGpt, model, appVersion: typeof(WinUiComputerUseContext).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        var options = new CuaRuntimeOptions
        {
            Confirm = ConfirmActionAsync,
            Settings = new CuaMuesliSettings(_settings.Load, saved => _dispatcher.EnqueueAsync(() => _settings.Save(saved), CancellationToken.None).GetAwaiter().GetResult()),
            OnStatus = status => Report(status),
        };
        try
        {
            // Desktop observation and input block, so the run stays off the UI thread.
            var result = await Task.Run(async () =>
            {
                using var desktop = new WindowsDesktop();
                return await new CuaRuntime(desktop, planner, options).RunAsync(command, cancellation.Token);
            });
            SaveTrace(result);
            var message = result.Status switch
            {
                CuaRunStatus.Done => result.Message,
                CuaRunStatus.Cancelled => result.Message,
                CuaRunStatus.TimedOut => "Computer Use ran out of time.",
                _ => $"Stopped: {result.Message}",
            };
            if (result.Status != CuaRunStatus.Cancelled) Outcome = new(message, result.Status == CuaRunStatus.Done);
            Report(message);
        }
        catch (Exception exception)
        {
            _log.Info($"Computer Use run failed: {exception.GetType().Name}.");
            Fail($"Computer Use failed: {exception.Message}");
        }
        finally
        {
            _running = false;
            Report(Status);
        }
    }

    /// <summary>Keeps the latest run's steps for the Settings diagnostics view (typed text included,
    /// screenshots never).</summary>
    private void SaveTrace(CuaRunResult result)
    {
        Diagnostics = $"{DateTime.Now:g}  {result.Status}\n" + string.Join("\n",
            result.Trace.Select(item => $"{(item.Step is { } step ? $"[{step}] " : "")}{item.Title}: {item.Body}"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TraceFilePath)!);
            File.WriteAllText(TraceFilePath, Diagnostics);
        }
        catch (IOException) { }
    }

    private void Fail(string message)
    {
        Outcome = new(message, false);
        Report(message);
    }

    /// <summary>Runs report status from a background thread; listeners update UI, so raise on the UI thread.</summary>
    private void Report(string status)
    {
        Status = status;
        _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _http.Dispose();
        _voiceHotkey.Dispose();
    }
}

public sealed record ComputerUseOutcome(string Message, bool Succeeded);
