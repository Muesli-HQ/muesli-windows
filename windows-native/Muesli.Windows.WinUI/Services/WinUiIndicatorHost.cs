using Microsoft.UI.Dispatching;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;
using System.Diagnostics;
using Windows.UI.ViewManagement;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Owns the WPF indicator companion on behalf of the WinUI shell. It keeps the product state in
/// the WinUI process (dictation, meeting and computer-use contexts), renders only idempotent
/// <see cref="IndicatorSnapshot"/> frames down a current-user-only named pipe, and marshals every
/// product command the companion sends back into the owning context. If the companion cannot start
/// or restarts without success, it falls back to the in-process <see cref="DictationIndicatorWindow"/>.
/// </summary>
public sealed class WinUiIndicatorHost : IDisposable
{
    private readonly WinUiDictationContext _dictation;
    private readonly WinUiMeetingContext _meetings;
    private readonly WinUiComputerUseContext _computerUse;
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log = new();

    private IndicatorPipeServer? _server;
    private Process? _companion;
    private DictationIndicatorWindow? _fallback;
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _dwell;
    private FileSystemWatcher? _probeWatcher;
    private string _instanceId = "";
    private int _restarts;
    private int _disposed;

    private FloatingIndicatorState _state = FloatingIndicatorState.Idle;
    private string _owner = IndicatorOwnerKind.None;
    private bool _meetingPaused;
    private long _sessionId;
    private float _amplitude;
    private IndicatorSnapshot? _latest;
    private IndicatorMeetingNotification? _meetingNotification;
    private MeetingNotificationCallbacks? _meetingNotificationCallbacks;
    private string? _dismissedOutcomeStatus;
    private string? _pendingOutcomeStatus;
    private FloatingIndicatorState? _probeState;
    private string _indicatorAnchor = "Middle Right";
    private double? _savedLeft;
    private double? _savedTop;
    private long _lastAmplitudePublishTicks;

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

    public bool IsFallbackActive => _fallback is not null;
    public bool CanPresentMeetingNotification => _fallback is null && _server is not null && Volatile.Read(ref _disposed) == 0;
    public bool IsMeetingNotificationVisible => _meetingNotification is not null;

    public void PresentMeetingNotification(MeetingNotificationRequest request, MeetingNotificationCallbacks callbacks)
    {
        _meetingNotification = new IndicatorMeetingNotification(
            request.PromptId, request.Title, request.Subtitle, request.Platform, request.Glyph,
            request.AccentHex, request.ShortLabel, request.ActionLabel, request.HasSplitAction,
            request.DefaultAction, request.DismissAfterSeconds);
        _meetingNotificationCallbacks = callbacks;
        Publish();
    }

    public void CloseMeetingNotification()
    {
        _meetingNotification = null;
        _meetingNotificationCallbacks = null;
        Publish();
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        _lifetime = new CancellationTokenSource();
        _instanceId = Guid.NewGuid().ToString("N");
        _server = new IndicatorPipeServer(_instanceId, OnCommand);
        _server.Connected += (_, _) => Publish();
        _server.Disconnected += (_, _) => HandleCompanionGone();
        _server.Start();

        _probeWatcher = TryStartPresentationProbe();
        LoadPosition();
        LaunchCompanion();
        UpdateState();
    }

    // ─── Snapshot engine ──────────────────────────────────────────────────────────────────────

    private void OnStateSourceChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(UpdateState);

    private void OnSettingsChanged(object? sender, MuesliSettings settings) => _dispatcher.TryEnqueue(UpdateState);

    private void OnRecordingLevel(object? sender, float peak)
    {
        _amplitude = Math.Clamp(peak, 0f, 1f);
        _dispatcher.TryEnqueue(PublishAmplitude);
    }

