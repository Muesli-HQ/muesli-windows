using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.ViewModels;

namespace Muesli.Windows.WinUI.Pages;

public sealed partial class MeetingDetailPage : Page
{
    public MeetingDetailViewModel ViewModel { get; } = new(
        App.Library,
        App.Settings,
        App.Meetings,
        App.Clipboard,
        App.FilePickers,
        App.Dialogs,
        App.UiDispatcher);

    private MeetingChatDocumentTarget? _citationTarget;

    /// <summary>
    /// macOS MeetingDetailView with appState.meetingChatDocumentTarget: the "Referenced passage" banner,
    /// "Back to Chat", and the cited transcript message highlighted.
    /// </summary>
    private void ShowCitationTarget()
    {
        BackLabel.Text = _citationTarget is null ? "Back to Meetings" : "Back to Chat";
        AutomationProperties.SetName(BackButton, _citationTarget is null ? "Back to meetings" : "Back to chat");
        CitationBanner.Visibility = _citationTarget is null ? Visibility.Collapsed : Visibility.Visible;
        if (_citationTarget is not { } target) return;
        var citation = target.Citation;
        CitationBannerTitle.Text = "Referenced passage · " + citation.Kind.Label();
        CitationBannerExcerpt.Text = citation.Excerpt;
        var text = citation.Kind switch
        {
            MeetingChatSourceKind.Transcript => ViewModel.Transcript,
            MeetingChatSourceKind.ManualNotes => ViewModel.ManualNotes,
            _ => ViewModel.Summary
        };
        var range = target.Locate(text);
        CitationBannerChanged.Visibility = range is null ? Visibility.Visible : Visibility.Collapsed;
        if (range is not null && target.ShowsTranscript)
            SavedTranscriptFeed.Highlight(MeetingChatDocumentTarget.TranscriptMessageIndex(text, range));
    }
    public MeetingDetailPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsStatusError)
                or nameof(ViewModel.IsWorking)
                or nameof(ViewModel.IsStatusOpen)
                or null
                or "")
            {
                UpdateStatusSeverity();
            }

            if (args.PropertyName == nameof(ViewModel.Transcript)) SavedTranscriptFeed.Update(null, ViewModel.Transcript);
            if (args.PropertyName == nameof(ViewModel.HasRecording)) RenderVideo();

            if (args.PropertyName == nameof(ViewModel.IsPlaying))
            {
                // Drive the playhead once per rendered frame, only while audio plays.
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingFrame;
                if (ViewModel.IsPlaying) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingFrame;
            }

            if (args.PropertyName is nameof(ViewModel.ShowNotesTab) or null or "")
            {
                SyncTabBar();
            }
        };
        // A folder move or a delete changes what the shell rail's folder rows count. The rail
        // recomputes through the shared builder; this page never counts anything itself.
        ViewModel.LibraryChanged += OnLibraryChanged;
        // Deleting removes the whole page chrome, and with it the control that had focus, so
        // WinUI fell back to the first focusable element in the window — the sidebar's collapse
        // toggle. Put focus on the one action the page still offers instead.
        ViewModel.MeetingRemoved += OnMeetingRemoved;
        ViewModel.RecordingStarted += OnRecordingStarted;
        App.ScreenRecording.Changed += OnScreenRecordingChanged;
        Loaded += (_, _) =>
        {
            RenderVideo();
            ApplyResponsiveLayout(ActualWidth);
            SyncTabBar();
            UpdateStatusSeverity();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        Unloaded += (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingFrame;
            ViewModel.LibraryChanged -= OnLibraryChanged;
            ViewModel.MeetingRemoved -= OnMeetingRemoved;
            ViewModel.RecordingStarted -= OnRecordingStarted;
            App.ScreenRecording.Changed -= OnScreenRecordingChanged;
            StopVideo();
            ViewModel.Dispose();
        };
    }

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The same rule, reachable from a DataTemplate where <c>ViewModel</c> is not in scope.</summary>
    public static Visibility StaticBoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private string _meetingId = "";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string id && id == App.Meetings.ActiveMeetingId)
        {
            DispatcherQueue.TryEnqueue(() => Frame.Navigate(typeof(MeetingRecordingPage)));
            return;
        }
        _meetingId = e.Parameter as string ?? "";
        ViewModel.Load(_meetingId);
        ShowPeople(MeetingParticipants.Load(App.Library.Profile.RootDirectory, _meetingId));
        MeetingChat.EmbeddedMeetingId = _meetingId;
        MeetingChat.Visibility = ViewModel.HasMeeting && App.MeetingChat is not null ? Visibility.Visible : Visibility.Collapsed;
        _citationTarget = App.MeetingChatDocumentTarget is { } target && target.Citation.MeetingId == _meetingId ? target : null;
        if (_citationTarget is not null) ViewModel.ShowNotesTab = !_citationTarget.ShowsTranscript;
        ShowCitationTarget();
        RenderVideo();
        SyncTabBar();
        UpdateStatusSeverity();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // macOS returnToMeetingChat: a cited document returns to the conversation it came from.
        if (_citationTarget is not null) { (App.Window as MainWindow)?.ShellPage?.NavigateTo("askmeetings"); return; }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private void OnRecordingStarted(object? sender, EventArgs e) => Frame.Navigate(typeof(MeetingRecordingPage));
    private void RelatedMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) Frame.Navigate(typeof(MeetingDetailPage), id);
    }

    private void OnLibraryChanged(object? sender, EventArgs e) =>
        (App.Window as MainWindow)?.ShellPage?.RefreshMeetingFolders();

    private void OnMeetingRemoved(object? sender, EventArgs e) =>
        BackButton.Focus(FocusState.Programmatic);

    private void OnRenderingFrame(object? sender, object e) => ViewModel.RefreshPlaybackPosition();

    private void Waveform_Seeked(object? sender, double fraction) =>
        ViewModel.SeekTo(fraction * ViewModel.PlaybackDurationSeconds);

    private void DetailTabBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.ShowNotesTab = sender.SelectedItem == NotesTabItem;
    }

    private void SyncTabBar()
    {
        var desired = ViewModel.ShowNotesTab ? NotesTabItem : TranscriptTabItem;
        if (DetailTabBar.SelectedItem != desired)
        {
            DetailTabBar.SelectedItem = desired;
        }
    }

    private void UpdateStatusSeverity()
    {
        StatusBar.Severity = ViewModel.IsWorking
            ? InfoBarSeverity.Informational
            : ViewModel.IsStatusError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Success;
    }

    /// <summary>
    /// Compact is the 720-wide window with the icon rail (~648 DIP of page). Action rows stack
    /// instead of clipping. Medium follows the 1008 window; large is 1280 with the expanded rail.
    /// The tier comes from <see cref="MuesliPageMetrics"/> rather than a private 760 threshold,
    /// and the reading cap is the shared <c>MuesliPageContentMaxWidth</c> rather than a local 980
    /// — the two things P1-04 froze.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var compact = MuesliPageMetrics.IsCompact(width);
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);
        ContentStack.MaxWidth = compact ? double.PositiveInfinity : ContentMaxWidth;
        ContentStack.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        HeaderCommands.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
        HeaderCommands.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        ChromeActions.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        DetailTabWell.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        MoreActionsLabel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        if (compact)
        {
            Grid.SetRow(ChromeActions, 1);
            Grid.SetColumn(ChromeActions, 0);
            Grid.SetColumnSpan(ChromeActions, 2);
            foreach (var button in new[] { SaveButton, CopyTranscriptButton, ExportButton, MoreActionsButton })
            {
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
            }

            Grid.SetRow(SummaryTemplatePicker, 1);
            Grid.SetColumn(SummaryTemplatePicker, 0);
            Grid.SetColumnSpan(SummaryTemplatePicker, 2);
            SummaryTemplatePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
            SummaryTemplatePicker.MaxWidth = double.PositiveInfinity;
            Grid.SetRow(GenerateNotesButton, 1);
            Grid.SetColumn(GenerateNotesButton, 2);
            Grid.SetColumnSpan(GenerateNotesButton, 1);
            GenerateNotesButton.HorizontalAlignment = HorizontalAlignment.Stretch;

            Grid.SetRow(SaveTranscriptButton, 1);
            Grid.SetColumn(SaveTranscriptButton, 0);
            Grid.SetColumnSpan(SaveTranscriptButton, 1);
            SaveTranscriptButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetRow(RetranscribeButton, 1);
            Grid.SetColumn(RetranscribeButton, 1);
            Grid.SetColumnSpan(RetranscribeButton, 2);
            RetranscribeButton.HorizontalAlignment = HorizontalAlignment.Stretch;

            Grid.SetRow(AliasSourceBox, 0);
            Grid.SetColumn(AliasSourceBox, 0);
            Grid.SetColumnSpan(AliasSourceBox, 3);
            Grid.SetRow(AliasReplacementBox, 1);
            Grid.SetColumn(AliasReplacementBox, 0);
            Grid.SetColumnSpan(AliasReplacementBox, 2);
            Grid.SetRow(AddAliasButton, 1);
            Grid.SetColumn(AddAliasButton, 2);
            Grid.SetColumnSpan(AddAliasButton, 1);

            FolderPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
            FolderPicker.MaxWidth = double.PositiveInfinity;
            CandidateActions.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            Grid.SetRow(ChromeActions, 0);
            Grid.SetColumn(ChromeActions, 1);
            Grid.SetColumnSpan(ChromeActions, 1);
            foreach (var button in new[] { SaveButton, CopyTranscriptButton, ExportButton, MoreActionsButton })
            {
                button.HorizontalAlignment = HorizontalAlignment.Left;
            }

            Grid.SetRow(SummaryTemplatePicker, 0);
            Grid.SetColumn(SummaryTemplatePicker, 1);
            Grid.SetColumnSpan(SummaryTemplatePicker, 1);
            SummaryTemplatePicker.HorizontalAlignment = HorizontalAlignment.Left;
            SummaryTemplatePicker.MaxWidth = 260;
            Grid.SetRow(GenerateNotesButton, 0);
            Grid.SetColumn(GenerateNotesButton, 2);
            Grid.SetColumnSpan(GenerateNotesButton, 1);
            GenerateNotesButton.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(SaveTranscriptButton, 0);
            Grid.SetColumn(SaveTranscriptButton, 1);
            Grid.SetColumnSpan(SaveTranscriptButton, 1);
            SaveTranscriptButton.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetRow(RetranscribeButton, 0);
            Grid.SetColumn(RetranscribeButton, 2);
            Grid.SetColumnSpan(RetranscribeButton, 1);
            RetranscribeButton.HorizontalAlignment = HorizontalAlignment.Left;

            Grid.SetRow(AliasSourceBox, 0);
            Grid.SetColumn(AliasSourceBox, 0);
            Grid.SetColumnSpan(AliasSourceBox, 1);
            Grid.SetRow(AliasReplacementBox, 0);
            Grid.SetColumn(AliasReplacementBox, 1);
            Grid.SetColumnSpan(AliasReplacementBox, 1);
            Grid.SetRow(AddAliasButton, 0);
            Grid.SetColumn(AddAliasButton, 2);
            Grid.SetColumnSpan(AddAliasButton, 1);

            FolderPicker.HorizontalAlignment = HorizontalAlignment.Left;
            FolderPicker.MaxWidth = 280;
            CandidateActions.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    /// <summary>The shared reading-width cap; the literal is only the fallback if the key is gone.</summary>
    private static double ContentMaxWidth =>
        Application.Current.Resources["MuesliPageContentMaxWidth"] is double value ? value : 1040;

    public static Visibility EmailVisibility(string? email, string displayName) =>
        BoolToVisibilityStatic(!string.IsNullOrEmpty(email) && email != displayName);

    public static string RemoveLabel(string displayName) => $"Remove {displayName}";

    private static Visibility BoolToVisibilityStatic(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>macOS MeetingParticipantsView label, count, and popover list.</summary>
    private void ShowPeople(IReadOnlyList<MeetingParticipant> people)
    {
        var description = people.Count == 1 ? "1 person" : $"{people.Count} people";
        PeopleLabel.Text = people.Count == 0 ? "Add people" : people[0].DisplayName;
        PeopleMore.Text = $"+{people.Count - 1}";
        PeopleMore.Visibility = BoolToVisibilityStatic(people.Count > 1);
        var help = people.Count == 0 ? "Add people to this meeting" : $"Show {description}";
        ToolTipService.SetToolTip(PeopleButton, help);
        AutomationProperties.SetName(PeopleButton, people.Count == 0 ? help : $"{description} in this meeting");
        PeopleCount.Text = people.Count == 0 ? "" : description;
        PeopleEmpty.Visibility = BoolToVisibilityStatic(people.Count == 0);
        PeopleList.ItemsSource = people;
    }

    private void AddPerson_Click(object sender, RoutedEventArgs e)
    {
        AddPersonForm.Visibility = Visibility.Visible;
        PersonNameBox.Focus(FocusState.Programmatic);
    }

    private void CancelPerson_Click(object sender, RoutedEventArgs e) => ClosePersonForm();

    private void PeopleFlyout_Closed(object? sender, object e) => ClosePersonForm();

    private void ClosePersonForm()
    {
        AddPersonForm.Visibility = Visibility.Collapsed;
        PersonNameBox.Text = PersonEmailBox.Text = "";
    }

    private void PersonBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        SavePerson_Click(sender, e);
    }

    private void SavePerson_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PersonNameBox.Text) && string.IsNullOrWhiteSpace(PersonEmailBox.Text))
        {
            PersonNameBox.Focus(FocusState.Programmatic);
            return;
        }
        UpdatePeople(() => MeetingParticipants.Add(App.Library.Profile.RootDirectory, _meetingId, PersonNameBox.Text, PersonEmailBox.Text));
        ClosePersonForm();
    }

    private void RemovePerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            UpdatePeople(() => MeetingParticipants.Remove(App.Library.Profile.RootDirectory, _meetingId, id));
    }

    private async void UpdatePeople(Func<IReadOnlyList<MeetingParticipant>> change)
    {
        try { ShowPeople(change()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PeopleFlyout.Hide();
            await App.Dialogs.ShowWarningAsync(exception.Message, "Couldn't Update People");
        }
    }

    private void OnScreenRecordingChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RenderVideo);


    private string _videoMode = "Video";
    private string? _selectedClipId;
    private string _renderedVideo = "";
    private MediaPlayerElement? _videoPlayer;

    /// <summary>Port of macOS MeetingMediaView: a clip picker ("Part n · time"), one player, and a
    /// Video/Audio switch when the meeting also kept a separate audio recording.</summary>
    private void RenderVideo()
    {
        var clips = _meetingId.Length > 0 ? App.ScreenRecording.Clips(_meetingId) : [];
        var hasAudio = ViewModel.HasRecording;
        var selected = clips.FirstOrDefault(clip => clip.Id == _selectedClipId) ?? clips.FirstOrDefault();
        var showVideo = selected is not null && (_videoMode == "Video" || !hasAudio);
        var key = $"{string.Join(',', clips.Select(clip => clip.Id))}|{hasAudio}|{_videoMode}|{selected?.Id}";
        AudioHost.Visibility = showVideo ? Visibility.Collapsed : Visibility.Visible;
        if (key == _renderedVideo) return;
        _renderedVideo = key;
        StopVideo();
        VideoPanel.Children.Clear();
        VideoPanel.Visibility = clips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (clips.Count == 0) return;
        if (hasAudio)
        {
            var media = new Microsoft.UI.Xaml.Controls.SelectorBar();
            foreach (var mode in new[] { "Video", "Audio" })
                media.Items.Add(new SelectorBarItem { Text = mode, IsSelected = mode == _videoMode });
            media.SelectionChanged += (bar, _) => { _videoMode = bar.SelectedItem?.Text ?? "Video"; RenderVideo(); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(media, "Media");
            VideoPanel.Children.Add(media);
        }
        if (!showVideo || selected is null) return;
        if (clips.Count > 1)
        {
            var parts = new ComboBox { MinWidth = 200 };
            for (var index = 0; index < clips.Count; index++)
                parts.Items.Add(new ComboBoxItem { Content = $"Part {index + 1} · {clips[index].CapturedAt.ToLocalTime():t}", Tag = clips[index].Id });
            parts.SelectedIndex = clips.ToList().FindIndex(clip => clip.Id == selected.Id);
            parts.SelectionChanged += (_, _) => { _selectedClipId = (parts.SelectedItem as ComboBoxItem)?.Tag as string; RenderVideo(); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(parts, "Recording");
            VideoPanel.Children.Add(parts);
        }
        if (App.ScreenRecording.MediaPath(selected) is not { } path)
        {
            VideoPanel.Children.Add(new TextBlock { Text = "Screen recording unavailable", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MuesliTextSecondaryBrush"] });
            return;
        }
        _videoPlayer = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MinHeight = 220, MaxHeight = 400,
            Source = global::Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path))
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_videoPlayer, "MeetingVideoPlayer");
        VideoPanel.Children.Add(_videoPlayer);
    }

    private void StopVideo()
    {
        if (_videoPlayer is null) return;
        _videoPlayer.MediaPlayer?.Pause();
        _videoPlayer.Source = null;
        _videoPlayer = null;
    }
}
