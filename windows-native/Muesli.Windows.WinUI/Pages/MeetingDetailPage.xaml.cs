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

    /// <summary>
    /// The recording controls and notes while this page shows the meeting being recorded
    /// (macOS compactQuickNotesHeader); null otherwise.
    /// </summary>
    public MeetingsPageViewModel? Recording { get; private set; }

    private string _meetingId = "";
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
            if (args.PropertyName == nameof(ViewModel.Transcript)) SavedTranscriptFeed.Update(null, ViewModel.Transcript);
            if (args.PropertyName == nameof(ViewModel.HasRecording)) RenderVideo();

            if (args.PropertyName == nameof(ViewModel.IsPlaying))
            {
                // Drive the playhead once per rendered frame, only while audio plays.
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingFrame;
                if (ViewModel.IsPlaying) Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRenderingFrame;
            }

            if (args.PropertyName is nameof(ViewModel.ShowNotesTab) or null or "") SyncTabBar();
            if (args.PropertyName is nameof(ViewModel.HasFolder) or null or "") UpdateFolderPill();
        };
        // A folder move or a delete changes what the shell rail's folder rows count. The rail
        // recomputes through the shared builder; this page never counts anything itself.
        ViewModel.LibraryChanged += OnLibraryChanged;
        ViewModel.MeetingRemoved += OnMeetingRemoved;
        ViewModel.RecordingStarted += OnRecordingStarted;
        App.Meetings.Changed += OnMeetingRuntimeChanged;
        App.ScreenRecording.Changed += OnScreenRecordingChanged;
        Loaded += (_, _) =>
        {
            UpdateScreenStatus();
            RenderVideo();
            ApplyResponsiveLayout(ActualWidth);
            SyncTabBar();
            UpdateFolderPill();
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        Unloaded += (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRenderingFrame;
            ViewModel.LibraryChanged -= OnLibraryChanged;
            ViewModel.MeetingRemoved -= OnMeetingRemoved;
            ViewModel.RecordingStarted -= OnRecordingStarted;
            App.Meetings.Changed -= OnMeetingRuntimeChanged;
            App.ScreenRecording.Changed -= OnScreenRecordingChanged;
            StopVideo();
            EndRecordingMode(reload: false);
            ViewModel.Dispose();
        };
    }

    public Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public bool Not(bool value) => !value;

    /// <summary>The progress bar is determinate once an operation reports a fraction.</summary>
    public bool IsIndeterminate(bool working, double progress) => working && progress <= 0;

    /// <summary>The raw-transcript call to action belongs to the Notes tab.</summary>
    public Visibility CtaVisibility(bool show, bool notesTab) => BoolToVisibility(show && notesTab);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _meetingId = e.Parameter as string ?? "";
        MeetingChat.EmbeddedMeetingId = _meetingId;
        ViewModel.Load(_meetingId);
        _citationTarget = App.MeetingChatDocumentTarget is { } target && target.Citation.MeetingId == _meetingId ? target : null;
        if (_citationTarget is not null) ViewModel.ShowNotesTab = !_citationTarget.ShowsTranscript;
        ShowCitationTarget();
        SyncTabBar();
        UpdateMode();
        ShowPeople(MeetingParticipants.Load(App.Library.Profile.RootDirectory, _meetingId));
        RenderVideo();
    }

    /// <summary>
    /// Recording this meeting, or finishing it: Stop clears the active id before the notes are
    /// generated, so the page stays on "Finalizing notes" until the runtime is idle.
    /// </summary>
    private bool IsRecordingThisMeeting =>
        (_meetingId.Length > 0 && _meetingId == App.Meetings.ActiveMeetingId
            && (App.Meetings.IsRecording || App.Meetings.IsPaused || App.Meetings.IsBusy))
        || (Recording is not null && App.Meetings.IsBusy);

    private void OnMeetingRuntimeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateMode);

    private void OnScreenRecordingChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateScreenStatus();
        RenderVideo();
    });

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

    /// <summary>The always-visible screen capture disclosure, plus the last screen-recording message.</summary>
    private void UpdateScreenStatus()
    {
        var screen = App.ScreenRecording;
        var text = screen.RecordingMeetingId == _meetingId && _meetingId.Length > 0
            ? (screen.IsFinalizing ? "Saving screen recording…"
                : screen.PauseState.IsPaused ? $"Screen recording paused · {screen.SourceName}"
                : $"Recording screen · {screen.SourceName}")
            : screen.IsPreparing ? "Preparing screen recording…" : "";
        if (screen.Message is { } message && (screen.MessageMeetingId is null || screen.MessageMeetingId == _meetingId))
            text = text.Length > 0 ? $"{text}\n{message}" : message;
        ScreenStatus.Text = text;
        ScreenStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenGlyph.Foreground = screen.IsRecording && !screen.PauseState.IsPaused
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xEF, 0x44, 0x44)) : null;
    }

    private void Screen_Click(object sender, RoutedEventArgs e)
    {
        var screen = App.ScreenRecording;
        var meetings = App.Meetings;
        var flyout = new MenuFlyout();
        if (screen.IsRecording)
        {
            var pause = new MenuFlyoutItem { Text = screen.PauseState.UserPaused ? "Resume Screen Recording" : "Pause Screen Recording",
                IsEnabled = !screen.IsFinalizing && !screen.IsPreparing };
            pause.Click += (_, _) => screen.ToggleScreenPause();
            var stop = new MenuFlyoutItem { Text = "Stop Screen Recording", IsEnabled = !screen.IsFinalizing };
            stop.Click += async (_, _) => await screen.StopAsync();
            flyout.Items.Add(pause);
            flyout.Items.Add(stop);
        }
        else
        {
            var record = new MenuFlyoutItem
            {
                Text = screen.IsPreparing ? "Preparing Screen Recording…" : "Record Screen…",
                IsEnabled = screen.IsAvailable && meetings.IsRecording && !meetings.IsPaused && !meetings.IsBusy && !screen.IsPreparing && !screen.IsFinalizing
            };
            record.Click += async (_, _) => await RecordScreenAsync();
            flyout.Items.Add(record);
        }
        flyout.ShowAt((FrameworkElement)sender);
    }

    /// <summary>macOS MuesliController.startMeetingScreenRecording: picker, confirm, then capture.</summary>
    private async Task RecordScreenAsync()
    {
        var screen = App.ScreenRecording;
        var meetings = App.Meetings;
        if (meetings.ActiveMeetingId is not { } meetingId || meetings.IsPaused) return;
        var picked = await Controls.MeetingScreenSourcePicker.PickAsync(XamlRoot, Controls.MeetingScreenSourcePicker.Sources());
        if (picked is not { } choice || meetings.ActiveMeetingId != meetingId || !meetings.IsRecording || meetings.IsPaused) return;
        if (choice.Source.CreateItem() is not { } item)
        {
            ScreenStatus.Text = "That window or display is no longer available. Try Record Screen again.";
            ScreenStatus.Visibility = Visibility.Visible;
            return;
        }
        try { meetings.EnsureDraftSaved(); }
        catch (Exception error)
        {
            ScreenStatus.Text = $"Screen recording could not start: {error.Message}";
            ScreenStatus.Visibility = Visibility.Visible;
            return;
        }
        await screen.StartAsync(meetingId, meetings.CaptureSessionId, meetings.StartedAt, item, $"{choice.Source.Title} — {choice.Source.Subtitle}");
        // The optional announcement never gates capture or its retention.
        if (choice.Announce && screen.RecordingMeetingId == meetingId) await screen.PlayAnnouncementAsync();
    }

    /// <summary>
    /// The meeting being recorded shows its live notes and recording controls; once it stops,
    /// the page reloads the saved meeting in the standard document view.
    /// </summary>
    private void UpdateMode()
    {
        if (IsRecordingThisMeeting && Recording is null)
        {
            Recording = new MeetingsPageViewModel(App.Library, App.Meetings, App.MeetingDetection, App.FilePickers,
                App.Dialogs, App.UiDispatcher, App.ShowLiveTranscript, App.Clipboard);
            Recording.PropertyChanged += OnRecordingPropertyChanged;
            Bindings.Update();
            RecordingModeBar.SelectedItem = RecordingNotesTab;
            UpdateLiveFeed();
        }
        else if (!IsRecordingThisMeeting && Recording is not null)
        {
            EndRecordingMode(reload: true);
        }

        var recording = Recording is not null;
        RecordingView.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        StandardView.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        MeetingChat.Visibility = !recording && ViewModel.HasMeeting && App.MeetingChat is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EndRecordingMode(bool reload)
    {
        if (Recording is null) return;
        Recording.PropertyChanged -= OnRecordingPropertyChanged;
        Recording.Dispose();
        Recording = null;
        if (!reload) return;
        Bindings.Update();
        ViewModel.Load(_meetingId);
        // A discarded recording leaves nothing to show; macOS returns to the browser.
        if (!ViewModel.HasMeeting)
        {
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(MeetingsPage));
            return;
        }
        SyncTabBar();
        UpdateFolderPill();
    }

    private void OnRecordingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (Recording is null) return;
        if (args.PropertyName is nameof(MeetingsPageViewModel.LiveTranscript) or nameof(MeetingsPageViewModel.PartialYou) or nameof(MeetingsPageViewModel.PartialOthers))
            UpdateLiveFeed();
        if (args.PropertyName == nameof(MeetingsPageViewModel.ShowLiveTab))
            RecordingModeBar.SelectedItem = Recording.ShowLiveTab ? RecordingLiveTab : RecordingNotesTab;
    }

    private void RecordingMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (Recording is null) return;
        Recording.ShowLiveTab = sender.SelectedItem == RecordingLiveTab;
        if (Recording.ShowLiveTab) UpdateLiveFeed();
    }

    private void UpdateLiveFeed()
    {
        if (Recording is null) return;
        LiveFeed.Update(App.Meetings.LatestLiveSnapshot, Recording.LiveTranscript, App.Meetings.LiveTranscriptPrefix);
        PreviousNotes.Markdown = App.Meetings.PreviousMeetingNotes;
        PreviousNotes.Visibility = string.IsNullOrWhiteSpace(PreviousNotes.Markdown) ? Visibility.Collapsed : Visibility.Visible;
        LiveCopyButton.IsEnabled = LiveFeed.CopyText.Length > 0;
    }

    private async void CopyLive_Click(object sender, RoutedEventArgs e) =>
        await Controls.LiveTranscriptFeed.CopyAsync(LiveCopyButton, LiveFeed.CopyText);

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // macOS returnToMeetingChat: a cited document returns to the conversation it came from.
        if (_citationTarget is not null) { (App.Window as MainWindow)?.ShellPage?.NavigateTo("askmeetings"); return; }
        if (Frame.CanGoBack) Frame.GoBack();
    }

    /// <summary>Resuming records into this meeting; a follow-up opens the new meeting.</summary>
    private void OnRecordingStarted(object? sender, EventArgs e)
    {
        if (App.Meetings.ActiveMeetingId is { } id && id != _meetingId) Frame.Navigate(typeof(MeetingDetailPage), id);
        else UpdateMode();
    }

    private void RelatedMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && id.Length > 0) Frame.Navigate(typeof(MeetingDetailPage), id);
    }

    private void TitleEditor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        ViewModel.Title = TitleEditor.Text;
    }

    private void FolderPill_Click(object sender, RoutedEventArgs e) =>
        MeetingFolderMenu.Show(FolderPill, ViewModel.FolderId, includeUnfiled: false,
            ViewModel.MoveToFolder, ViewModel.CreateFolderAndMove);

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

    /// <summary>macOS folderPill: folder.fill in accent once filed, folder.badge.plus otherwise.</summary>
    private void UpdateFolderPill()
    {
        FolderPillIcon.Glyph = ViewModel.HasFolder ? "" : "";
        FolderPill.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ViewModel.HasFolder ? "MuesliAccentBrush" : "MuesliTextSecondaryBrush"];
    }

    /// <summary>MAC-04: while recording, choosing a template only saves it for this meeting; notes are written at Stop.</summary>
    private void RecordingTemplateMenu_Opening(object? sender, object e)
    {
        RecordingTemplateMenu.Items.Clear();
        if (Recording is not { } recording) return;
        foreach (var name in recording.RecordingTemplateNames)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = name,
                IsChecked = string.Equals(name, recording.RecordingTemplate, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => recording.SelectRecordingTemplate(name);
            RecordingTemplateMenu.Items.Add(item);
        }
    }

    /// <summary>The Template submenu lists every template with a check on the meeting's, then Manage Templates….</summary>
    private void MoreActionsMenu_Opening(object? sender, object e)
    {
        TemplateMenu.Items.Clear();
        foreach (var name in ViewModel.TemplateNames)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = name,
                IsChecked = string.Equals(name, ViewModel.SelectedTemplate, StringComparison.OrdinalIgnoreCase),
                IsEnabled = ViewModel.CanGenerateSummary
            };
            item.Click += async (_, _) => await ViewModel.ApplyTemplateAsync(name);
            TemplateMenu.Items.Add(item);
        }
        TemplateMenu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Manage Templates…" };
        manage.Click += (_, _) => Frame.Navigate(typeof(MeetingTemplatesPage));
        TemplateMenu.Items.Add(manage);
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

    /// <summary>
    /// Compact is the 720-wide window with the icon rail (~648 DIP of page): the header actions
    /// drop under the title and the speaker-name editor stacks. The tier and the reading cap come
    /// from <see cref="MuesliPageMetrics"/> and <c>MuesliPageContentMaxWidth</c>.
    /// </summary>
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var compact = MuesliPageMetrics.IsCompact(width);
        ContentStack.Padding = MuesliPageMetrics.ContentPadding(width);
        ContentStack.MaxWidth = compact ? double.PositiveInfinity : ContentMaxWidth;
        ContentStack.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        Grid.SetRow(ChromeActions, compact ? 1 : 0);
        Grid.SetColumn(ChromeActions, compact ? 0 : 1);
        Grid.SetColumnSpan(ChromeActions, compact ? 2 : 1);
        ChromeActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Grid.SetRow(RecordingActions, compact ? 1 : 0);
        Grid.SetColumn(RecordingActions, compact ? 0 : 1);
        Grid.SetColumnSpan(RecordingActions, compact ? 2 : 1);
        RecordingActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Grid.SetRow(AliasSourceBox, 0);
        Grid.SetColumn(AliasSourceBox, 0);
        Grid.SetColumnSpan(AliasSourceBox, compact ? 3 : 1);
        Grid.SetRow(AliasReplacementBox, compact ? 1 : 0);
        Grid.SetColumn(AliasReplacementBox, compact ? 0 : 1);
        Grid.SetColumnSpan(AliasReplacementBox, compact ? 2 : 1);
        Grid.SetRow(AddAliasButton, compact ? 1 : 0);
        Grid.SetColumn(AddAliasButton, 2);
        CandidateActions.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
    }

    /// <summary>The shared reading-width cap; the literal is only the fallback if the key is gone.</summary>
    private static double ContentMaxWidth =>
        Application.Current.Resources["MuesliPageContentMaxWidth"] is double value ? value : 1040;
}
