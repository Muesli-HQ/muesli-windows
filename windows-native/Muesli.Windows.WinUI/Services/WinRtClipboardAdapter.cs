using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// Clipboard adapter for active-app delivery, including common rich-format restoration. The WinRT
/// clipboard only works on the UI thread (elsewhere it throws CO_E_NOTINITIALIZED), and dictation
/// and Quill deliver from background threads, so every call is moved onto <paramref name="dispatcher"/>.
/// </summary>
public sealed class WinRtClipboardAdapter(IUiDispatcher dispatcher) : IClipboardAdapter
{
    private Task<T> OnUiThread<T>(Func<Task<T>> work)
    {
        if (dispatcher.HasThreadAccess) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
            {
                try { done.SetResult(await work()); }
                catch (Exception exception) { done.SetException(exception); }
            }))
            done.SetException(new InvalidOperationException("The clipboard is unavailable while Muesli is closing."));
        return done.Task;
    }

    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken) => OnUiThread(() => CaptureOnUiAsync(cancellationToken));

    public Task SetTextAsync(string text, CancellationToken cancellationToken) => OnUiThread(() => SetTextOnUi(text, cancellationToken));

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken) => OnUiThread(() => ReadTextOnUiAsync(cancellationToken));

    public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken) => OnUiThread(() => RestoreOnUi(snapshot, cancellationToken));

    private static async Task<ClipboardSnapshot> CaptureOnUiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = Clipboard.GetContent();
        var value = new SnapshotValue(
            view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null,
            view.Contains(StandardDataFormats.Html) ? await view.GetHtmlFormatAsync() : null,
            view.Contains(StandardDataFormats.Rtf) ? await view.GetRtfAsync() : null,
            view.Contains(StandardDataFormats.Bitmap) ? await view.GetBitmapAsync() : null,
            view.Contains(StandardDataFormats.StorageItems) ? await view.GetStorageItemsAsync() : null);
        return new ClipboardSnapshot(value);
    }

    private static Task<bool> SetTextOnUi(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text ?? string.Empty);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.FromResult(true);
    }

    private static async Task<string?> ReadTextOnUiAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = Clipboard.GetContent();
        return view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null;
    }

    private static Task<bool> RestoreOnUi(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Data is not SnapshotValue value)
        {
            throw new ArgumentException("The clipboard snapshot was created by another adapter.", nameof(snapshot));
        }
        if (value is { Text: null, Html: null, Rtf: null, Bitmap: null } &&
            value.StorageItems is not { Count: > 0 })
        {
            Clipboard.Clear();
            return Task.FromResult(true);
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (value.Text is not null) package.SetText(value.Text);
        if (value.Html is not null) package.SetHtmlFormat(value.Html);
        if (value.Rtf is not null) package.SetRtf(value.Rtf);
        if (value.Bitmap is not null) package.SetBitmap(value.Bitmap);
        if (value.StorageItems is { Count: > 0 }) package.SetStorageItems(value.StorageItems);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.FromResult(true);
    }

    private sealed record SnapshotValue(
        string? Text,
        string? Html,
        string? Rtf,
        RandomAccessStreamReference? Bitmap,
        IReadOnlyList<IStorageItem>? StorageItems);
}
