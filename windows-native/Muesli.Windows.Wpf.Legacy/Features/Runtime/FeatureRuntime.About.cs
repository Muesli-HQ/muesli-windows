using System.Diagnostics;
using System.IO;
using System.Windows;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void OpenLogs_Click(object sender, RoutedEventArgs e)
{
    try
    {
        _logService.OpenLogDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open logs: {ConciseUiError(exception)}";
    }
}
private void CleanupCaptures_Click(object sender, RoutedEventArgs e)
{
    var inventory = _captureStorageService.InspectLegacyAndTransientCaptures();
    if (inventory.FileCount == 0)
    {
        System.Windows.MessageBox.Show(
            "No legacy or interrupted capture files were found. Saved meetings, imports, transcripts, settings, models, and last-dictation.wav were not inspected for deletion.",
            "Audio storage",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return;
    }

    var confirmed = System.Windows.MessageBox.Show(
        $"Muesli found {inventory.FileCount} legacy or interrupted capture file(s), totaling {FormatByteCount(inventory.TotalBytes)}.\n\nDelete these files? Saved meeting recordings, imported media, transcripts, settings, models, and last-dictation.wav are excluded.",
        "Clean legacy captures",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
    if (confirmed != MessageBoxResult.Yes)
    {
        return;
    }

    var result = _captureStorageService.DeleteLegacyAndTransientCaptures(inventory);
    DictationStatus = result.FailedPaths.Count == 0
        ? $"Deleted {result.DeletedCount} legacy capture file(s)"
        : $"Deleted {result.DeletedCount}; {result.FailedPaths.Count} could not be removed";
    System.Windows.MessageBox.Show(
        result.FailedPaths.Count == 0
            ? $"Deleted {result.DeletedCount} legacy or interrupted capture file(s)."
            : $"Deleted {result.DeletedCount} file(s). {result.FailedPaths.Count} file(s) were busy or inaccessible and were left in place.",
        "Audio storage",
        MessageBoxButton.OK,
        result.FailedPaths.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
}

private void OpenPrivacy_Click(object sender, RoutedEventArgs e)
{
    var privacyPath = System.IO.Path.Combine(AppContext.BaseDirectory, "WINDOWS-PRIVACY.md");
    if (!System.IO.File.Exists(privacyPath))
    {
        DictationStatus = "Privacy document is missing from this build";
        return;
    }
    Process.Start(new ProcessStartInfo { FileName = privacyPath, UseShellExecute = true });
}

private static string FormatByteCount(long bytes) => bytes switch
{
    >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
    >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
    >= 1024 => $"{bytes / 1024.0:0.0} KB",
    _ => $"{bytes} B"
};
private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
{
    DictationStatus = "Opening release page";
    OpenExternalUrl("https://github.com/Muesli-HQ/Muesli-Windows/releases", "Could not open release page");
}
private void Donate_Click(object sender, RoutedEventArgs e)
{
    OpenExternalUrl("https://buymeacoffee.com/phequals7", "Could not open donation link");
}
private void ViewGitHub_Click(object sender, RoutedEventArgs e)
{
    OpenExternalUrl("https://github.com/Muesli-HQ/Muesli-Windows", "Could not open GitHub");
}
private void OpenExternalUrl(string url, string failurePrefix)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch (Exception exception)
    {
        DictationStatus = $"{failurePrefix}: {exception.Message}";
        _logService.Error(failurePrefix, exception);
    }
}
}
