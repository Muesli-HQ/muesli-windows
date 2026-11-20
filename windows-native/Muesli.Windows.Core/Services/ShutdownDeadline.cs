namespace Muesli.Windows.Core.Services;

/// <summary>
/// macOS ShutdownDeadline (4a36479f): the work that must finish before quitting gets a deadline. A step waiting on
/// something that never finishes (a stuck meeting operation holding the lifecycle gate) would otherwise keep the
/// app from ever quitting. An overrunning operation is left running; the app exits around it.
/// </summary>
public static class ShutdownDeadline
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(8);

    /// <summary>True when <paramref name="operation"/> finished (or failed) in time; false at the deadline.</summary>
    public static async Task<bool> RunAsync(TimeSpan deadline, Func<Task> operation)
    {
        Task work;
        try { work = operation(); }
        catch (Exception exception) { work = Task.FromException(exception); }
        if (await Task.WhenAny(work, Task.Delay(deadline)).ConfigureAwait(false) != work) return false;
        await work.ConfigureAwait(false); // a failed step is reported to the caller, which still quits
        return true;
    }
}
