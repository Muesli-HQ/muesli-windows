using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Tells a page when a dictation or meeting has finished and saved something, so it can reload
/// what it shows.
/// <para>
/// Pages read the library when they open. A dictation is normally made while Muesli sits in the
/// background on whatever page was last open, so without this the page the user comes back to is
/// the one from before they spoke. The Dictations and Meetings pages already listen to their own
/// engines; this is the same signal for every other page that shows history.
/// </para>
/// <para>
/// The callback runs on the UI thread when an engine reports a change, that engine is no longer
/// recording or working, and the library has been written to since the last callback. Each engine
/// is judged on its own: a dictation made while a meeting is recording or paused still counts.
/// The check is deliberately not limited to a busy-to-idle transition: the change is delivered
/// through the dispatcher, and by the time it runs a short dictation can already be idle again,
/// so the transition would never be seen.
/// </para>
/// <para>
/// Reloading a page reads the whole history, so the callback is held back until it is needed: a
/// burst of notifications is one check, and a notification that saved nothing (a recording
/// starting, a dictation with no speech) ends at a counter comparison.
/// </para>
/// </summary>
/// <param name="dictation">Null for a page that shows nothing a dictation can change.</param>
public sealed class LibraryActivityWatcher(
    WinUiLibraryContext library,
    WinUiDictationContext? dictation,
    WinUiMeetingContext meetings,
    IUiDispatcher dispatcher,
    Action onSettled)
{
    private const int FromDictation = 1;
    private const int FromMeeting = 2;

    private bool _running;
    private int _pendingSources;
    private long _seenRevision;

    /// <summary>Call once the page has loaded its data: only later writes count as changes.</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _seenRevision = library.Revision;
        if (dictation is not null) dictation.Changed += OnDictationChanged;
        meetings.Changed += OnMeetingChanged;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (dictation is not null) dictation.Changed -= OnDictationChanged;
        meetings.Changed -= OnMeetingChanged;
    }

    private void OnDictationChanged(object? sender, EventArgs args) => Queue(FromDictation);

    private void OnMeetingChanged(object? sender, EventArgs args) => Queue(FromMeeting);

    private void Queue(int source)
    {
        // An engine reports several changes in a row as it finishes (stage cleared, busy flag
        // released). One queued check covers the whole burst: it runs after them and reads the
        // state as it is then.
        if (Interlocked.Or(ref _pendingSources, source) != 0) return;
        if (!dispatcher.TryEnqueue(CheckSettled)) Interlocked.Exchange(ref _pendingSources, 0);
    }

    private void CheckSettled()
    {
        var sources = Interlocked.Exchange(ref _pendingSources, 0);
        if (!_running) return;

        var dictationSettled = (sources & FromDictation) != 0 && dictation is not null &&
                               !dictation.IsRecording && !dictation.IsBusy;
        var meetingSettled = (sources & FromMeeting) != 0 &&
                             !meetings.IsRecording && !meetings.IsPaused && !meetings.IsBusy;
        if (!dictationSettled && !meetingSettled) return;

        var revision = library.Revision;
        if (revision == _seenRevision) return;
        _seenRevision = revision;
        onSettled();
    }
}
