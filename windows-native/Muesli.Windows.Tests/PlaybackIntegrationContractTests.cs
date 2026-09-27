namespace Muesli.Windows.Tests;

public sealed class PlaybackIntegrationContractTests
{
    [Fact]
    public void RuntimePlaybackSelectionUsesAsyncLoadingAndSupersessionCancellation()
    {
        // Active WinUI meeting-detail playback loads a selected track asynchronously with a
        // cancellation token and delegates to the playback service.
        var viewModel = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "ViewModels", "MeetingDetailViewModel.cs"));
        var context = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiMeetingDetailContext.cs"));
        var service = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.Core", "Services", "MeetingRecordingPlaybackService.cs"));

        Assert.Contains("LoadPlaybackTrackAsync(CancellationToken", viewModel, StringComparison.Ordinal);
        Assert.Contains("_runtime.LoadTrackAsync(SelectedPlaybackTrack, cancellationToken)", viewModel, StringComparison.Ordinal);
        Assert.Contains("LoadTrackAsync(MeetingPlaybackTrack track, CancellationToken", context, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();", service, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
