using System.Diagnostics;
using Muesli.Windows.ComputerUse;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// WinUI Computer Use host: the Computer Use shortcut (Ctrl+Shift+F8 by default) starts voice capture, a second press transcribes the
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
    private Stopwatch? _listenClock;
    private int _disposed;

    public WinUiComputerUseContext(WinUiLibraryContext library, WinUiSettingsContext settings, WinUiDictationContext dictation, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _library = library;
        _settings = settings;
        _dictation = dictation;
        ChatGpt = new ChatGptAuth(settings.Secrets, _http);
        _dictation.Changed += OnDictationChanged;
        Status = $"Press {Shortcut}, say what to do, then press it again.";
    }

    public ChatGptAuth ChatGpt { get; }
    public Action? CaptureStarted { get; set; }
    /// <summary>Asks the user to approve a risky step; returns false when declined.</summary>
    public Func<string, CancellationToken, Task<bool>>? ConfirmActionAsync { get; set; }
    /// <summary>Asks the user a question the run needs answered; returns null when they stop the run.</summary>
    public Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? AskAsync { get; set; }
    public event EventHandler? Changed;
    public string Status { get; private set; }
    /// <summary>The pill line: macOS shows the spoken command while the planner thinks.</summary>
    public string PillText => Status == "Thinking…" && _command is { Length: > 0 } command ? $"“{command}”" : Status;
    private string? _command;
    public bool IsListening => _listening;
    public bool IsRunning => _running;
    public bool StopEnabled => _listening || _running;
    /// <summary>How the last run (or attempt) ended, for the floating pill; null once a new one starts.</summary>
    public ComputerUseOutcome? Outcome { get; private set; }
    private string Shortcut => _settings.Load().ComputerUseHotkey;

    /// <summary>Installs the saved Computer Use shortcut; returns why it failed, or null.</summary>
    public string? RegisterVoiceShortcut()
    {
        try
        {
            _voiceHotkey.Register(Shortcut, () => _dispatcher.TryEnqueue(() => _ = ToggleVoiceAsync()),
                () => { }, () => StopEnabled, () => _dispatcher.TryEnqueue(() => _ = CancelAsync()));
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return exception.Message;
        }
    }

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

    /// <summary>Escape reaches the dictation hook first and cancels the shared capture; without this
    /// the run would keep "listening" (and the pill recording) with no microphone.</summary>
    private void OnDictationChanged(object? sender, EventArgs e)
    {
        if (!_listening || _dictation.IsRecording) return;
        _listening = false;
        Report("Computer Use stopped.");
    }

    public async Task CancelAsync()
    {
        _sessionCancellation?.Cancel();
        if (_listening && _dictation.IsRecording) await _dictation.CancelAsync();
        _listening = false;
        Report("Computer Use stopped.");
    }

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
            _listenClock = Stopwatch.StartNew();
            CaptureStarted?.Invoke();
            Report($"Listening. Press {settings.ComputerUseHotkey} again when you are done.");
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
        await RunAsync(command.Trim(), (int)(_listenClock?.ElapsedMilliseconds ?? 0), _settings.Load());
    }

    private async Task RunAsync(string command, int spokenMs, MuesliSettings settings)
    {
        Outcome = null;
        if (!ComputerUseConfiguration.TryValidate(settings, ChatGpt.IsSignedIn, out var error)) { Fail(error); return; }
        _running = true;
        _sessionCancellation?.Dispose();
        var cancellation = _sessionCancellation = new CancellationTokenSource();
        _command = command;
        Report($"Working on: {command}");
        var model = ComputerUseConfiguration.Model(settings.ComputerUsePlannerModel);
        var planner = ResponsesPlanner.WithChatGpt(_http, ChatGpt, model.Id, ComputerUseConfiguration.Effort(model, settings.ComputerUseReasoningEffort),
            typeof(WinUiComputerUseContext).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        var options = new CuaRuntimeOptions
        {
            Timeout = TimeSpan.FromSeconds(settings.ComputerUseTimeoutSeconds),
            Confirm = ConfirmActionAsync,
            Ask = AskAsync,
            Settings = new CuaMuesliSettings(_settings.Load, saved => _dispatcher.EnqueueAsync(() => _settings.Save(saved), CancellationToken.None).GetAwaiter().GetResult(),
                HotkeyConflictProbe.IsAdvisoryRegistrationAvailable, ReapplyDictationShortcut),
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
            SaveRun(command, spokenMs, result);
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

    /// <summary>Re-installs the push-to-talk hook after a voice change, like the Shortcuts page.</summary>
    private string? ReapplyDictationShortcut()
    {
        string? failure = null;
        _dispatcher.EnqueueAsync(() =>
        {
            _dictation.RegisterHotkey();
            failure = _dictation.IsHotkeyRegistered ? null : _dictation.Status;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return failure;
    }

    /// <summary>Like macOS, a run is a dictation-history record (the spoken command) whose steps
    /// (typed text included, screenshots never) sit in a file beside it for the Dictations page.</summary>
    private void SaveRun(string command, int spokenMs, CuaRunResult result)
    {
        var id = $"dict_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")[..6]}";
        var steps = $"{result.Status}\n" + string.Join("\n",
            result.Trace.Select(item => $"{(item.Step is { } step ? $"[{step}] " : "")}{item.Title}: {item.Body}"));
        try
        {
            var path = ComputerUseConfiguration.RunStepsPath(_library.Profile.RootDirectory, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, steps);
            _library.History.AppendDictation(new PersistedDictation(id, DateTime.Now, command, spokenMs, ComputerUseConfiguration.HistoryLabel));
        }
        catch (Exception exception)
        {
            _log.Info($"Computer Use run was not saved to history: {exception.GetType().Name}.");
        }
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
        _dictation.Changed -= OnDictationChanged;
        _sessionCancellation?.Cancel();
        _sessionCancellation?.Dispose();
        _http.Dispose();
        _voiceHotkey.Dispose();
    }
}

public sealed record ComputerUseOutcome(string Message, bool Succeeded);
