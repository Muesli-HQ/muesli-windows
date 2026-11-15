using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

/// <summary>
/// The in-progress meeting's own page. macOS opens the meeting document when a recording starts
/// instead of drawing it inside the Meetings list; this is that document while it is recording.
/// When the recording ends the page hands over to the saved meeting, or returns to Meetings if
/// nothing was kept.
/// </summary>
public sealed partial class MeetingRecordingPage : Page
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public MeetingRecordingViewModel ViewModel { get; } = new(
        App.Library,
        App.Meetings,
        App.Dialogs,
        App.UiDispatcher,
        App.ShowLiveTranscript);

    public MeetingRecordingPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.LiveTranscript) or nameof(ViewModel.PartialYou) or nameof(ViewModel.PartialOthers))
                UpdateLiveFeed();
            if (args.PropertyName == nameof(ViewModel.ShowLiveTab))
                RecordingModeBar.SelectedItem = ViewModel.ShowLiveTab ? RecordingLiveTab : RecordingNotesTab;
            if (args.PropertyName is nameof(ViewModel.StateLabel) or nameof(ViewModel.StartedAt)) UpdateClock();
        };
        ViewModel.Finished += OnFinished;
        _clock.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(ActualWidth);
            UpdateLiveFeed();
            UpdateClock();
            _clock.Start();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            ViewModel.Finished -= OnFinished;
            ViewModel.Dispose();
        };
    }

    /// <summary>Shared gutters; below the compact/medium band the controls drop under the title.</summary>
    private void ApplyResponsiveLayout(double width)
    {
        ContentGrid.Padding = MuesliPageMetrics.ContentPadding(width);
        var compact = MuesliPageMetrics.IsCompact(width) || MuesliPageMetrics.IsMedium(width);
        Grid.SetRow(RecordingControls, compact ? 1 : 0);
        Grid.SetColumn(RecordingControls, compact ? 0 : 1);
        RecordingControls.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Nothing is recording (the app was reopened on a stale route): there is no page to show.
        if (!ViewModel.IsActive) DispatcherQueue.TryEnqueue(() => Leave(null));
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // Leaving the page never stops the recording; the Meetings banner and the pill lead back.
        if (Frame.CanGoBack && Frame.BackStack[^1].SourcePageType == typeof(MeetingsPage)) Frame.GoBack();
        else Frame.Navigate(typeof(MeetingsPage));
    }

    private void OnFinished(object? sender, string? meetingId) => Leave(meetingId);

    /// <summary>Replaces this page with the saved meeting, or with the Meetings list.</summary>
    private void Leave(string? meetingId)
    {
        if (Frame is null || !ReferenceEquals(Frame.Content, this)) return;
        if (meetingId is null) Frame.Navigate(typeof(MeetingsPage));
        else Frame.Navigate(typeof(MeetingDetailPage), meetingId);
        // Back from the saved meeting should reach the list, not a recording that is over.
        for (var index = Frame.BackStack.Count - 1; index >= 0; index--)
        {
            if (Frame.BackStack[index].SourcePageType == typeof(MeetingRecordingPage)) Frame.BackStack.RemoveAt(index);
        }
        if (meetingId is not null && (Frame.BackStack.Count == 0 || Frame.BackStack[^1].SourcePageType != typeof(MeetingsPage)))
        {
            Frame.BackStack.Add(new PageStackEntry(typeof(MeetingsPage), "meetings", null));
        }
    }

    private void RecordingMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.ShowLiveTab = sender.SelectedItem == RecordingLiveTab;
        if (ViewModel.ShowLiveTab) UpdateLiveFeed();
    }

    private void UpdateLiveFeed()
    {
        LiveFeed.Update(App.Meetings.LatestLiveSnapshot, ViewModel.LiveTranscript, App.Meetings.LiveTranscriptPrefix);
        PreviousNotes.Markdown = App.Meetings.PreviousMeetingNotes;
        PreviousNotes.Visibility = string.IsNullOrWhiteSpace(PreviousNotes.Markdown) ? Visibility.Collapsed : Visibility.Visible;
        LiveCopyButton.IsEnabled = LiveFeed.CopyText.Length > 0;
    }

    private async void CopyLive_Click(object sender, RoutedEventArgs e) =>
        await Controls.LiveTranscriptFeed.CopyAsync(LiveCopyButton, LiveFeed.CopyText);

    /// <summary>macOS shows a running timer while recording and the state word otherwise.</summary>
    private void UpdateClock()
    {
        var started = ViewModel.StartedAt;
        StartedLabel.Text = started == default ? "" : started.ToString("MMM d, yyyy 'at' h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
        ElapsedLabel.Text = ViewModel.IsRecording && started != default
            ? MeetingRecordingClock.Format(DateTime.Now - started)
            : ViewModel.StateLabel;
    }
}

internal static class MeetingRecordingClock
{
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
    }
}
