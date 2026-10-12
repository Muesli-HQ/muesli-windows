using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Core.Insights;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

public partial class AboutPageViewModel(
    WinUiLibraryContext library,
    WinUiSettingsContext settings,
    IClipboardService clipboard,
    IFilePickerService filePickers,
    IAppDialogService dialogs) : ObservableObject
{
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly RuntimeDiagnosticsService _diagnostics = new();
    private readonly AppLogService _log = new();
    private readonly SupportBundleService _supportBundle = new();
    private readonly SignedUpdateService _updates = new(UpdateHttp);

    [ObservableProperty] public partial string Version { get; private set; } = "";
    [ObservableProperty] public partial string AssemblyVersion { get; private set; } = "";
    [ObservableProperty] public partial string RuntimeVersion { get; private set; } = "";
    [ObservableProperty] public partial string VersionTooltip { get; private set; } = "";
    [ObservableProperty] public partial string ProfilePath { get; private set; } = library.Profile.RootDirectory;
    /// <summary>The completed 1,000-word milestone the share links post; null hides them.</summary>
    [ObservableProperty] public partial int? ShareWordCount { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsStatusOpen { get; set; }
    [ObservableProperty] public partial bool IsStatusError { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    public bool CanShare => ShareWordCount is not null;
    [ObservableProperty] public partial string UpdateStatus { get; private set; } =
        "Updates are manual. Release notes are the update channel.";

    public void Load()
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
        ShareWordCount = ContributionShare.CompletedWordMilestone(library.ReadSnapshot().DictationWords);

        var update = settings.Load();
        UpdateStatus = string.IsNullOrWhiteSpace(update.UpdateManifestUrl) ||
                       string.IsNullOrWhiteSpace(update.UpdatePublisherPublicKeySha256)
            ? "Updates are manual. Release notes are the update channel."
            : "An automatic update channel is configured. Use Check for updates.";
    }

    partial void OnShareWordCountChanged(int? value) => OnPropertyChanged(nameof(CanShare));

    /// <summary>macOS openContributionSocialAction: LinkedIn drops prefilled text, so the message goes to the clipboard too.</summary>
    public async Task ShareAsync(bool linkedIn)
    {
        if (ShareWordCount is not { } words) return;
        if (linkedIn)
        {
            try { await clipboard.SetTextAsync(ContributionShare.Message(words), CancellationToken.None); }
            catch (Exception) { } // A busy clipboard only loses the convenience copy.
        }
        await global::Windows.System.Launcher.LaunchUriAsync(
            linkedIn ? ContributionShare.LinkedInUrl(words) : ContributionShare.TweetUrl(words));
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        Directory.CreateDirectory(ProfilePath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ProfilePath, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var current = settings.Load();
            var result = await _updates.CheckAsync(
                AssemblyVersion,
                current.UpdateManifestUrl,
                current.UpdateSignatureUrl,
                current.UpdatePublisherPublicKeySha256,
                cancellationToken);
            UpdateStatus = result.Message ?? result.Availability.ToString();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            UpdateStatus = $"The update check could not complete: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportSupportBundleAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;
            var current = settings.Load();
            var runtime = await _diagnostics.InspectAsync(
                current.EnableLocalCleanup,
                current.DictationModelId,
                current.FinalMeetingModelId,
                current.LiveMeetingModelId);
            var request = new SupportBundleRequest(
                new SupportBundleEnvironment(
                    Version,
                    RuntimeVersion,
                    RuntimeInformation.OSDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    DescribePackageIdentity()),
                WindowsSupportBundleFacts.CollectAudioDevices(),
                new SupportBundleSettingsSummary(
                    current.Theme,
                    current.DictationModelId,
                    current.FinalMeetingModelId,
                    current.LiveMeetingModelId,
                    current.EnableLocalCleanup,
                    current.MeetingSummaryProvider,
                    current.CrashReportingEnabled,
                    !string.IsNullOrWhiteSpace(current.ResolvedOpenAIApiKey),
                    !string.IsNullOrWhiteSpace(current.ResolvedOpenRouterApiKey),
                    !string.IsNullOrWhiteSpace(current.OllamaEndpoint)),
                runtime);
            var bundle = _supportBundle.Build(request, _log.LogDirectory, DateTimeOffset.Now);

            var choice = await dialogs.ShowPreviewAsync(
                bundle.Text,
                "Support bundle preview",
                "Save bundle…",
                "Cancel",
                cancellationToken);
            if (choice != AppDialogChoice.Primary)
            {
                return;
            }

            var target = await filePickers.PickSaveFileAsync(
                new FilePickerRequest(
                    "Save support bundle",
                    [".txt"],
                    $"muesli-support-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),
                cancellationToken);
            if (target is null)
            {
                return;
            }

            await File.WriteAllTextAsync(target.Path, bundle.Text, cancellationToken);
            IsStatusError = false;
            StatusMessage = $"Support bundle saved to {target.Path}.";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            IsStatusError = true;
            StatusMessage = $"Could not export support bundle: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        IsStatusOpen = true;
    }

    private static string DescribePackageIdentity()
    {
        try
        {
            var identity = global::Windows.ApplicationModel.Package.Current.Id;
            return $"{identity.Name} {identity.Version} ({identity.Architecture})";
        }
        catch
        {
            return "Unpackaged (no package identity)";
        }
    }

}
