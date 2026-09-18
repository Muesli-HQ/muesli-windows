using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.UIA3;
using FlaUI.Core.Definitions;

namespace Muesli.Windows.Services;

public sealed class MeetingDetectionService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private readonly AppLogService _logService = new();
    private readonly MeetingPresenceSignals _signals = new();
    private readonly MeetingCandidateResolver _resolver = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private int _scanCount;
    private int _disposed;

    public event EventHandler<DetectedMeeting>? MeetingDetected;
    public event EventHandler<MeetingDetectionScan>? ScanCompleted;

    public bool IsRunning => _loop is { IsCancellationRequested: false };

    /// <summary>
    /// Scanning runs on a background loop rather than a a UI timer. A scan walks
    /// every top-level window and, for browsers, a UI Automation subtree; on the dispatcher that
    /// stalls rendering and input for as long as the walk takes.
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || IsRunning) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _logService.Info("Meeting detection started.");
        _loopTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await ScanAsync(publish: true, token).ConfigureAwait(false);
                    await Task.Delay(ScanInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logService.Error("Meeting detection loop stopped unexpectedly.", exception);
            }
        }, token);
    }

    public void Stop()
    {
        var loop = Interlocked.Exchange(ref _loop, null);
        if (loop is null) return;
        loop.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        loop.Dispose();
        _loopTask = null;
        _resolver.Reset();
        _logService.Info("Meeting detection stopped.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _signals.Dispose();
        _scanGate.Dispose();
    }

    /// <summary>Suppresses further prompts for this meeting until it ends and starts again.</summary>
    public void DismissCandidate(string? key) => _resolver.Dismiss(key);

    public MeetingDetectionScan CheckNow(bool publish = true) =>
        ScanAsync(publish, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<MeetingDetectionScan> ScanAsync(bool publish, CancellationToken cancellationToken)
    {
        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return MeetingDetectionScan.NotFound("A detection scan was already running.", 0);
        }
        try
        {
            return DetectMeeting(publish);
        }
        catch (Exception exception)
        {
            _logService.Info($"Meeting detection scan failed. category={exception.GetType().Name}");
            return MeetingDetectionScan.NotFound("Detection scan failed.", 0);
        }
        finally
        {
            _scanGate.Release();
        }
    }

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
        var visibleWindowCount = 0;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || handle == IntPtr.Zero)
            {
                return true;
            }

            visibleWindowCount++;

            if (TryDetectMeeting(handle, out var meeting))
            {
                detected = meeting;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        if (detected is null)
        {
            var empty = _signals.Capture(MeetingEvidenceStrength.None, null);
            var decision = _resolver.Observe(empty);
            if (decision.Action == MeetingCandidateAction.Ended)
            {
                _resolver.Forget(decision.Key);
            }
            return CompleteScan(MeetingDetectionScan.NotFound(
                $"{foregroundSummary}. Suppressed: {MeetingCandidateResolver.DescribeSuppression(empty)}",
                visibleWindowCount));
        }

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
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // The process can exit between foreground capture and lookup.
        }

        var browserUrl = "";
        MeetingUrlMatch? urlMatch = null;
        if (IsBrowserProcess(processName))
        {
            browserUrl = TryGetBrowserUrl(handle, processName) ?? "";
            urlMatch = MeetingUrlParser.TryParse(browserUrl);
        }

        var evidence = MeetingEvidenceClassifier.Classify(title, processName, browserUrl);
        if (evidence == MeetingEvidenceStrength.None)
        {
            return false;
        }

        // A validated join URL names the platform authoritatively; otherwise fall back to the
        // process/title heuristic, which only ever produces weak evidence.
        var platform = urlMatch?.Platform ?? DetectPlatform(title, processName, browserUrl);
        if (platform is null)
        {
            return false;
        }

        var meetingTitle = urlMatch?.DisplayName ?? CleanMeetingTitle(title, platform, browserUrl);
        var joinUrl = urlMatch?.JoinUrl ?? "";
        var key = $"{platform}|{processName}|{meetingTitle}|{joinUrl}".ToLowerInvariant();
        meeting = new DetectedMeeting(
            platform, meetingTitle, title, processName, joinUrl, key, checked((int)processId), evidence);
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
        // Sensor state corroborates the window evidence; the resolver owns the decision so the
        // conservative policy and its hysteresis live in one tested place.
        var snapshot = _signals.Capture(meeting.Evidence, meeting.Key);
        var decision = _resolver.Observe(snapshot);
        if (decision.Action == MeetingCandidateAction.Ended)
        {
            _resolver.Forget(decision.Key);
            return;
        }
        if (decision.Action != MeetingCandidateAction.Prompt)
        {
            return;
        }
        _logService.Info(
            $"Meeting candidate confirmed. platform={meeting.Platform}; evidence={meeting.Evidence}; mic={snapshot.MicrophoneInUse}; camera={snapshot.CameraInUse}; reason={decision.Reason}");
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
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(handle);
            var edits = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            foreach (var edit in edits)
            {
                if (!edit.Patterns.Value.IsSupported) continue;
                var value = edit.Patterns.Value.Pattern.Value.ValueOrDefault;
                if (value is not null && LooksLikeMeetingUrl(value))
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

    /// <summary>Strict, host-anchored validation; a substring match would accept lookalike domains.</summary>
    private static bool LooksLikeMeetingUrl(string? value) => MeetingUrlParser.IsSupportedMeetingUrl(value);

    private static string NormalizeUrl(string value) =>
        MeetingUrlParser.TryParse(value)?.JoinUrl ?? value.Trim();

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
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch
        {
            // Process may exit during enumeration.
        }

        var titleFingerprint = AppLogService.SensitiveTextFingerprint(title);
        var description = string.IsNullOrWhiteSpace(processName)
            ? $"window title hash {titleFingerprint}"
            : $"{processName}; title hash {titleFingerprint}";
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
    string Key,
    int ProcessId,
    MeetingEvidenceStrength Evidence = MeetingEvidenceStrength.Weak);

public sealed record MeetingDetectionScan(
    bool Found,
    string Summary,
    DetectedMeeting? DetectedMeeting)
{
    public static MeetingDetectionScan Detected(DetectedMeeting meeting, string source)
    {
        return new MeetingDetectionScan(
            true,
            $"Found {meeting.Platform} from {source}; process={meeting.ProcessName}; titleHash={AppLogService.SensitiveTextFingerprint(meeting.WindowTitle)}",
            meeting);
    }

    public static MeetingDetectionScan NotFound(string foregroundSummary, int visibleWindowCount)
    {
        return new MeetingDetectionScan(
            false,
            $"{foregroundSummary}. Visible top-level window count: {visibleWindowCount}.",
            null);
    }
}
