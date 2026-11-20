using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-10 (macOS 4a36479f, e9d4f2a9, 875664ea): quitting never waits on a stuck step past the deadline.</summary>
public sealed class ShutdownDeadlineTests
{
    [Fact]
    public async Task WorkThatFinishesInTimeIsReportedFinished()
    {
        var ran = false;
        Assert.True(await ShutdownDeadline.RunAsync(TimeSpan.FromSeconds(5), async () => { await Task.Yield(); ran = true; }));
        Assert.True(ran);
    }

    [Fact]
    public async Task WorkThatCompletedBeforeTheWaitBeganIsRemembered()
    {
        // The release arrives before the caller parks on it.
        Assert.True(await ShutdownDeadline.RunAsync(TimeSpan.FromMilliseconds(1), () => Task.CompletedTask));
    }

    [Fact]
    public async Task AStuckStepIsAbandonedAtTheDeadlineAndKeepsRunning()
    {
        var release = new TaskCompletionSource();
        var stuck = release.Task.ContinueWith(_ => { }, TaskScheduler.Default);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await ShutdownDeadline.RunAsync(TimeSpan.FromMilliseconds(100), () => stuck));
        Assert.InRange(watch.ElapsedMilliseconds, 90, 5_000);
        // The overrunning work is left running, not cancelled.
        release.SetResult();
        await stuck.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AFailedStepIsReportedToTheCaller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ShutdownDeadline.RunAsync(TimeSpan.FromSeconds(5), () => throw new InvalidOperationException("boom")));
    }
}
