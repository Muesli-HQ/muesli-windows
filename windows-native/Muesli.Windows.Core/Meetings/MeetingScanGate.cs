namespace Muesli.Windows.Services;

/// <summary>
/// Non-overlapping scan coordination. A scan that is already running causes the next attempt to be
/// skipped rather than queued, so a slow scan can never stack. Kept framework-free so the
/// non-overlap contract is testable without walls of wall-clock timing.
/// </summary>
public sealed class MeetingScanGate
{
    private int _inScan;

    public int SkippedCount { get; private set; }

    public bool IsScanning => Volatile.Read(ref _inScan) != 0;

    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _inScan, 1, 0) == 0)
        {
            return true;
        }

        SkippedCount++;
        return false;
    }

    public void Exit() => Interlocked.Exchange(ref _inScan, 0);
}

/// <summary>
/// The per-scan time budget policy. The walk continues until the budget elapses; the decision is a
/// pure predicate so it can be tested deterministically.
/// </summary>
public static class MeetingScanBudget
{
    public static bool ShouldStop(long elapsedMs, long budgetMs) => elapsedMs > budgetMs;
}
