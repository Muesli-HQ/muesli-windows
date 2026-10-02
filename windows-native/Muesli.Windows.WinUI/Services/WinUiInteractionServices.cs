using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Foundation;
using System.Runtime.InteropServices;

namespace Muesli.Windows.WinUI.Services;

public sealed class WinUiDispatcher(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    public bool HasThreadAccess => dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action callback) => dispatcherQueue.TryEnqueue(() => callback());

    public Task EnqueueAsync(Action callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (HasThreadAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcherQueue.TryEnqueue(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    callback();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The WinUI dispatcher is shutting down."));
        }

        return completion.Task;
    }
}

public sealed class WinUiDialogService(Func<FrameworkElement?> xamlRootOwner) : IAppDialogService
{
    public Task ShowInfoAsync(string message, string title, CancellationToken cancellationToken = default) =>
        ShowMessageAsync(message, title, cancellationToken);

    public Task ShowWarningAsync(string message, string title, CancellationToken cancellationToken = default) =>
        ShowMessageAsync(message, title, cancellationToken);

    public async Task<AppDialogChoice> ConfirmAsync(
        string message,
        string title,
        CancellationToken cancellationToken = default) =>
        await ConfirmAsync(message, title, "Delete", "Cancel", cancellationToken);

    public async Task<AppDialogChoice> ConfirmAsync(
        string message,
        string title,
        string primaryLabel,
        string cancelLabel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = CreateDialog(message, title);
        dialog.PrimaryButtonText = string.IsNullOrWhiteSpace(primaryLabel) ? "OK" : primaryLabel;
        dialog.CloseButtonText = string.IsNullOrWhiteSpace(cancelLabel) ? "Cancel" : cancelLabel;
        dialog.DefaultButton = ContentDialogButton.Close;
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? AppDialogChoice.Primary
            : AppDialogChoice.Cancel;
    }

    public async Task<AppDialogChoice> ShowPreviewAsync(
        string message,
        string title,
        string primaryLabel,
        string cancelLabel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = xamlRootOwner()
            ?? throw new InvalidOperationException("The application window is not ready to show a dialog.");
        var body = new Border
        {
            MinWidth = 520,
            MaxHeight = 420,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    FontSize = 12,
                    LineHeight = 18
                }
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = string.IsNullOrWhiteSpace(primaryLabel) ? "Save" : primaryLabel,
            CloseButtonText = string.IsNullOrWhiteSpace(cancelLabel) ? "Cancel" : cancelLabel,
            DefaultButton = ContentDialogButton.Close
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, title);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? AppDialogChoice.Primary
            : AppDialogChoice.Cancel;
    }

    private async Task ShowMessageAsync(string message, string title, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = CreateDialog(message, title);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    public async Task<AppDialogChoice> ChooseAsync(string message, string title, string primaryLabel,
        string secondaryLabel, string cancelLabel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = CreateDialog(message, title);
        dialog.PrimaryButtonText = primaryLabel;
        dialog.SecondaryButtonText = secondaryLabel;
        dialog.CloseButtonText = cancelLabel;
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => AppDialogChoice.Primary,
            ContentDialogResult.Secondary => AppDialogChoice.Secondary,
            _ => AppDialogChoice.Cancel
        };
    }

    private ContentDialog CreateDialog(string message, string title)
    {
        var owner = xamlRootOwner()
            ?? throw new InvalidOperationException("The application window is not ready to show a dialog.");
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dialog, title);
        return dialog;
    }
}

public sealed class WinUiFilePickerService(Func<nint> windowHandle) : IFilePickerService
{
    public async Task<PickedFile?> PickOpenFileAsync(
        FilePickerRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        foreach (var extension in NormalizeExtensions(request.Extensions))
        {
            picker.FileTypeFilter.Add(extension);
        }

        var file = await picker.PickSingleFileAsync();
        return file is null ? null : new PickedFile(file.Path, file.DisplayName);
    }

