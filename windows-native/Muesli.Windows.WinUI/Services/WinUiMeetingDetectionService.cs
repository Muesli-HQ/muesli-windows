using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>WinUI adapter over the shared Windows window, browser URL and sensor detector.</summary>
public sealed class WinUiMeetingDetectionService : IDisposable
{
    private readonly MeetingDetectionService _detector = new();
    public WinUiMeetingDetectionService()
    {
        _detector.MeetingDetected += (_, meeting) => MeetingDetected?.Invoke(this,
            new WinUiDetectedMeeting(meeting.Key, $"{meeting.Platform}: {meeting.Title}", meeting));
        _detector.ScanCompleted += (_, scan) =>
        {
            Status = scan.Summary;
            ScanCompleted?.Invoke(this, scan);
        };
    }
    public event EventHandler<WinUiDetectedMeeting>? MeetingDetected;
    public event EventHandler<MeetingDetectionScan>? ScanCompleted;
    public string Status { get; private set; } = "No current Windows meeting evidence.";
    public void Start() => _detector.Start();
    public void Stop() { _detector.Stop(); Status = "Meeting detection is off."; }
    public void Dismiss(string? key) => _detector.DismissCandidate(key);
    public void Dispose() => _detector.Dispose();
}

public sealed record WinUiDetectedMeeting(string Key, string Description, DetectedMeeting? Meeting = null);