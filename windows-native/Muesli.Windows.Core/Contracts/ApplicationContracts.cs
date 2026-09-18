namespace Muesli.Windows.Core.Contracts;

public enum AppDialogChoice
{
    None,
    Primary,
    Secondary,
    Cancel
}

public interface IAppDialogService
{
    Task ShowInfoAsync(string message, string title, CancellationToken cancellationToken = default);
    Task ShowWarningAsync(string message, string title, CancellationToken cancellationToken = default);
    Task<AppDialogChoice> ConfirmAsync(string message, string title, CancellationToken cancellationToken = default);

    Task<AppDialogChoice> ConfirmAsync(
        string message,
        string title,
        string primaryLabel,
        string cancelLabel,
        CancellationToken cancellationToken = default) =>
        ConfirmAsync(message, title, cancellationToken);
}

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action callback);
    Task EnqueueAsync(Action callback, CancellationToken cancellationToken = default);
}

public sealed record FilePickerRequest(
    string Title,
    IReadOnlyList<string> Extensions,
    string? SuggestedFileName = null);

public sealed record PickedFile(string Path, string DisplayName);

public interface IFilePickerService
{
    Task<PickedFile?> PickOpenFileAsync(FilePickerRequest request, CancellationToken cancellationToken = default);
    Task<PickedFile?> PickSaveFileAsync(FilePickerRequest request, CancellationToken cancellationToken = default);
    Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default);
}

public interface IClipboardSnapshot : IAsyncDisposable
{
}

public interface IClipboardService
{
    Task<IClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);
    Task SetImageFileAsync(string path, CancellationToken cancellationToken = default);
    Task RestoreAsync(IClipboardSnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IShareService
{
    Task ShareFileAsync(string path, string title, string description, CancellationToken cancellationToken = default);
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public sealed record HotkeyGesture(int VirtualKey, HotkeyModifiers Modifiers, string DisplayName);

public interface IWindowCoordinator
{
    void ShowDashboard();
    void HideDashboard();
    void ShowLiveTranscript();
    void HideLiveTranscript();
}

public interface IStartupRegistrationService
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface ITrayService : IDisposable
{
    void SetStatus(string status);
}

public interface IAppActivationService
{
    Task RedirectToPrimaryAsync(CancellationToken cancellationToken = default);
}