    public async Task<PickedFile?> PickSaveFileAsync(
        FilePickerRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extensions = NormalizeExtensions(request.Extensions);
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        foreach (var extension in extensions)
            picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant() + " file", [extension]);
        if (!string.IsNullOrWhiteSpace(request.SuggestedFileName))
        {
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(request.SuggestedFileName);
        }

        var file = await picker.PickSaveFileAsync();
        return file is null ? null : new PickedFile(file.Path, file.DisplayName);
    }

    public async Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private static List<string> NormalizeExtensions(IReadOnlyList<string> extensions)
    {
        var normalized = extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(extension => extension == "*" || extension.StartsWith('.') ? extension : $".{extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return normalized.Count == 0 ? ["*"] : normalized;
    }
}

public sealed class WinUiClipboardService : IClipboardService
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    /// <summary>
    /// User-facing reason for a failed clipboard write. The WinRT projection of
    /// CLIPBRD_E_CANT_OPEN carries an empty Message, so the raw text would read "Could not copy: ".
    /// </summary>
    public static string DescribeFailure(Exception exception) =>
        exception.HResult == ClipboardCannotOpen
            ? "another app is using the clipboard. Try again."
            : string.IsNullOrWhiteSpace(exception.Message)
                ? $"clipboard error 0x{exception.HResult:X8}."
                : exception.Message;

    public async Task<IClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var view = Clipboard.GetContent();
        var text = view.Contains(StandardDataFormats.Text)
            ? await view.GetTextAsync()
            : null;
        var html = view.Contains(StandardDataFormats.Html)
            ? await view.GetHtmlFormatAsync()
            : null;
        var rtf = view.Contains(StandardDataFormats.Rtf)
            ? await view.GetRtfAsync()
            : null;
        var bitmap = view.Contains(StandardDataFormats.Bitmap)
            ? await view.GetBitmapAsync()
            : null;
        var storageItems = view.Contains(StandardDataFormats.StorageItems)
            ? await view.GetStorageItemsAsync()
            : null;
        return new WinUiClipboardSnapshot(text, html, rtf, bitmap, storageItems);
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text ?? string.Empty);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    public async Task SetImageFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    public Task RestoreAsync(IClipboardSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is not WinUiClipboardSnapshot value)
        {
            throw new ArgumentException("The clipboard snapshot was not created by WinUI.", nameof(snapshot));
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (value.Text is not null) package.SetText(value.Text);
        if (value.Html is not null) package.SetHtmlFormat(value.Html);
        if (value.Rtf is not null) package.SetRtf(value.Rtf);
        if (value.Bitmap is not null) package.SetBitmap(value.Bitmap);
        if (value.StorageItems is { Count: > 0 }) package.SetStorageItems(value.StorageItems);
        if (value is { Text: null, Html: null, Rtf: null, Bitmap: null } &&
            value.StorageItems is not { Count: > 0 })
        {
            Clipboard.Clear();
            return Task.CompletedTask;
        }
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    private sealed record WinUiClipboardSnapshot(
        string? Text,
        string? Html,
        string? Rtf,
        RandomAccessStreamReference? Bitmap,
        IReadOnlyList<IStorageItem>? StorageItems) : IClipboardSnapshot
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class WinUiShareService(Func<nint> windowHandle) : IShareService
{
    private static readonly Guid DataTransferManagerIid = new(
        0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    public async Task ShareFileAsync(
        string path,
        string title,
        string description,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        IDataTransferManagerInterop interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var abi = interop.GetForWindow(windowHandle(), DataTransferManagerIid);
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi);
        TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
        handler = (sender, args) =>
        {
            sender.DataRequested -= handler;
            args.Request.Data.Properties.Title = title;
            args.Request.Data.Properties.Description = description;
            args.Request.Data.SetStorageItems([file]);
        };
        manager.DataRequested += handler;
        interop.ShowShareUIForWindow(windowHandle());
    }

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
