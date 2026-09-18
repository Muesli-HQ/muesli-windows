using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class AboutPageViewModel(
    WinUiLibraryContext library,
    WinUiSettingsContext settings,
    IClipboardService clipboard,
    Action resumeSetup,
    Action replayTour) : ObservableObject
{
    private readonly RuntimeDiagnosticsService _diagnostics = new();
    private readonly AppLogService _log = new();

    [ObservableProperty] public partial string Version { get; private set; } = "";
    [ObservableProperty] public partial string AssemblyVersion { get; private set; } = "";
    [ObservableProperty] public partial string RuntimeVersion { get; private set; } = "";
    [ObservableProperty] public partial string VersionTooltip { get; private set; } = "";
    [ObservableProperty] public partial string ProfilePath { get; private set; } = library.Profile.RootDirectory;
    [ObservableProperty] public partial string LogDirectory { get; private set; } = library.Profile.LogDirectory;
    [ObservableProperty] public partial string DataDirectory { get; private set; } = library.Profile.DataDirectory;
    [ObservableProperty] public partial string SettingsFilePath { get; private set; } = library.Profile.SettingsPath;
    [ObservableProperty] public partial string ModelCachePath { get; private set; } = "";
    [ObservableProperty] public partial string RuntimeSummary { get; private set; } = "Loading diagnostics…";
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    public async Task LoadAsync()
    {
        var assembly = typeof(App).Assembly;
        var version = assembly.GetName().Version;
        Version = version is { } parsed ? $"v{parsed.ToString(3)}" : "Version unavailable";
        AssemblyVersion = version?.ToString() ?? "unavailable";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(informational) &&
            !string.Equals(informational, AssemblyVersion, StringComparison.Ordinal))
        {
            VersionTooltip = $"Product {informational} · Assembly {AssemblyVersion}";
        }
        else
        {
            VersionTooltip = $"Assembly {AssemblyVersion}";
        }

        RuntimeVersion = RuntimeInformation.FrameworkDescription;
        ProfilePath = library.Profile.RootDirectory;
        LogDirectory = library.Profile.LogDirectory;
        DataDirectory = library.Profile.DataDirectory;
        SettingsFilePath = library.Profile.SettingsPath;
        ModelCachePath = _diagnostics.ModelCacheDirectory;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            var current = settings.Load();
            var result = await _diagnostics.InspectAsync(
                current.EnableLocalCleanup,
                current.DictationModelId,
                current.FinalMeetingModelId,
                current.LiveMeetingModelId);
            RuntimeSummary = result.Summary;
            ModelCachePath = result.ModelCacheDirectory;
        }
        catch (Exception exception)
        {
            RuntimeSummary = $"Diagnostics could not be completed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLogs() => _log.OpenLogDirectory();

    [RelayCommand]
    private void OpenModelCache() => _diagnostics.OpenModelCacheDirectory();

    [RelayCommand]
    private async Task CopyDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // Prompt 9 CR-01: Clipboard.SetContent throws COMException 0x800401D0 while another
        // process holds the clipboard open. Uncaught, AsyncRelayCommand rethrows it on the UI
        // context and App.UnhandledException (App.xaml.cs:89-90) lets the shell terminate.
        try
        {
            await clipboard.SetTextAsync(RuntimeSummary, cancellationToken);
            IsStatusError = false;
            StatusMessage = "Diagnostics copied to the clipboard.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            StatusMessage = $"Could not copy diagnostics: {WinUiClipboardService.DescribeFailure(exception)}";
        }
        IsStatusOpen = true;
    }

    [RelayCommand]
    private void ResumeSetup() => resumeSetup();

    [RelayCommand]
    private void ReplayFeatureTour() => replayTour();
}
