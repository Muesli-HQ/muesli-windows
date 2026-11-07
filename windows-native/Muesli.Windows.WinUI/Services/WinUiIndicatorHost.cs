using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Indicator;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Drives the in-process floating indicator from the dictation, meeting and computer-use contexts.
/// It derives one idempotent <see cref="IndicatorSnapshot"/> per state change, applies it to the
/// <see cref="FloatingIndicatorWindow"/>, and routes the window's commands back into the owning
/// context. If the window cannot be created, Muesli carries on without a floating pill.
/// </summary>
public sealed class WinUiIndicatorHost : IDisposable
{
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiMeetingContext _meetings;
    private readonly WinUiComputerUseContext _computerUse;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log = new();

    private FloatingIndicatorWindow? _window;
    private CancellationTokenSource? _dwell;
    private FileSystemWatcher? _probeWatcher;
    private int _disposed;

    private FloatingIndicatorState _state = FloatingIndicatorState.Idle;
    private string _owner = IndicatorOwnerKind.None;
    private bool _meetingPaused;
    private long _sessionId;
    // Written by the audio pipeline, read by the window's 30 fps animation; never marshalled per buffer.
    private float _amplitude;
    private string? _dismissedOutcomeStatus;
    private string? _pendingOutcomeStatus;
    private FloatingIndicatorState? _probeState;
    private string _indicatorAnchor = "Middle Right";
    private double? _savedLeft;
    private double? _savedTop;

    public WinUiIndicatorHost(
        WinUiDictationContext dictation,
        WinUiMeetingContext meetings,
        WinUiComputerUseContext computerUse,
        IUiDispatcher dispatcher)
    {
        _dictation = dictation;
        _meetings = meetings;
        _computerUse = computerUse;
        _dispatcher = dispatcher;

        _dictation.Changed += OnStateSourceChanged;
        _dictation.RecordingLevelChanged += OnRecordingLevel;
        _meetings.Changed += OnStateSourceChanged;
        _computerUse.Changed += OnStateSourceChanged;
        App.Settings.Changed += OnSettingsChanged;
    }

