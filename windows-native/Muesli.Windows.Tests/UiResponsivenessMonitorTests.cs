using System.Collections.Concurrent;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class UiResponsivenessMonitorTests
{
    [Fact]
    public async Task A_responsive_ui_thread_logs_no_stalls()
    {
        var log = new ConcurrentQueue<string>();
        var dispatcher = new FakeDispatcher(TimeSpan.Zero);
        using (var monitor = new UiResponsivenessMonitor(dispatcher, log.Enqueue, TimeSpan.FromMilliseconds(5)))
        {
            monitor.Start();
            await EventuallyAsync(() => monitor.Snapshot().Probes >= 10);
            Assert.Equal(0, monitor.Snapshot().OverBudget);
        }

        Assert.DoesNotContain(log, line => line.Contains("stall of", StringComparison.Ordinal));
        Assert.Contains(log, line => line.StartsWith("UI responsiveness (exit)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slow_ui_callback_is_logged_as_a_stall_over_budget()
    {
        var log = new ConcurrentQueue<string>();
        var dispatcher = new FakeDispatcher(TimeSpan.FromMilliseconds(300));
        using var monitor = new UiResponsivenessMonitor(dispatcher, log.Enqueue, TimeSpan.FromMilliseconds(5));
        monitor.Start();

        await EventuallyAsync(() => log.Any(line => line.Contains("stall of", StringComparison.Ordinal)));

        var snapshot = monitor.Snapshot();
        Assert.True(snapshot.OverBudget >= 1);
        Assert.True(snapshot.OverLogThreshold >= 1);
        Assert.True(snapshot.WorstMs >= 250, $"worst was {snapshot.WorstMs} ms");
    }

    [Fact]
    public async Task A_hang_is_reported_while_blocked_and_again_when_it_ends()
    {
        var log = new ConcurrentQueue<string>();
        var release = new ManualResetEventSlim();
        var dispatcher = new FakeDispatcher(TimeSpan.Zero) { Gate = release };
        using var monitor = new UiResponsivenessMonitor(dispatcher, log.Enqueue, TimeSpan.FromMilliseconds(5));
        monitor.Start();

        await EventuallyAsync(() => log.Any(line => line.Contains("still blocked", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
        release.Set();
        await EventuallyAsync(() => log.Any(line => line.Contains("hang ended after", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));

        Assert.Equal(1, monitor.Snapshot().Hangs);
    }

    [Fact]
    public async Task The_monitor_stops_when_the_dispatcher_refuses_work()
    {
        var dispatcher = new FakeDispatcher(TimeSpan.Zero) { Accepting = false };
        using var monitor = new UiResponsivenessMonitor(dispatcher, _ => { }, TimeSpan.FromMilliseconds(5));
        monitor.Start();

        await Task.Delay(100);

        Assert.Equal(1, dispatcher.Attempts);
        Assert.Equal(0, monitor.Snapshot().Probes);
    }

    private static async Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Runs each callback on a worker after a delay, optionally held behind a gate.</summary>
    private sealed class FakeDispatcher(TimeSpan delay) : IUiDispatcher
    {
        private int _attempts;
        public bool Accepting { get; init; } = true;
        public ManualResetEventSlim? Gate { get; init; }
        public int Attempts => Volatile.Read(ref _attempts);
        public bool HasThreadAccess => false;

        public bool TryEnqueue(Action callback)
        {
            Interlocked.Increment(ref _attempts);
            if (!Accepting) return false;
            _ = Task.Run(async () =>
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                Gate?.Wait();
                callback();
            });
            return true;
        }

        public Task EnqueueAsync(Action callback, CancellationToken cancellationToken = default)
        {
            TryEnqueue(callback);
            return Task.CompletedTask;
        }
    }
}
