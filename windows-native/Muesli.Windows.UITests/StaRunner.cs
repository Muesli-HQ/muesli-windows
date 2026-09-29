using System.Runtime.ExceptionServices;

namespace Muesli.Windows.UITests;

internal static class StaRunner
{
    public static void Run(Action work) =>
        Run(() =>
        {
            work();
            return 0;
        });

    public static T Run<T>(Func<T> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return work();

        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Muesli.UITests.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(4)))
            throw new TimeoutException("STA UI Automation work did not finish within four minutes.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }
}