    private void UpdateState()
    {
        if (Volatile.Read(ref _disposed) != 0 || _fallback is not null) return;

        var settings = App.Settings.Load();
        var status = _dictation.Status ?? "";
        var state = FloatingIndicatorStateClassifier.Classify(status, _dictation.IsRecording, _dictation.IsTranscribing);
        var owner = IndicatorOwnerKind.Dictation;
        var meetingPaused = false;

        if (_meetings.IsRecording || _meetings.IsPaused)
        {
            owner = IndicatorOwnerKind.Meeting;
            meetingPaused = _meetings.IsPaused;
            state = FloatingIndicatorState.Recording;
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
        Publish();
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
        var visible = _state != FloatingIndicatorState.Idle || settings.ShowFloatingIndicator;
        return new IndicatorSnapshot
        {
            SessionId = _sessionId,
            State = StateName(_state),
            Owner = _owner,
            Visible = visible,
            HotkeyLabel = string.IsNullOrWhiteSpace(settings.Hotkey) ? "shortcut" : settings.Hotkey,
            Message = ComposeMessage(_state, _dictation.Status ?? ""),
            RecordingColorHex = settings.RecordingColorHex,
            MeetingPaused = _meetingPaused,
            HandsFree = settings.EnableDoubleTapDictation,
            IndicatorAnchor = _indicatorAnchor,
            SavedLeft = _savedLeft,
            SavedTop = _savedTop,
            Theme = settings.Theme,
            HighContrast = new AccessibilitySettings().HighContrast,
            Amplitude = _state == FloatingIndicatorState.Recording ? _amplitude : 0f,
            MeetingNotification = _meetingNotification
        };
    }

    private void Publish()
    {
        if (Volatile.Read(ref _disposed) != 0 || _fallback is not null || _server is null) return;
        var snapshot = BuildSnapshot();
        _latest = snapshot;
        _server.Publish(snapshot);
    }

    private void PublishAmplitude()
    {
        if (_state != FloatingIndicatorState.Recording || _fallback is not null || _latest is null) return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastAmplitudePublishTicks, now).TotalMilliseconds < 1000.0 / FloatingIndicatorLayout.WaveformUpdateFramesPerSecond)
        {
            return;
        }
        _lastAmplitudePublishTicks = now;
        _latest = _latest with { Amplitude = _amplitude };
        _server?.Publish(_latest);
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
            FloatingIndicatorState.Idle => "idle",
            FloatingIndicatorState.Preparing => "preparing",
            FloatingIndicatorState.Recording => "recording",
            FloatingIndicatorState.Transcribing => "transcribing",
            FloatingIndicatorState.Success => "success",
            FloatingIndicatorState.Error => "error",
            _ => "idle"
        };

    // ─── Command routing ──────────────────────────────────────────────────────────────────────

    private void OnCommand(IndicatorCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0 || _fallback is not null) return;
        if (command.Type is IndicatorCommandType.MeetingNotificationAction or
            IndicatorCommandType.MeetingNotificationDismiss or
            IndicatorCommandType.MeetingNotificationAutoDismiss)
        {
            _dispatcher.TryEnqueue(() => HandleMeetingNotificationCommand(command));
            return;
        }
        if (IndicatorProtocol.IsStaleSession(command.SessionId, _sessionId)) return;

        switch (command.Type)
        {
            case IndicatorCommandType.Ready:
                Publish();
                break;

            case IndicatorCommandType.Stop:
                _dispatcher.TryEnqueue(() =>
                {
                    if (_owner == IndicatorOwnerKind.Meeting) _ = _meetings.StopAndSaveAsync();
                    else _ = _dictation.StopRecordingAsync();
                });
                break;

            case IndicatorCommandType.Cancel:
                _dispatcher.TryEnqueue(() =>
                {
                    if (_owner == IndicatorOwnerKind.Meeting) _ = _meetings.CancelAsync();
                    else _ = _dictation.CancelAsync();
                });
                break;

            case IndicatorCommandType.Pause:
                _dispatcher.TryEnqueue(() => _ = _meetings.PauseAsync());
                break;

            case IndicatorCommandType.Resume:
                _dispatcher.TryEnqueue(() => _ = _meetings.ResumeAsync());
                break;

            case IndicatorCommandType.Drag:
                _dispatcher.TryEnqueue(() => ApplyCustomPosition(command.DragLeft, command.DragTop));
                break;

            case IndicatorCommandType.Heartbeat:
            case IndicatorCommandType.HoverEnter:
            case IndicatorCommandType.HoverExit:
            case IndicatorCommandType.Exit:
            default:
                // Hover is rendered locally by the companion; heartbeat/exit only inform lifecycle.
                break;
        }
    }

    private void HandleMeetingNotificationCommand(IndicatorCommand command)
    {
        if (_meetingNotification is null ||
            !string.Equals(_meetingNotification.PromptId, command.NotificationPromptId, StringComparison.Ordinal)) return;
        MeetingNotificationAction? action = null;
        if (command.Type == IndicatorCommandType.MeetingNotificationAction)
        {
            if (!Enum.TryParse<MeetingNotificationAction>(command.NotificationAction, out var parsed) ||
                (_meetingNotification.HasSplitAction
                    ? parsed is not (MeetingNotificationAction.JoinAndRecord or MeetingNotificationAction.JoinOnly or MeetingNotificationAction.TranscribeOnly)
                    : parsed != MeetingNotificationAction.StartTranscribing)) return;
            action = parsed;
        }
        var callbacks = _meetingNotificationCallbacks;
        CloseMeetingNotification();
        if (callbacks is null) return;
        if (command.Type == IndicatorCommandType.MeetingNotificationDismiss) callbacks.OnDismiss();
        else if (command.Type == IndicatorCommandType.MeetingNotificationAutoDismiss) callbacks.OnAutoDismiss();
        else if (action is { } validAction) callbacks.OnAction(validAction);
    }

    private void ApplyCustomPosition(double? left, double? top)
    {
        if (left is not null && double.IsFinite(left.Value) &&
            top is not null && double.IsFinite(top.Value))
        {
            _savedLeft = left;
            _savedTop = top;
            _indicatorAnchor = "Custom";
            try
            {
                var settings = App.Settings.Load();
                App.Settings.Save(settings with
                {
                    IndicatorAnchor = _indicatorAnchor,
                    IndicatorLeft = _savedLeft,
                    IndicatorTop = _savedTop
                });
            }
            catch (Exception)
            {
            }
        }
    }

    private void LoadPosition()
    {
        var settings = App.Settings.Load();
        _indicatorAnchor = string.IsNullOrWhiteSpace(settings.IndicatorAnchor) ? "Middle Right" : settings.IndicatorAnchor;
        _savedLeft = settings.IndicatorLeft;
        _savedTop = settings.IndicatorTop;
    }

    // ─── Companion lifecycle ──────────────────────────────────────────────────────────────────

    private void LaunchCompanion()
    {
        // Never allow a second companion, even across a crash/restart cycle.
        try { _companion?.Kill(); } catch (InvalidOperationException) { } catch (NotSupportedException) { }
        _companion?.Dispose();
        _companion = null;

        try
        {
            var exe = CompanionExecutablePath();
            if (exe is null || !File.Exists(exe))
            {
                _log.Error("WPF indicator companion is missing from the layout; using the WinUI indicator.", null);
                ActivateFallback();
                return;
            }

            var startInfo = new ProcessStartInfo(exe, $"--pipe={_instanceId} --parent-pid={Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            _companion = Process.Start(startInfo);
            _log.Info($"Started WPF indicator companion pid={_companion?.Id} instance={_instanceId}");
        }
        catch (Exception exception)
        {
            _log.Error("WPF indicator companion failed to start; using the WinUI indicator.", exception);
            ActivateFallback();
        }
    }

    private void HandleCompanionGone()
    {
        if (Volatile.Read(ref _disposed) != 0 || _fallback is not null) return;
        if (_restarts < 1)
        {
            _restarts++;
            _log.Info("WPF indicator companion disconnected; restarting once.");
            _dispatcher.TryEnqueue(() =>
            {
                RecreateServer();
                LaunchCompanion();
            });
        }
        else
        {
            _log.Info("WPF indicator companion disconnected again; falling back to the WinUI indicator.");
            _dispatcher.TryEnqueue(ActivateFallback);
        }
    }

    private void RecreateServer()
    {
        _server?.Dispose();
        _server = new IndicatorPipeServer(_instanceId, OnCommand);
        _server.Connected += (_, _) => Publish();
        _server.Disconnected += (_, _) => HandleCompanionGone();
        _server.Start();
    }

    private void ActivateFallback()
    {
        if (_fallback is not null || Volatile.Read(ref _disposed) != 0) return;
        _fallback = new DictationIndicatorWindow(_dictation, _dispatcher);
        App.TrackTheme(_fallback);
    }

    private static string? CompanionExecutablePath()
    {
        var root = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(root, "Indicator", "Muesli.Windows.Indicator.Wpf.exe"),
            Path.Combine(root, "Muesli.Windows.Indicator.Wpf.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ─── Presentation probe ───────────────────────────────────────────────────────────────────

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
        _lifetime?.Cancel();

        try { _companion?.Kill(); } catch (InvalidOperationException) { } catch (NotSupportedException) { }
        _companion?.Dispose();

        _server?.Dispose();
        _fallback?.Dispose();
    }
}

/// <summary>Internal renderer selection: WPF companion by default, WinUI fallback as a diagnostic override.</summary>
public static class IndicatorRendererKind
{
    public const string Wpf = "wpf";
    public const string WinUi = "winui";
    private const string Flag = "--indicator-renderer";

    public static string For(IReadOnlyList<string>? args)
    {
        if (args is null) return Wpf;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index]?.Trim();
            if (arg == null) continue;
            if (arg.Equals(Flag, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                var value = args[index + 1].Trim().ToLowerInvariant();
                return value == WinUi ? WinUi : Wpf;
            }
            if (arg.StartsWith(Flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[(Flag.Length + 1)..].Trim().ToLowerInvariant();
                return value == WinUi ? WinUi : Wpf;
            }
        }
        return Wpf;
    }
}
