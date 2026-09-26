using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Owns the single meeting-notification window, its dedup/suppression state, and the sanitized
/// logging for every outcome. It renders presentation state and forwards explicit user actions; it
/// never owns capture, persistence, or transcription.
/// </summary>
public sealed class WinUiMeetingNotificationService : IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly AppLogService _log;
    private readonly Func<bool> _detectionEnabled;
    private readonly Func<bool> _isRecording;
    private readonly Func<bool> _isBusy;
    private readonly MeetingNotificationSuppressionState _state = new();
    private MeetingNotificationWindow? _window;
    private int _disposed;

    public WinUiMeetingNotificationService(
        IUiDispatcher dispatcher,
        AppLogService log,
        Func<bool> detectionEnabled,
        Func<bool> isRecording,
        Func<bool> isBusy)
    {
        _dispatcher = dispatcher;
        _log = log;
        _detectionEnabled = detectionEnabled;
        _isRecording = isRecording;
        _isBusy = isBusy;
    }

    public bool IsVisible => _window is { IsPresenting: true };

    public MeetingNotificationOutcome? LastOutcome { get; private set; }

    /// <summary>
    /// Presents a prompt unless suppression rejects it, and returns the recorded outcome. The
    /// development-only preview passes <paramref name="bypassSuppression"/> so it renders even when
    /// live detection is disabled.
    /// </summary>
    public MeetingNotificationOutcome Present(
        MeetingNotificationRequest request,
        MeetingNotificationCallbacks callbacks,
        bool bypassSuppression = false)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return MeetingNotificationOutcome.SuppressedDisabled;
        }

        var decision = bypassSuppression
            ? new MeetingNotificationDecision(
                true,
                request.IsScheduled
                    ? MeetingNotificationOutcome.UpcomingScheduledMeeting
                    : MeetingNotificationOutcome.ActiveMeetingDetected,
                "preview",
                request.PromptId)
            : _state.Evaluate(request, _detectionEnabled(), _isRecording(), _isBusy());
        LastOutcome = decision.Outcome;
        Log(request, decision);
        if (!decision.Show)
        {
            return decision.Outcome;
        }

        // Replacing a prompt must suppress the old window's callbacks so it cannot act on the wrong
        // meeting.
        var previous = _window;
        if (previous is not null)
        {
            previous.SuppressCallbacks();
            _window = null;
        }

        MeetingNotificationWindow? window = null;
        window = new MeetingNotificationWindow(
            request,
            action =>
            {
                _state.MarkHidden();
                _state.MarkRecordingStarted(request.PromptId);
                ClearWindow(window!);
                callbacks.OnAction(action);
            },
            () =>
            {
                _state.MarkHidden();
                _state.SuppressForUser(request.PromptId);
                ClearWindow(window!);
                callbacks.OnDismiss();
            },
            () =>
            {
                _state.MarkHidden();
                _state.SuppressForAuto(request.PromptId);
                ClearWindow(window!);
                callbacks.OnAutoDismiss();
            },
            () =>
            {
                _state.MarkHidden();
                ClearWindow(window!);
            });

        if (Volatile.Read(ref _disposed) != 0)
        {
            window.SuppressCallbacks();
            return MeetingNotificationOutcome.SuppressedDisabled;
        }

        _window = window;
        _state.MarkShown(request.PromptId);
        window.ShowNotification();
        return decision.Outcome;
    }

    /// <summary>Clears suppression once a detected meeting genuinely ended.</summary>
    public void Forget(string promptId)
    {
        if (_state.Forget(promptId))
        {
            _log.Info($"Meeting notification suppression cleared (meeting ended). promptHash={AppLogService.SensitiveTextFingerprint(promptId)}");
        }
    }

    /// <summary>Closes any visible prompt without invoking its callbacks.</summary>
    public void Close()
    {
        var window = _window;
        _window = null;
        _state.MarkHidden();
        window?.SuppressCallbacks();
    }

    private void ClearWindow(MeetingNotificationWindow window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
        }
    }

    private void Log(MeetingNotificationRequest request, MeetingNotificationDecision decision)
    {
        var promptHash = AppLogService.SensitiveTextFingerprint(request.PromptId);
        var kind = request.IsScheduled ? "ScheduledUpcoming" : "ActiveDetected";
        if (decision.Show)
        {
            _log.Info(
                $"Meeting notification shown. platform={request.Platform}; kind={kind}; promptHash={promptHash}");
        }
        else
        {
            _log.Info(
                $"Meeting notification suppressed. outcome={decision.Outcome}; reason={decision.Reason}; platform={request.Platform}; promptHash={promptHash}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close();
    }
}
