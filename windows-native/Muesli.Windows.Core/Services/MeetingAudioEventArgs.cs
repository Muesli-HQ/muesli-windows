namespace Muesli.Windows.Services;

/// <summary>
/// Neutral event payload raised when the meeting capture health snapshot changes.
/// The payload deliberately contains no WPF or WinUI dispatcher/window types so capture
/// orchestration can be hosted by either desktop shell.
/// </summary>
public sealed class MeetingAudioHealthChangedEventArgs(MeetingAudioHealthSnapshot snapshot) : EventArgs
{
    public MeetingAudioHealthSnapshot Snapshot { get; } = snapshot;
}
