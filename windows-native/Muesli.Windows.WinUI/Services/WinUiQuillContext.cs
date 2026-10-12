using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS Quill: highlight text, hold the Quill shortcut and say how to change it, release, and the
/// selection is replaced with the rewrite. With nothing highlighted, the spoken request is written
/// at the cursor. The instruction is transcribed locally; the rewrite goes to the signed-in
/// ChatGPT account, the same account Computer Use and ChatGPT summaries use.
/// </summary>
public sealed class WinUiQuillContext : IDisposable
{
    private readonly WinUiSettingsContext _settings;
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiComputerUseContext _computerUse;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log = new();
    private readonly GlobalHotkeyService _hotkey = new();
    private readonly WinRtClipboardAdapter _clipboard = new();
    private readonly ActiveAppPasteService _paste;
    private CancellationTokenSource? _run;
    private IntPtr _target;
    private bool _listening;
    private bool _starting;
    private bool _releasedWhileStarting;
    private bool _running;
    private int _disposed;

    public WinUiQuillContext(WinUiSettingsContext settings, WinUiDictationContext dictation, WinUiComputerUseContext computerUse, IUiDispatcher dispatcher)
    {
        _settings = settings;
        _dictation = dictation;
        _computerUse = computerUse;
        _dispatcher = dispatcher;
        _paste = new ActiveAppPasteService(_clipboard, new NativeWindowActivationAdapter(),
            new NativeKeyboardInputAdapter(() => _settings.Load().PasteShortcut));
        _dictation.Changed += OnDictationChanged;
    }

    public event EventHandler? Changed;
    public string Status { get; private set; } = "";
    public bool IsListening => _listening;
    public bool IsRunning => _running;
    /// <summary>How the last rewrite ended, for the floating pill; null once a new one starts.</summary>
    public ComputerUseOutcome? Outcome { get; private set; }

    /// <summary>Installs or removes the Quill shortcut from settings; returns why it failed, or null.</summary>
    public string? RegisterShortcut()
    {
        var settings = _settings.Load();
        if (!settings.QuillEnabled)
        {
            _hotkey.Dispose();
            return null;
        }
        try
        {
            _hotkey.Register(settings.QuillHotkey,
                () => _dispatcher.TryEnqueue(() => _ = StartAsync()),
                () => _dispatcher.TryEnqueue(() => _ = FinishAsync()),
                () => _listening || _running,
                () => _dispatcher.TryEnqueue(() => _ = CancelAsync()));
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return exception.Message;
        }
    }

    private async Task StartAsync()
    {
        if (_listening || _running || _starting) return;
        Outcome = null;
        var settings = _settings.Load();
        if (!_computerUse.ChatGpt.IsSignedIn) { Fail("Sign in to ChatGPT in Settings to use Quill."); return; }
        if (_dictation.IsRecording || _dictation.IsBusy) { Fail("Finish the active dictation first."); return; }
        // The app the user is editing; Muesli itself never becomes the target.
        _target = _paste.CaptureForegroundWindow();
        _starting = true;
        _releasedWhileStarting = false;
        try
        {
            await _dictation.StartAuxiliaryAsync(settings.MicrophoneName);
            _listening = _dictation.IsRecording;
            if (!_listening) throw new InvalidOperationException("The microphone did not start.");
            Report("Quill is listening…");
        }
        catch (Exception exception)
        {
            _listening = false;
            Fail($"Could not start the microphone ({exception.GetType().Name}).");
        }
        finally
        {
            _starting = false;
        }
        // A quick tap can release the key before the microphone finished opening.
        if (_releasedWhileStarting) await FinishAsync();
    }

    private async Task FinishAsync()
    {
        if (_starting) { _releasedWhileStarting = true; return; }
        if (!_listening) return;
        _listening = false;
        _running = true;
        _run?.Dispose();
        var run = _run = new CancellationTokenSource();
        try
        {
            Report("Transcribing your instruction…");
            var instruction = await _dictation.StopWithoutPersistingAsync(run.Token, reportResult: false);
            if (string.IsNullOrWhiteSpace(instruction)) throw new QuillException("Quill did not hear a spoken instruction.");
            await FocusedTextSelection.WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1), run.Token);
            var selected = await ReadSelectionAsync(run.Token);
            Report(selected.Length > 0 ? "Rewriting…" : "Writing…");
            var replacement = await QuillTransformation.TransformAsync(selected, instruction,
                (system, user, token) => MeetingSummaryService.CompleteWithChatGptAsync(system, user, null, token), run.Token);
            run.Token.ThrowIfCancellationRequested();
            await _paste.PasteTextAsync(replacement, _target, run.Token);
            Outcome = new(selected.Length > 0 ? "Rewrote the selection" : "Inserted text", true);
            Report(Outcome.Message);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            Report("Quill cancelled.");
        }
        catch (QuillException exception) { Fail(exception.Message); }
        catch (SummaryProviderException exception)
        {
            Fail(exception.SafeReason == "not-signed-in"
                ? "Sign in to ChatGPT in Settings to use Quill."
                : $"ChatGPT could not rewrite the text ({exception.SafeReason}). Nothing was pasted.");
        }
        catch (Exception exception)
        {
            _log.Error("Quill failed.", exception);
            Fail(exception is InvalidOperationException ? exception.Message : $"Quill failed ({exception.GetType().Name}). Nothing was pasted.");
        }
        finally
        {
            _running = false;
            Report(Status);
        }
    }

    /// <summary>
    /// UI Automation reads the selection without touching the clipboard. Apps that do not expose it
    /// get a copy whose clipboard change is detected by sequence number and then undone; terminals
    /// never get one, because Ctrl+C there interrupts the running program.
    /// </summary>
    private async Task<string> ReadSelectionAsync(CancellationToken cancellationToken)
    {
        var viaAutomation = await Task.Run(FocusedTextSelection.ReadSelection, cancellationToken);
        if (viaAutomation is not null) return viaAutomation;
        if (FocusedTextSelection.IsTerminal(_target)) return "";

        var saved = await _clipboard.CaptureAsync(cancellationToken);
        var before = FocusedTextSelection.ClipboardSequence;
        try
        {
            if (!FocusedTextSelection.SendCopy()) return "";
            for (var waited = 0; waited < 500 && FocusedTextSelection.ClipboardSequence == before; waited += 25)
                await Task.Delay(25, cancellationToken);
            return FocusedTextSelection.ClipboardSequence == before ? "" : await _clipboard.ReadTextAsync(cancellationToken) ?? "";
        }
        finally
        {
            if (FocusedTextSelection.ClipboardSequence != before) await _clipboard.RestoreAsync(saved, CancellationToken.None);
        }
    }

    public async Task CancelAsync()
    {
        _run?.Cancel();
        if (_listening && _dictation.IsRecording) await _dictation.CancelAsync();
        _listening = false;
        Report("Quill cancelled.");
    }

    /// <summary>Escape reaches the dictation hook first and cancels the shared capture.</summary>
    private void OnDictationChanged(object? sender, EventArgs e)
    {
        if (!_listening || _dictation.IsRecording) return;
        _listening = false;
        Report("Quill cancelled.");
    }

    private void Fail(string message)
    {
        Outcome = new(message, false);
        Report(message);
    }

    private void Report(string status)
    {
        Status = status;
        _dispatcher.TryEnqueue(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _dictation.Changed -= OnDictationChanged;
        _run?.Cancel();
        _run?.Dispose();
        _hotkey.Dispose();
    }
}
