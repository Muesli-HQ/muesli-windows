using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Muesli.Windows.Services;

public sealed class MeetingDetectionService : IDisposable
{
    private const int AbsenceScansToRearm = 5; // 5 scans * 3s interval = ~15s before we consider the meeting gone

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly AppLogService _logService = new();
    private string _publishedKey = "";
    private int _absenceScans;
    private int _scanCount;

    public event EventHandler<DetectedMeeting>? MeetingDetected;
    public event EventHandler<MeetingDetectionScan>? ScanCompleted;

    public MeetingDetectionService()
    {
        _timer.Tick += (_, _) => DetectForegroundMeeting();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        _timer.Start();
        _logService.Info("Meeting detection started.");
        DetectMeeting(publish: true);
    }

    public void Stop()
    {
        _timer.Stop();
        _logService.Info("Meeting detection stopped.");
    }

    public void Dispose() => _timer.Stop();

    public MeetingDetectionScan CheckNow(bool publish = true) => DetectMeeting(publish);

    private void DetectForegroundMeeting() => DetectMeeting(publish: true);

    private MeetingDetectionScan DetectMeeting(bool publish)
    {
        _scanCount++;
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return DetectVisibleMeetingWindow(publish, "No foreground window.");
        }

        if (TryDetectMeeting(handle, out var meeting))
        {
            _absenceScans = 0;
            if (publish)
            {
                PublishMeeting(meeting);
            }

            return CompleteScan(MeetingDetectionScan.Detected(meeting, "foreground"));
        }