    /// <summary>Creates the hidden indicator window. Must run on the UI thread.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            _window = new FloatingIndicatorWindow(OnCommand, () => Volatile.Read(ref _amplitude));
            _log.Info("Floating indicator ready (in-process).");
        }
        catch (Exception exception)
        {
            _log.Error("Floating indicator window could not be created; continuing without the floating pill.", exception);
            _window = null;
        }

        _probeWatcher = TryStartPresentationProbe();
        LoadPosition();
        UpdateState();
    }

    // ─── Snapshot engine ──────────────────────────────────────────────────────────────────────

    private void OnStateSourceChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(UpdateState);

    private void OnSettingsChanged(object? sender, MuesliSettings settings) => _dispatcher.TryEnqueue(UpdateState);

    private void OnRecordingLevel(object? sender, float peak) => Volatile.Write(ref _amplitude, Math.Clamp(peak, 0f, 1f));

    private void UpdateState()
    {
        if (Volatile.Read(ref _disposed) != 0 || _window is null) return;

        var status = _dictation.Status ?? "";
        var state = FloatingIndicatorStateClassifier.Classify(status, _dictation.IsRecording, _dictation.IsTranscribing);
        var owner = IndicatorOwnerKind.Dictation;
        var meetingPaused = false;

        if (_meetings.IsRecording || _meetings.IsPaused)
        {
            owner = IndicatorOwnerKind.Meeting;
            meetingPaused = _meetings.IsPaused;
            state = FloatingIndicatorState.Recording;
            // Levels arrive with live-transcript snapshots only; a flat pill means live transcription is off.
            Volatile.Write(ref _amplitude, meetingPaused
                ? 0f
                : (float)Math.Clamp(Math.Max(_meetings.MicrophoneLevel, _meetings.SystemLevel), 0, 1));
        }
        else if (_meetings.IsBusy)
        {
            owner = IndicatorOwnerKind.Meeting;
            state = FloatingIndicatorState.Transcribing;
        }
        else if (_computerUse.IsListening)
        {
            owner = IndicatorOwnerKind.ComputerUse;
            state = FloatingIndicatorState.Recording;
        }
        else if (_computerUse.IsRunning)
        {
            owner = IndicatorOwnerKind.ComputerUse;
            state = FloatingIndicatorState.Transcribing;
        }

        if (_probeState is { } probed)
        {
            state = probed;
            status = FloatingIndicatorProbe.StatusFor(probed);
        }

        if (FloatingIndicatorStateClassifier.IsAlreadyDismissed(state, status, _dismissedOutcomeStatus))
        {
            state = FloatingIndicatorState.Idle;
        }

        if (state != _state || owner != _owner || meetingPaused != _meetingPaused)
        {
            _sessionId++;
        }

        _state = state;
        _owner = owner;
        _meetingPaused = meetingPaused;

        UpdateReturnDwell(state, status);
        LoadPosition();
        try
        {
            _window.Apply(BuildSnapshot());
        }
        catch (Exception exception)
        {
            // The pill is optional: a rendering failure must never take dictation or meetings down.
            _log.Error("Floating indicator failed to render; continuing without the floating pill.", exception);
            try { _window.Close(); } catch (Exception) { }
            _window = null;
        }
    }

    private void UpdateReturnDwell(FloatingIndicatorState state, string status)
    {
        _dwell?.Cancel();
        if (FloatingIndicatorStateClassifier.IsTransientOutcome(state))
        {
            _pendingOutcomeStatus = status;
            _dwell = new CancellationTokenSource();
            var token = _dwell.Token;
            var duration = FloatingIndicatorLayout.ReturnMilliseconds(state);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(duration), token);
                    if (token.IsCancellationRequested) return;
                    _dismissedOutcomeStatus = _pendingOutcomeStatus;
                    _pendingOutcomeStatus = null;
                    _probeState = null;
                    _dispatcher.TryEnqueue(UpdateState);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
        else
        {
            if (FloatingIndicatorStateClassifier.IsActive(state)) _dismissedOutcomeStatus = null;
            _pendingOutcomeStatus = null;
        }
    }

    private IndicatorSnapshot BuildSnapshot()
    {
        var settings = App.Settings.Load();
        return new IndicatorSnapshot
        {
            SessionId = _sessionId,
            State = StateName(_state),
            Owner = _owner,
            Visible = _state != FloatingIndicatorState.Idle || settings.ShowFloatingIndicator,
            HotkeyLabel = string.IsNullOrWhiteSpace(settings.Hotkey) ? "shortcut" : settings.Hotkey,
            Message = ComposeMessage(_state, _dictation.Status ?? ""),
            RecordingColorHex = settings.RecordingColorHex,
            IndicatorStyle = settings.IndicatorStyle,
            MeetingPaused = _meetingPaused,
            HandsFree = settings.EnableDoubleTapDictation,
            IndicatorAnchor = _indicatorAnchor,
            SavedLeft = _savedLeft,
            SavedTop = _savedTop,
            Theme = settings.Theme,
            HighContrast = new AccessibilitySettings().HighContrast
        };
    }

    private static string ComposeMessage(FloatingIndicatorState state, string status) =>
        state switch
        {
            FloatingIndicatorState.Transcribing => "Transcribing",
            FloatingIndicatorState.Success or FloatingIndicatorState.Error => status,
            _ => ""
        };

    private static string StateName(FloatingIndicatorState state) =>
        state switch
        {
            FloatingIndicatorState.Preparing => "preparing",
            FloatingIndicatorState.Recording => "recording",
            FloatingIndicatorState.Transcribing => "transcribing",
            FloatingIndicatorState.Success => "success",
            FloatingIndicatorState.Error => "error",
            _ => "idle"
        };

    // ─── Command routing ──────────────────────────────────────────────────────────────────────

    // A meeting discard deletes audio and can delete written notes, so the pill asks first (macOS
    // discardMeetingWithConfirmation). The dashboard is raised so the dialog is actually visible.
    private async Task DiscardMeetingWithConfirmationAsync()
    {
        try
        {
            if (App.Window is MainWindow main) main.ShowDashboard("meetings");
            await _meetings.ConfirmAndDiscardAsync(App.Dialogs, !string.IsNullOrWhiteSpace(_meetings.ManualNotes));
        }
        catch (Exception exception)
        {
            _log.Error("Meeting discard from the floating indicator failed.", exception);
        }
    }

    private void OnCommand(IndicatorCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0 || IndicatorProtocol.IsStaleSession(command.SessionId, _sessionId)) return;

        switch (command.Type)
        {
            case IndicatorCommandType.Stop:
                if (_owner == IndicatorOwnerKind.Meeting) _ = _meetings.StopAndSaveAsync();
                else if (_owner == IndicatorOwnerKind.ComputerUse) _ = _computerUse.ToggleVoiceAsync();
                else _ = _dictation.StopRecordingAsync();
                break;

            case IndicatorCommandType.Cancel:
                if (_owner == IndicatorOwnerKind.Meeting) _ = DiscardMeetingWithConfirmationAsync();
                else if (_owner == IndicatorOwnerKind.ComputerUse) _ = _computerUse.CancelAsync();
                else _ = _dictation.CancelAsync();
                break;

            case IndicatorCommandType.Pause:
                _ = _meetings.PauseAsync();
                break;

            case IndicatorCommandType.Resume:
                _ = _meetings.ResumeAsync();
                break;

            case IndicatorCommandType.Drag:
                ApplyCustomPosition(command.DragLeft, command.DragTop);
                break;

            case IndicatorCommandType.HoverEnter:
                if (_owner == IndicatorOwnerKind.Meeting && App.Settings.Load().ShowLiveWaveformOnHover &&
                    command.IndicatorX is { } x && command.IndicatorY is { } y &&
                    command.IndicatorWidth is > 0 and < 2000 && command.IndicatorHeight is > 0 and < 2000)
                {
                    App.LiveTranscript.ShowOnIndicatorHover(new global::Windows.Graphics.RectInt32(x, y, command.IndicatorWidth.Value, command.IndicatorHeight.Value));
                }
                break;

            case IndicatorCommandType.HoverExit:
                App.LiveTranscript.ScheduleHoverExit();
                break;
        }
    }

    private void ApplyCustomPosition(double? left, double? top)
    {
        if (left is not { } x || !double.IsFinite(x) || top is not { } y || !double.IsFinite(y)) return;

        _savedLeft = x;
        _savedTop = y;
        _indicatorAnchor = "Custom";
        try
        {
            App.Settings.Save(App.Settings.Load() with
            {
                IndicatorAnchor = _indicatorAnchor,
                IndicatorLeft = _savedLeft,
                IndicatorTop = _savedTop
            });
        }
        catch (Exception exception)
        {
            _log.Error("Could not save the floating indicator position.", exception);
        }
    }

    private void LoadPosition()
    {
        var settings = App.Settings.Load();
        _indicatorAnchor = string.IsNullOrWhiteSpace(settings.IndicatorAnchor) ? "Middle Right" : settings.IndicatorAnchor;
        _savedLeft = settings.IndicatorLeft;
        _savedTop = settings.IndicatorTop;
    }

    // ─── Presentation probe (UI automation tests) ─────────────────────────────────────────────

    private FileSystemWatcher? TryStartPresentationProbe()
    {
        if (!FloatingIndicatorProbe.IsEnabledForCurrentProcess()) return null;
        try
        {
            var root = MuesliProfilePaths.Current().RootDirectory;
            Directory.CreateDirectory(root);
            var statePath = Path.Combine(root, FloatingIndicatorProbe.StateFileName);
            ApplyProbeFile(statePath);
            var watcher = new FileSystemWatcher(root, FloatingIndicatorProbe.StateFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName
            };
            watcher.Changed += (_, _) => _dispatcher.TryEnqueue(() => ApplyProbeFile(statePath));
            watcher.Created += (_, _) => _dispatcher.TryEnqueue(() => ApplyProbeFile(statePath));
            watcher.Deleted += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                _probeState = null;
                UpdateState();
            });
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ApplyProbeFile(string statePath)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (!File.Exists(statePath)) return;
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var parsed = FloatingIndicatorProbe.ParseState(reader.ReadToEnd());
            if (parsed is null) return;
            _probeState = parsed;
            UpdateState();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _dictation.Changed -= OnStateSourceChanged;
        _dictation.RecordingLevelChanged -= OnRecordingLevel;
        _meetings.Changed -= OnStateSourceChanged;
        _computerUse.Changed -= OnStateSourceChanged;
        App.Settings.Changed -= OnSettingsChanged;

        _probeWatcher?.Dispose();
        _dwell?.Cancel();
        try { _window?.Close(); } catch (Exception) { }
        _window = null;
    }
}
