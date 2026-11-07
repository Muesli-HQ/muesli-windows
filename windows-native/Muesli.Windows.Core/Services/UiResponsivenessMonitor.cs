using System.Diagnostics;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

/// <summary>
/// Measures how long the UI thread takes to run a trivial posted callback and logs stalls against
/// the 50 ms budget in docs/PERFORMANCE_BUDGETS.md.
/// </summary>
/// <remarks>
/// A background thread posts a no-op every <see cref="ProbeInterval"/> and times how long it waits.
/// That latency is what a click or keystroke experiences, so it catches synchronous disk, registry,
/// credential or COM work on the UI thread regardless of which page or service caused it. A hang is
/// reported while it is still happening, so a frozen window leaves evidence in the log even if the
/// user kills the process.
/// </remarks>
public sealed class UiResponsivenessMonitor : IDisposable
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan StallBudget = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan LogThreshold = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(10);
    private const int MaxStallLinesPerMinute = 20;

    private readonly IUiDispatcher _dispatcher;
    private readonly Action<string> _log;
    private readonly TimeSpan _probeInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _statsGate = new();
    // Never disposed: a probe still queued on the UI thread at shutdown may Set it afterwards.
    private readonly ManualResetEventSlim _handled = new();
    private Thread? _thread;
    private long _probes;
    private long _overBudget;
    private long _overLogThreshold;
    private long _hangs;
    private double _worstMs;
    private int _linesThisMinute;
    private long _minuteStartedAt = Stopwatch.GetTimestamp();

    public UiResponsivenessMonitor(IUiDispatcher dispatcher, Action<string> log, TimeSpan? probeInterval = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _probeInterval = probeInterval ?? ProbeInterval;
    }

    public UiResponsivenessSnapshot Snapshot()
    {
        lock (_statsGate)
        {
            return new UiResponsivenessSnapshot(_probes, _overBudget, _overLogThreshold, _hangs, _worstMs);
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "Muesli UI responsiveness monitor" };
        _thread.Start();
    }

    private void Run()
    {
        var token = _stop.Token;
        var lastSummary = Stopwatch.GetTimestamp();
        var handled = _handled;
        try
        {
            while (!token.IsCancellationRequested)
            {
                handled.Reset();
                var posted = Stopwatch.GetTimestamp();
                if (!_dispatcher.TryEnqueue(handled.Set))
                {
                    return; // The dispatcher is shutting down.
                }

                var hangReported = false;
                if (!handled.Wait(HangThreshold, token))
                {
                    hangReported = true;
                    Record(hang: true);
                    _log($"UI responsiveness: UI thread has not responded for {HangThreshold.TotalMilliseconds:0} ms and is still blocked.");
                    handled.Wait(token);
                }

                var latency = Stopwatch.GetElapsedTime(posted);
                Observe(latency, hangReported);

                if (Stopwatch.GetElapsedTime(lastSummary) >= SummaryInterval)
                {
                    lastSummary = Stopwatch.GetTimestamp();
                    LogSummary("periodic");
                }

                token.WaitHandle.WaitOne(_probeInterval);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Observe(TimeSpan latency, bool hangReported)
    {
        var milliseconds = latency.TotalMilliseconds;
        bool logLine;
        lock (_statsGate)
        {
            _probes++;
            if (milliseconds > _worstMs) _worstMs = milliseconds;
            if (latency >= StallBudget) _overBudget++;
            if (latency < LogThreshold) return;
            _overLogThreshold++;
            if (Stopwatch.GetElapsedTime(_minuteStartedAt) >= TimeSpan.FromMinutes(1))
            {
                _minuteStartedAt = Stopwatch.GetTimestamp();
                _linesThisMinute = 0;
            }

            logLine = hangReported || _linesThisMinute++ < MaxStallLinesPerMinute;
        }

        if (logLine)
        {
            _log(hangReported
                ? $"UI responsiveness: UI thread hang ended after {milliseconds:0} ms."
                : $"UI responsiveness: UI thread stall of {milliseconds:0} ms (budget {StallBudget.TotalMilliseconds:0} ms).");
        }
    }

    private void Record(bool hang)
    {
        lock (_statsGate)
        {
            if (hang) _hangs++;
        }
    }

    private void LogSummary(string reason)
    {
        var snapshot = Snapshot();
        if (snapshot.Probes == 0) return;
        _log(
            $"UI responsiveness ({reason}): probes={snapshot.Probes}; over {StallBudget.TotalMilliseconds:0} ms={snapshot.OverBudget}; " +
            $"over {LogThreshold.TotalMilliseconds:0} ms={snapshot.OverLogThreshold}; hangs={snapshot.Hangs}; worst={snapshot.WorstMs:0} ms.");
    }

    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        LogSummary("exit");
        _stop.Dispose();
    }
}

public sealed record UiResponsivenessSnapshot(long Probes, long OverBudget, long OverLogThreshold, long Hangs, double WorstMs);
