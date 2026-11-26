using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Tells a page when a dictation or meeting has finished, so it can reload what it shows.
/// <para>
/// Pages read the library when they open. A dictation is normally made while Muesli sits in the
/// background on whatever page was last open, so without this the page the user comes back to is
/// the one from before they spoke. The Dictations and Meetings pages already listen to their own
/// engines; this is the same signal for every other page that shows history.
/// </para>
/// <para>
/// The callback runs on the UI thread, each time either engine reports a change while neither is
/// recording or working. It is deliberately not limited to a busy-to-idle transition: the change
/// is delivered through the dispatcher, and by the time it runs a short dictation can already be
/// idle again, so the transition would never be seen.
/// </para>
/// </summary>
public sealed class LibraryActivityWatcher(
    WinUiDictationContext dictation,
    WinUiMeetingContext meetings,
    IUiDispatcher dispatcher,
    Action onSettled)
{
    private bool _running;
    private int _checkQueued;

    public void Start()
    {
        if (_running) return;
        _running = true;
        dictation.Changed += OnChanged;
        meetings.Changed += OnChanged;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        dictation.Changed -= OnChanged;
        meetings.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs args)
    {
        // An engine reports several changes in a row as it finishes (stage cleared, busy flag
        // released). One queued check covers the whole burst: it runs after them and reads the
        // state as it is then, so the page reloads once per finish, not once per notification.
        if (Interlocked.Exchange(ref _checkQueued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(CheckSettled)) Volatile.Write(ref _checkQueued, 0);
    }

    private void CheckSettled()
    {
        Volatile.Write(ref _checkQueued, 0);
        if (!_running) return;
        if (dictation.IsRecording || dictation.IsBusy) return;
        if (meetings.IsRecording || meetings.IsPaused || meetings.IsBusy) return;
        onSettled();
    }
}