        return DetectVisibleMeetingWindow(publish, DescribeWindow(handle, "Foreground not a meeting"));
    }

    private MeetingDetectionScan DetectVisibleMeetingWindow(bool publish, string foregroundSummary)
    {
        DetectedMeeting? detected = null;
        var visibleWindows = new List<string>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || handle == IntPtr.Zero)
            {
                return true;
            }

            if (visibleWindows.Count < 8)
            {
                var description = DescribeWindow(handle);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    visibleWindows.Add(description);
                }
            }

            if (TryDetectMeeting(handle, out var meeting))
            {
                detected = meeting;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (detected is null)
        {
            if (_absenceScans < AbsenceScansToRearm)
            {
                _absenceScans++;
                if (_absenceScans >= AbsenceScansToRearm)
                {
                    _publishedKey = "";
                }
            }

            return CompleteScan(MeetingDetectionScan.NotFound(foregroundSummary, visibleWindows));
        }

        _absenceScans = 0;
        if (publish)
        {
            PublishMeeting(detected);
        }

        return CompleteScan(MeetingDetectionScan.Detected(detected, "visible windows"));
    }

    private static bool TryDetectMeeting(IntPtr handle, out DetectedMeeting meeting)
    {
        meeting = default!;
        var title = GetWindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        var processName = "";
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            // The process can exit between foreground capture and lookup.
        }

        var browserUrl = "";
        var platform = DetectPlatform(title, processName, browserUrl);
        if (platform is null && IsBrowserProcess(processName))
        {
            browserUrl = TryGetBrowserUrl(handle, processName) ?? "";
            platform = DetectPlatform(title, processName, browserUrl);
        }

        if (platform is null)
        {
            return false;
        }

        var meetingTitle = CleanMeetingTitle(title, platform, browserUrl);
        // Key intentionally excludes the title — window titles flap (participant counts, tab badges)
        // and would otherwise refire the prompt every few seconds during a single meeting.
        var key = $"{platform}|{processName}|{browserUrl}".ToLowerInvariant();
        meeting = new DetectedMeeting(platform, meetingTitle, title, processName, browserUrl, key);
        return true;
    }

    private MeetingDetectionScan CompleteScan(MeetingDetectionScan scan)
    {
        ScanCompleted?.Invoke(this, scan);
        if (scan.DetectedMeeting is not null || _scanCount % 10 == 1)
        {
            _logService.Info($"Meeting detection scan: {scan.Summary}");
        }

        return scan;
    }

    private void PublishMeeting(DetectedMeeting meeting)
    {
        if (meeting.Key == _publishedKey)
        {
            return;
        }

        _publishedKey = meeting.Key;
        MeetingDetected?.Invoke(this, meeting);
    }

    private static string? DetectPlatform(string title, string processName, string? browserUrl)
    {
        var haystack = $"{title} {processName} {browserUrl}".ToLowerInvariant();
        if (haystack.Contains("meet.google.com") ||
            haystack.Contains("google meet") ||
            haystack.Contains(" - meet ") ||
            haystack.StartsWith("meet -", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("msedge_proxy", StringComparison.OrdinalIgnoreCase))
        {
            return "Google Meet";
        }

        if (haystack.Contains("zoom meeting") ||
            haystack.Contains("zoom workplace") ||
            haystack.Contains("zoom.us/j/") ||
            haystack.Contains("zoom.us/wc/") ||
            processName.Equals("Zoom", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("CptHost", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("Zoom Workplace", StringComparison.OrdinalIgnoreCase))
        {
            return "Zoom";
        }

        if (haystack.Contains("microsoft teams") ||
            haystack.Contains("teams meeting") ||
            haystack.Contains("teams.microsoft.com") ||
            haystack.Contains("teams.live.com") ||
            processName.Contains("ms-teams", StringComparison.OrdinalIgnoreCase) ||
            processName.Contains("Teams", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft Teams";
        }

        if (haystack.Contains("webex") || processName.Contains("Webex", StringComparison.OrdinalIgnoreCase))
        {
            return "Webex";
        }

        return null;
    }

    private static string CleanMeetingTitle(string title, string platform, string? browserUrl)
    {
        if (!string.IsNullOrWhiteSpace(browserUrl) &&
            Uri.TryCreate(browserUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Contains("meet.google.com", StringComparison.OrdinalIgnoreCase))
        {
            var code = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(code))
            {
                return $"Google Meet {code}";
            }
        }

        var cleaned = title
            .Replace("- Google Meet", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Google Meet", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Zoom Meeting", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Microsoft Teams", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Webex", "", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', '|', '\u2013', '\u2014');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = $"{platform} meeting";
        }

        return cleaned;
    }

    private static string? TryGetBrowserUrl(IntPtr handle, string processName)
    {
        if (!IsBrowserProcess(processName))
        {
            return null;
        }

        try
        {
            var root = AutomationElement.FromHandle(handle);
            var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edits = root.FindAll(TreeScope.Descendants, editCondition);
            foreach (AutomationElement edit in edits)
            {
                if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    continue;
                }

                var value = ((ValuePattern)pattern).Current.Value;
                if (LooksLikeMeetingUrl(value))
                {
                    return NormalizeUrl(value);
                }
            }
        }
        catch
        {
            // UI Automation can fail for elevated browsers or locked-down windows.
        }

        return null;
    }

    private static bool IsBrowserProcess(string processName)
    {
        return processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("opera", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMeetingUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("meet.google.com") ||
               normalized.Contains("zoom.us/j/") ||
               normalized.Contains("zoom.us/wc/") ||
               normalized.Contains("teams.microsoft.com") ||
               normalized.Contains("teams.live.com") ||
               normalized.Contains("webex.com");
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"https://{trimmed}";
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return "";
        }

        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string DescribeWindow(IntPtr handle, string prefix = "")
    {
        var title = GetWindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        var processName = "";
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            // Process may exit during enumeration.
        }

        var description = string.IsNullOrWhiteSpace(processName)
            ? title
            : $"{processName}: {title}";
        return string.IsNullOrWhiteSpace(prefix) ? description : $"{prefix}: {description}";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}

public sealed record DetectedMeeting(
    string Platform,
    string Title,
    string WindowTitle,
    string ProcessName,
    string? BrowserUrl,
    string Key);

public sealed record MeetingDetectionScan(
    bool Found,
    string Summary,
    DetectedMeeting? DetectedMeeting)
{
    public static MeetingDetectionScan Detected(DetectedMeeting meeting, string source)
    {
        return new MeetingDetectionScan(
            true,
            $"Found {meeting.Platform} from {source}: {meeting.ProcessName}: {meeting.WindowTitle}",
            meeting);
    }

    public static MeetingDetectionScan NotFound(string foregroundSummary, IReadOnlyList<string> visibleWindows)
    {
        var windows = visibleWindows.Count == 0
            ? "No visible windows with titles."
            : string.Join(" | ", visibleWindows);
        return new MeetingDetectionScan(false, $"{foregroundSummary}. Visible: {windows}", null);
    }
}
