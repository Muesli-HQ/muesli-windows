namespace Muesli.Windows.Services;

/// <summary>
/// Holds meeting detection back while Muesli itself is using the microphone, as macOS does
/// (<c>MeetingMonitor.suppressWhileActive</c> / <c>resumeAfterCooldown</c>): dictation or a voice
/// command must never look like the start of a call, and detection stays quiet for a short
/// cooldown afterwards so the tail of that activity cannot confirm a candidate either.
/// </summary>
public sealed class MeetingDetectionSuppression
{
    /// <summary>macOS resumeAfterCooldown: detection resumes 15 seconds after the activity ends.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(15);

    /// <summary>macOS re-offers a browser meeting 120 seconds after its prompt timed out unanswered.</summary>
    public static readonly TimeSpan BrowserRepromptDelay = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private bool _active;
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    /// <summary>Suppresses with no end, until <see cref="ResumeAfterCooldown"/>.</summary>
    public void SuppressWhileActive()
    {
        lock (_gate) _active = true;
    }

    public void ResumeAfterCooldown(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            _until = now + Cooldown;
        }
    }

    public bool IsSuppressed(DateTimeOffset now)
    {
        lock (_gate) return _active || now < _until;
    }
}
