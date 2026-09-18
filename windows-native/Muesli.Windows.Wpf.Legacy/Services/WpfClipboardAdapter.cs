using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>WPF-only clipboard adapter kept at the fallback shell boundary.</summary>
public sealed class WpfClipboardAdapter : IClipboardAdapter
{
    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        InvokeWithRetriesAsync(() => new ClipboardSnapshot(System.Windows.Clipboard.GetDataObject()), cancellationToken);

    public Task SetTextAsync(string text, CancellationToken cancellationToken) =>
        InvokeWithRetriesAsync(() => System.Windows.Clipboard.SetText(text), cancellationToken);

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken) =>
        InvokeWithRetriesAsync(
            () => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null,
            cancellationToken);

    public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Data is not System.Windows.IDataObject data)
        {
            return InvokeWithRetriesAsync(System.Windows.Clipboard.Clear, cancellationToken);
        }

        return InvokeWithRetriesAsync(() => System.Windows.Clipboard.SetDataObject(data, true), cancellationToken);
    }

    private static async Task<T> InvokeWithRetriesAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                {
                    return await dispatcher.InvokeAsync(
                        action,
                        System.Windows.Threading.DispatcherPriority.Send,
                        cancellationToken);
                }

                return action();
            }
            catch (COMException) when (attempt < 4)
            {
                await Task.Delay(20 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static async Task InvokeWithRetriesAsync(Action action, CancellationToken cancellationToken)
    {
        await InvokeWithRetriesAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);
    }
}
