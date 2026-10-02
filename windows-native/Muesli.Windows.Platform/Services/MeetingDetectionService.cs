using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.UIA3;
using FlaUI.Core.Definitions;

namespace Muesli.Windows.Services;

public sealed class MeetingDetectionService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Hard ceiling for one scan's window walk. A browser UI Automation subtree can stall; the walk
    /// stops as soon as this elapses so the 3-second cadence is not held hostage by one window.
    /// </summary>
    private const long ScanBudgetMs = 5000;

    /// <summary>How long a browser URL observation is reused so repeated scans do not re-walk UIA.</summary>
    private static readonly TimeSpan BrowserUrlCacheTtl = TimeSpan.FromSeconds(5);

    private const int MaxBrowserEditScan = 32;

    private readonly AppLogService _logService = new();
    private readonly MeetingPresenceSignals _signals = new();
    private readonly MeetingCandidateResolver _resolver = new();
    private readonly MeetingScanGate _scanGate = new();
    private readonly Dictionary<string, (string Url, DateTime At)> _browserUrlCache = new();
    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private int _scanCount;
    private int _disposed;

    public event EventHandler<DetectedMeeting>? MeetingDetected;
    public event EventHandler<MeetingDetectionScan>? ScanCompleted;

    /// <summary>Raised when a confirmed meeting genuinely ends, so prompt suppression can be forgotten.</summary>
    public event EventHandler<string>? MeetingEnded;

    public bool IsRunning => _loop is { IsCancellationRequested: false };

    /// <summary>
    /// Scanning runs on a background loop rather than a UI timer. A scan walks top-level windows and,
    /// for plausible browser candidates only, a bounded UI Automation subtree; on the dispatcher that
    /// would stall rendering and input for as long as the walk takes.
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
        _browserUrlCache.Clear();
        _logService.Info("Meeting detection stopped.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _signals.Dispose();
    }

    /// <summary>Suppresses further prompts for this meeting until it ends and starts again.</summary>
    public void DismissCandidate(string? key) => _resolver.Dismiss(key);

    public MeetingDetectionScan CheckNow(bool publish = true) =>
        ScanAsync(publish, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Runs one scan. Overlapping scans are collapsed rather than queued: a slow scan must never let
    /// a second one run on top of it.
    /// </summary>
    private Task<MeetingDetectionScan> ScanAsync(bool publish, CancellationToken cancellationToken)
    {
        if (!_scanGate.TryEnter())
        {
            _logService.Info($"Meeting detection scan skipped: a scan was already running. skippedScans={_scanGate.SkippedCount}");
            return Task.FromResult(MeetingDetectionScan.NotFound("A detection scan was already running.", 0));
        }

        try
        {
            return Task.FromResult(DetectMeeting(publish, cancellationToken));
        }
        catch (Exception exception)
        {
            _logService.Info($"Meeting detection scan failed. category={exception.GetType().Name}");
            return Task.FromResult(MeetingDetectionScan.NotFound("Detection scan failed.", 0));
        }
        finally
        {
            _scanGate.Exit();
        }
    }

    private MeetingDetectionScan DetectMeeting(bool publish, CancellationToken cancellationToken)
    {
        _scanCount++;
        var stopwatch = Stopwatch.StartNew();
        var candidates = 0;

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero &&
            ObserveWindow(foreground) is { } foregroundObservation &&
            TryDetectMeeting(foregroundObservation, isForeground: true, out var foregroundMeeting))
        {
            candidates++;
            if (publish)
            {
                PublishMeeting(foregroundMeeting);
            }

            return CompleteScan(MeetingDetectionScan.Detected(foregroundMeeting, "foreground"), stopwatch, candidates);
        }

        var foregroundSummary = foreground == IntPtr.Zero
            ? "No foreground window."
            : DescribeWindow(foreground, "Foreground not a meeting");
        return DetectVisibleMeetingWindow(publish, foregroundSummary, stopwatch, cancellationToken);
    }

    private MeetingDetectionScan DetectVisibleMeetingWindow(
        bool publish, string foregroundSummary, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var observations = new List<WindowObservation>();
        var visibleWindowCount = 0;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || IsWindowCloaked(handle) || handle == IntPtr.Zero)
            {
                return true;
            }

            visibleWindowCount++;
            if (ObserveWindow(handle) is { } observation)
            {
                observations.Add(observation);
            }

            return true;
        }, IntPtr.Zero);

        // Meeting-shaped windows first, browsers next, unrelated windows last; the scan budget is
        // spent on the candidates most likely to be a meeting.
        observations.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));

        var candidates = 0;
        var budgetExceeded = false;
        DetectedMeeting? detected = null;
        foreach (var observation in observations)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (MeetingScanBudget.ShouldStop(stopwatch.ElapsedMilliseconds, ScanBudgetMs))
            {
                budgetExceeded = true;
                break;
            }

            if (!IsPlausibleForUiAutomation(observation, isForeground: false)) continue;
            candidates++;
            if (TryDetectMeeting(observation, isForeground: false, out var meeting))
            {
                detected = meeting;
                break;
            }
        }

        if (budgetExceeded)
        {
            _logService.Info(
                $"Meeting detection scan stopped at the {ScanBudgetMs} ms budget. candidates={candidates}; visibleWindows={visibleWindowCount}");
        }

        if (detected is null)
        {
            var empty = _signals.Capture(MeetingEvidenceStrength.None, null);
            var decision = _resolver.Observe(empty);
            if (decision.Action == MeetingCandidateAction.Ended)
            {
                _resolver.Forget(decision.Key);
                if (!string.IsNullOrWhiteSpace(decision.Key)) MeetingEnded?.Invoke(this, decision.Key);
            }

            return CompleteScan(MeetingDetectionScan.NotFound(
                $"{foregroundSummary}. Suppressed: {MeetingCandidateResolver.DescribeSuppression(empty)}",
                visibleWindowCount), stopwatch, candidates);
        }

        if (publish)
        {
            PublishMeeting(detected);
        }

        return CompleteScan(MeetingDetectionScan.Detected(detected, "visible windows"), stopwatch, candidates);
    }

    /// <summary>Collects native title/process/evidence without any UI Automation work.</summary>
    private static WindowObservation? ObserveWindow(IntPtr handle)
    {
        var title = GetWindowTitle(handle);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
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
            return null;
        }

        if (IsSelfProcess(processName) || title.StartsWith("Muesli", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var evidence = MeetingEvidenceClassifier.Classify(title, processName, null);
        var isBrowser = IsBrowserProcess(processName);
        var isDedicated = MeetingPresenceSignals.IsDedicatedMeetingProcess(processName);
        var priority = isDedicated || evidence == MeetingEvidenceStrength.Strong ? 0
            : evidence != MeetingEvidenceStrength.None ? 1
            : isBrowser && MeetingEvidenceClassifier.TitleMentionsPlatform(title) ? 2
            : 3;

        return new WindowObservation(handle, title, processName, checked((int)processId), evidence, isBrowser, priority);
    }

    /// <summary>
    /// True when a window is worth a UI Automation walk: it carries meeting evidence, or it is a
    /// browser whose title/foreground position makes a meeting URL plausible. A generic browser or
    /// unrelated window is skipped before any expensive work.
    /// </summary>
    private static bool IsPlausibleForUiAutomation(WindowObservation observation, bool isForeground) =>
        observation.Evidence != MeetingEvidenceStrength.None ||
        (observation.IsBrowser &&
         (isForeground || MeetingEvidenceClassifier.TitleMentionsPlatform(observation.Title)));

    private bool TryDetectMeeting(WindowObservation observation, bool isForeground, out DetectedMeeting meeting)
    {
        meeting = default!;
        var plausibleUrl = IsPlausibleForUiAutomation(observation, isForeground);
        if (!plausibleUrl && !observation.IsBrowser)
        {
            return false;
        }

        var browserUrl = "";
        MeetingUrlMatch? urlMatch = null;
        if (observation.IsBrowser && plausibleUrl)
        {
            browserUrl = TryGetBrowserUrl(observation) ?? "";
            urlMatch = MeetingUrlParser.TryParse(browserUrl);
        }

        var evidence = MeetingEvidenceClassifier.Classify(observation.Title, observation.ProcessName, browserUrl);
        if (evidence == MeetingEvidenceStrength.None)
        {
            if (observation.IsBrowser)
            {
                var browserKey = $"meeting|{observation.ProcessName}|{observation.ProcessId}".ToLowerInvariant();
                var browserPresence = _signals.Capture(MeetingEvidenceStrength.Strong, browserKey, observation.ProcessId,
                    isForeground, requiresMediaActivity: true);
                // macOS can recognize an unknown browser meeting from attributed input, including a renderer process.
                if (browserPresence.CandidateMicrophoneInUse)
                {
                    meeting = new DetectedMeeting("Meeting", observation.Title, observation.Title, observation.ProcessName,
                        "", browserKey, observation.ProcessId, MeetingEvidenceStrength.Strong, observation.Handle, browserPresence);
                    return true;
                }
            }
            return false;
        }

        // A validated join URL names the platform authoritatively; otherwise fall back to the
        // process/title heuristic, which only ever produces weak evidence.
        var platform = urlMatch?.Platform ?? DetectPlatform(observation.Title, observation.ProcessName, browserUrl);
        if (platform is null)
        {
            return false;
        }

        var meetingTitle = urlMatch?.DisplayName ?? CleanMeetingTitle(observation.Title, platform, browserUrl);
        var joinUrl = urlMatch?.JoinUrl ?? "";
        var identity = urlMatch is null ? observation.ProcessId.ToString() : joinUrl;
        var key = $"{platform}|{observation.ProcessName}|{identity}".ToLowerInvariant();
        var presence = _signals.Capture(evidence, key, observation.ProcessId, isForeground,
            requiresMediaActivity: urlMatch is null || !isForeground,
            requiresDuplexAudio: urlMatch is null && !observation.IsBrowser);
        if (!MeetingCandidateResolver.QualifiesAsMeeting(presence)) return false;
        meeting = new DetectedMeeting(
            platform, meetingTitle, observation.Title, observation.ProcessName, joinUrl, key,
            observation.ProcessId, evidence, observation.Handle, presence);
        return true;
    }

    private MeetingDetectionScan CompleteScan(MeetingDetectionScan scan, Stopwatch stopwatch, int candidates)
    {
        var enriched = scan with { ElapsedMs = stopwatch.ElapsedMilliseconds, CandidateCount = candidates };
        ScanCompleted?.Invoke(this, enriched);
        if (enriched.DetectedMeeting is not null || _scanCount % 10 == 1)
        {
            _logService.Info(
                $"Meeting detection scan: {enriched.Summary} elapsedMs={enriched.ElapsedMs}; candidates={enriched.CandidateCount}; skippedScans={_scanGate.SkippedCount}.");
        }

        return enriched;
    }

    private void PublishMeeting(DetectedMeeting meeting)
    {
        // Sensor state corroborates the window evidence; the resolver owns the decision so the
        // conservative policy and its hysteresis live in one tested place.
        var snapshot = meeting.Presence ?? _signals.Capture(meeting.Evidence, meeting.Key);
        var decision = _resolver.Observe(snapshot);
        if (decision.Action == MeetingCandidateAction.Ended)
        {
            _resolver.Forget(decision.Key);
            if (!string.IsNullOrWhiteSpace(decision.Key)) MeetingEnded?.Invoke(this, decision.Key);
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

        if (processName.Equals("slack", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (processName.Equals("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (processName.Contains("chime", StringComparison.OrdinalIgnoreCase)) return "Amazon Chime";
        if (processName.Equals("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";

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

    /// <summary>
    /// Reads the browser's address bar through a bounded UI Automation walk, caching the observation
    /// briefly so the 3-second loop does not re-walk the same subtree every scan.
    /// </summary>
    private string? TryGetBrowserUrl(WindowObservation observation)
    {
        var key = $"{observation.ProcessName}:{observation.Handle.ToInt64():X}";
        if (_browserUrlCache.TryGetValue(key, out var cached) &&
            DateTime.UtcNow - cached.At < BrowserUrlCacheTtl)
        {
            return string.IsNullOrEmpty(cached.Url) ? null : cached.Url;
        }

        var url = ReadBrowserUrlBounded(observation.Handle);
        if (_browserUrlCache.Count > 64)
        {
            _browserUrlCache.Clear();
        }

        _browserUrlCache[key] = (url ?? "", DateTime.UtcNow);
        return url;
    }

    private static string? ReadBrowserUrlBounded(IntPtr handle)
    {
        try
        {
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(handle);
            var edits = root.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            var scanned = 0;
            foreach (var edit in edits)
            {
                if (++scanned > MaxBrowserEditScan) break;
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

    private static bool IsSelfProcess(string processName)
    {
        return processName.Equals("Muesli.Windows.WinUI", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("Muesli.Windows.Indicator.Wpf", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("Muesli.Windows.CommandHost", StringComparison.OrdinalIgnoreCase) ||
               processName.Equals("Muesli", StringComparison.OrdinalIgnoreCase);
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

    private sealed record WindowObservation(
        IntPtr Handle,
        string Title,
        string ProcessName,
        int ProcessId,
        MeetingEvidenceStrength Evidence,
        bool IsBrowser,
        int Priority);

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

    private const int DwmwaCloaked = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    /// <summary>A cloaked window is a UWP/ghost window that is not really on screen.</summary>
    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record DetectedMeeting(
    string Platform,
    string Title,
    string WindowTitle,
    string ProcessName,
    string? BrowserUrl,
    string Key,
    int ProcessId,
    MeetingEvidenceStrength Evidence = MeetingEvidenceStrength.Weak,
    nint WindowHandle = 0,
    MeetingPresenceSnapshot? Presence = null);

public sealed record MeetingDetectionScan(
    bool Found,
    string Summary,
    DetectedMeeting? DetectedMeeting)
{
    public long ElapsedMs { get; init; }
    public int CandidateCount { get; init; }

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
