using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Windows.System;
using Windows.UI.Core;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>
/// macOS MeetingChatView (2f10b23d): the Ask Meetings page and, with <see cref="EmbeddedMeetingId"/>, the
/// panel in a meeting document. State lives in the shared <see cref="MeetingChatCoordinator"/>; this view
/// only renders it and forwards intent.
/// </summary>
public sealed partial class MeetingChatView : UserControl
{
    private const string CitationScheme = "muesli-citation:///";
    private const string ProviderPrompt = "Connect your meeting AI provider in AI Settings. Your question is saved here.";
    private MeetingChatCoordinator? _chat;
    private readonly DispatcherTimer _waitingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(TextBlock Label, string Phase)> _waitingLabels = [];
    private readonly HashSet<string> _expanded = [];
    private IReadOnlyList<string> _phrases = MeetingChatWaitingStatus.Phrases;
    private DateTimeOffset _phaseStartedAt = DateTimeOffset.Now;
    private string _phase = "";
    private string _turnsSignature = "";
    private Guid? _renderedSession;
    private bool _syncingComposer;
    private bool _preparedIsDraft;
    private int _mentionIndex;
    private MeetingChatMentionQuery? _dismissedMention;

    public MeetingChatView()
    {
        InitializeComponent();
        _waitingTimer.Tick += (_, _) => UpdateWaitingLabels();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // macOS frame(maxWidth: 752).frame(maxWidth: .infinity): a centered reading column.
        IndexView.SizeChanged += (_, e) => IndexContent.Width = Math.Min(752, e.NewSize.Width);
        ConversationView.SizeChanged += (_, e) => TurnsHost.Width = Math.Min(752, e.NewSize.Width);
        foreach (var action in MeetingChatQuickActions.All)
        {
            var chip = new Button
            {
                Content = action.Label(), FontSize = 12, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0),
                Background = (Brush)Application.Current.Resources["MuesliBackgroundRaisedBrush"]
            };
            chip.Click += async (_, _) => await PrepareAsync(action, App.Settings.Load().UserName);
            SuggestionsPanel.Children.Add(chip);
            var item = new MenuFlyoutItem { Text = action.Label() };
            item.Click += async (_, _) => await PrepareAsync(action, App.Settings.Load().UserName);
            PrepareMenu.Items.Add(item);
        }
    }

    /// <summary>The meeting whose document hosts this panel; null for the Ask Meetings page. Set before load.</summary>
    public string? EmbeddedMeetingId { get; set; }

    private bool IsEmbedded => EmbeddedMeetingId is not null;
    private bool _expandedPanel;
    private bool _wasLocked;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _chat = App.MeetingChat;
        if (_chat is null)
        {
            // Ask Meetings reads the SQLite library; a JSON-only profile fails closed instead of guessing.
            IndexView.Visibility = Visibility.Visible;
            ErrorText.Text = "Ask Meetings needs the upgraded meeting library. Restart Muesli to finish the library upgrade.";
            ErrorText.Visibility = Visibility.Visible;
            MoveComposer(IndexComposerSlot);
            Composer.IsHitTestVisible = false;
            return;
        }
        _chat.Changed += OnChatChanged;
        App.Settings.Changed += OnSettingsChanged;
        _wasLocked = Locked;
        // The shared coordinator refreshes on mutations; browsing notes must not reload history again.
        if (EmbeddedMeetingId is { } id) _chat.PrepareMeetingDraft(id);
        else _chat.RefreshForSourceChanges();
        Render();
        await _chat.RefreshChoicesAsync();
        if (!IsEmbedded) ComposerBox.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _waitingTimer.Stop();
        App.Settings.Changed -= OnSettingsChanged;
        if (_chat is not null) _chat.Changed -= OnChatChanged;
    }

    private void OnChatChanged()
    {
        if (DispatcherQueue.HasThreadAccess) ChatChanged();
        else DispatcherQueue.TryEnqueue(ChatChanged);
    }

    private void ChatChanged()
    {
        // macOS onChange(isBusy || isUpdatingChat): an empty draft is prepared again once the chat goes idle.
        var locked = Locked;
        if (_wasLocked && !locked && EmbeddedMeetingId is { } id) _chat?.PrepareMeetingDraft(id);
        _wasLocked = locked;
        Render();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expandedPanel == expanded) return;
        _expandedPanel = expanded;
        _turnsSignature = "";
        Render();
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        SetExpanded(true);
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) => SetExpanded(false);

    private void OpenFull_Click(object sender, RoutedEventArgs e) => (App.Window as MainWindow)?.ShellPage?.NavigateTo("askmeetings");

    private void ComposerBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (IsEmbedded) SetExpanded(true);
    }

    /// <summary>macOS MuesliController.showMeetingChatSource.</summary>
    private void OpenSource(MeetingChatCoordinator chat, MeetingChatCitation citation)
    {
        if (chat.SelectedSessionId is not { } sessionId) return;
        if (App.Library.FindMeeting(citation.MeetingId) is null) { chat.ErrorMessage = "This source meeting is no longer available."; return; }
        (App.Window as MainWindow)?.ShellPage?.ShowMeetingDocument(citation.MeetingId, new MeetingChatDocumentTarget(citation, sessionId));
    }

    private void OnSettingsChanged(object? sender, MuesliSettings settings) => DispatcherQueue.TryEnqueue(RenderModel);

    private bool Locked => _chat is null || _chat.IsBusy || _chat.IsUpdatingChat;

    private void Render()
    {
        if (_chat is not { } chat) return;
        var conversation = chat.Turns.Count > 0;
        var open = !IsEmbedded || _expandedPanel;
        Header.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BackGroup.Visibility = !IsEmbedded && chat.SelectedSessionId is not null ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = chat.SelectedSession?.Title ?? "Ask Meetings";
        TitleText.Visibility = IsEmbedded ? Visibility.Collapsed : Visibility.Visible;
        EmbeddedTitle.Visibility = OpenFullButton.Visibility = CollapseButton.Visibility = IsEmbedded ? Visibility.Visible : Visibility.Collapsed;
        EmbeddedSubtitle.Text = chat.SelectedSession?.Title ?? "";
        EmbeddedSubtitle.Visibility = chat.SelectedSession is null ? Visibility.Collapsed : Visibility.Visible;
        NewChatButton.IsEnabled = !Locked;
        IndexView.Visibility = !IsEmbedded && !conversation ? Visibility.Visible : Visibility.Collapsed;
        ConversationView.Visibility = conversation && open ? Visibility.Visible : Visibility.Collapsed;
        ConversationRow.Height = IsEmbedded ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ConversationView.Height = IsEmbedded ? 260 : double.NaN;
        MoveComposer(IsEmbedded || conversation ? BottomComposerSlot : IndexComposerSlot);
        Composer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RestingComposer.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        // macOS embedded + expanded: a framed deep surface with a 20pt radius, inset 16 x 12.
        var framed = IsEmbedded && _expandedPanel;
        Surface.CornerRadius = new CornerRadius(framed ? 20 : 0);
        Surface.BorderThickness = new Thickness(framed ? 1 : 0);
        Surface.Margin = framed ? new Thickness(16, 12, 16, 12) : new Thickness(0);
        Surface.Background = (Brush)Application.Current.Resources[framed ? "MuesliBackgroundDeepBrush" : "MuesliBackgroundBrush"];
        HeaderDivider.Visibility = framed ? Visibility.Visible : Visibility.Collapsed;
        if (!open) RenderResting(chat);
        if (!IsEmbedded && !conversation) RenderIndex(chat);
        RenderComposer(chat);
        RenderTurns(chat);
    }

    /// <summary>macOS restingComposer: the collapsed embedded chat.</summary>
    private void RenderResting(MeetingChatCoordinator chat)
    {
        var ownMeeting = chat.Scope.Selection.Kind == MeetingChatSelectionKind.Meetings
            && chat.Scope.Selection.Ids.SequenceEqual(EmbeddedMeetingId is { } id ? [id] : []);
        RestingText.Text = chat.ComposerDraft.Length > 0 ? chat.ComposerDraft
            : chat.Turns.Count > 0 ? "Continue conversation…"
            : ownMeeting ? "Ask about this meeting…" : "Ask your meetings…";
        ToolTipService.SetToolTip(RestingComposer, "Chat context: " + ScopeLabel(chat));
    }

    private void MoveComposer(Border slot)
    {
        if (slot.Child == Composer) return;
        IndexComposerSlot.Child = BottomComposerSlot.Child = null;
        slot.Child = Composer;
    }

    private void RenderIndex(MeetingChatCoordinator chat)
    {
        foreach (var chip in SuggestionsPanel.Children.OfType<Button>()) chip.IsEnabled = !Locked;
        RecentHeader.Visibility = chat.Sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Children.Clear();
        foreach (var session in chat.Sessions)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 13, Foreground = Secondary });
            var title = new TextBlock { Text = session.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            var date = new TextBlock { Text = MeetingChatFormat.MonthDay(session.UpdatedAt), FontSize = 12, Foreground = Tertiary };
            Grid.SetColumn(date, 2);
            row.Children.Add(date);
            var button = PlainButton(row);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(0);
            AutomationName(button, session.Title);
            var id = session.Id;
            button.Click += (_, _) => chat.SelectChat(id);
            RecentList.Children.Add(button);
        }
    }

    // ── Composer ────────────────────────────────────────────────────────────────────────────────

    private void RenderComposer(MeetingChatCoordinator chat)
    {
        ErrorText.Text = chat.ErrorMessage ?? "";
        ErrorText.Visibility = chat.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        SavingText.Visibility = chat.IsBusy && chat.ActiveTurnId is null ? Visibility.Visible : Visibility.Collapsed;
        SavingText.Text = chat.Phase.Length == 0 ? "Saving question" : chat.Phase;

        if (ComposerBox.Text != chat.ComposerDraft)
        {
            _syncingComposer = true;
            var caret = ComposerBox.SelectionStart;
            ComposerBox.Text = chat.ComposerDraft;
            ComposerBox.SelectionStart = Math.Min(caret, ComposerBox.Text.Length);
            _syncingComposer = false;
        }
        ComposerBox.PlaceholderText = chat.Turns.Count == 0 ? "Ask a question, or @ to attach a meeting…" : "Ask a follow-up, or @ to add context…";

        var attached = AttachedIds(chat);
        AttachmentScroll.Visibility = attached is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        AttachmentList.Children.Clear();
        foreach (var id in attached ?? [])
        {
            var source = chat.SourceChoices.FirstOrDefault(item => item.Id == id);
            var title = source?.Title ?? "Unavailable meeting";
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            chip.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
            chip.Children.Add(new TextBlock { Text = title, FontSize = 12, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
            chip.Children.Add(new FontIcon { Glyph = "", FontSize = 10 });
            var button = PlainButton(chip);
            button.IsEnabled = !Locked;
            button.Background = (Brush)Application.Current.Resources["MuesliBackgroundBrush"];
            ToolTipService.SetToolTip(button, $"Remove {source?.Title ?? "meeting"} from context");
            AutomationName(button, $"Remove {title} from context");
            var sessionId = chat.SelectedSessionId;
            button.Click += async (_, _) =>
            {
                if (chat.SelectedSessionId != sessionId) return;
                await chat.RemoveMeetingFromScopeAsync(id);
            };
            AttachmentList.Children.Add(button);
        }

        var scopeLabel = ScopeLabel(chat);
        ScopeText.Text = scopeLabel;
        ScopeText.Visibility = attached is null ? Visibility.Visible : Visibility.Collapsed;
        var dates = MeetingChatScopePresentation.DateLabel(chat.Scope);
        DateText.Text = dates ?? "";
        DateText.Visibility = dates is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(ScopeButton, "Meeting context: " + scopeLabel);
        ScopeButton.IsEnabled = MentionButton.IsEnabled = ModelButton.IsEnabled = PrepareButton.IsEnabled = !Locked;
        PrepareButton.Visibility = chat.Turns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = chat.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = chat.IsBusy ? Visibility.Collapsed : Visibility.Visible;
        var canSend = !chat.IsUpdatingChat && chat.ComposerDraft.Trim().Length > 0;
        SendButton.IsEnabled = canSend;
        SendButton.Opacity = canSend ? 1 : 0.4;
        RenderModel();
        RenderMention(chat);
    }

    private void RenderModel()
    {
        var settings = App.Settings.Load();
        var provider = settings.MeetingSummaryProvider.Trim().ToLowerInvariant();
        var model = MeetingSummaryModels.Model(settings);
        // macOS thinkingLabel: shown for the ChatGPT and OpenAI providers when the model has levels.
        var thinking = provider is "chatgpt" or "openai" ? ReasoningEffortPolicy.Resolved(model, settings.MeetingSummaryReasoningEffort) : null;
        ModelText.Text = (model.Length > 0 ? model : MeetingChatCoordinator.ProviderLabel(provider))
            + (thinking is null ? "" : " · " + ReasoningEffortPolicy.Label(thinking));
    }

    private static IReadOnlyList<string>? AttachedIds(MeetingChatCoordinator chat) =>
        chat.Scope.Selection.Kind == MeetingChatSelectionKind.Meetings ? chat.Scope.Selection.Ids : null;

    private static string ScopeLabel(MeetingChatCoordinator chat)
    {
        var folders = App.Library.History.LoadMeetingFolders();
        return MeetingChatScopePresentation.ScopeLabel(chat.Scope, id => folders.FirstOrDefault(item => item.Id == id)?.Name, chat.SourceChoices);
    }

    private void ComposerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingComposer || _chat is null) return;
        _dismissedMention = null;
        _chat.ComposerDraft = ComposerBox.Text;
    }

    private void ComposerBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_chat is not null && !_syncingComposer) RenderMention(_chat);
    }

    private MeetingChatMentionQuery? CurrentMention()
    {
        if (_chat is null || Locked) return null;
        var mention = MeetingChatMentionQuery.Find(ComposerBox.Text, ComposerBox.SelectionStart, ComposerBox.SelectionLength);
        return mention is null || mention == _dismissedMention ? null : mention;
    }

    private List<MeetingChatSourceChoice> MentionMatches(MeetingChatMentionQuery mention) =>
        _chat!.SourceChoices.Where(item => mention.Query.Length == 0 || item.Title.Contains(mention.Query, StringComparison.CurrentCultureIgnoreCase)).ToList();

    private MeetingChatMentionQuery? _renderedMention;

    private void RenderMention(MeetingChatCoordinator chat)
    {
        var mention = CurrentMention();
        if (mention != _renderedMention) _mentionIndex = 0;
        _renderedMention = mention;
        MentionPanel.Visibility = mention is null ? Visibility.Collapsed : Visibility.Visible;
        if (mention is null) return;
        var matches = MentionMatches(mention);
        MentionEmpty.Text = $"No saved meetings match “{mention.Query}”.";
        MentionEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MentionScroll.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        MentionNote.Visibility = AttachedIds(chat) is null ? Visibility.Visible : Visibility.Collapsed;
        var attached = AttachedIds(chat) ?? [];
        MentionList.Children.Clear();
        for (var index = 0; index < matches.Count; index++)
        {
            var meeting = matches[index];
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Secondary });
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock { Text = meeting.Title, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = MeetingChatFormat.AbbreviatedDate(meeting.StartDate), FontSize = 12, Foreground = Secondary });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (attached.Contains(meeting.Id))
            {
                var check = new FontIcon { Glyph = "", FontSize = 12, Foreground = Secondary };
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }
            var button = PlainButton(row);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Padding = new Thickness(12, 9, 12, 9);
            button.CornerRadius = new CornerRadius(0);
            if (index == _mentionIndex) button.Background = (Brush)Application.Current.Resources["MuesliSelectedBrush"];
            AutomationName(button, $"Attach {meeting.Title}, {MeetingChatFormat.AbbreviatedDate(meeting.StartDate)}");
            button.Click += (_, _) => AttachMeeting(meeting, mention);
            MentionList.Children.Add(button);
        }
        if (_mentionIndex < MentionList.Children.Count) MentionList.Children[_mentionIndex].StartBringIntoView();
    }

    private async void AttachMeeting(MeetingChatSourceChoice meeting, MeetingChatMentionQuery mention)
    {
        if (_chat is not { } chat) return;
        var original = chat.ComposerDraft;
        var sessionId = chat.SelectedSessionId;
        if (chat.SelectedSessionId != sessionId) return;
        if (!await chat.AddMeetingToScopeAsync(meeting.Id)) return;
        if (chat.ComposerDraft == original)
        {
            chat.ComposerDraft = mention.RemovingFrom(original);
            ComposerBox.SelectionStart = Math.Min(mention.Start, ComposerBox.Text.Length);
        }
        _dismissedMention = null;
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void Mention_Click(object sender, RoutedEventArgs e)
    {
        if (_chat is not { } chat) return;
        var text = chat.ComposerDraft;
        var location = Math.Clamp(ComposerBox.SelectionStart, 0, text.Length);
        var trigger = location == 0 || char.IsWhiteSpace(text[location - 1]) ? "@" : " @";
        chat.ComposerDraft = text.Insert(location, trigger);
        ComposerBox.SelectionStart = location + trigger.Length;
        _dismissedMention = null;
        ComposerBox.Focus(FocusState.Programmatic);
        RenderMention(chat);
    }

    private void ComposerBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_chat is not { } chat) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (CurrentMention() is { } mention)
        {
            var matches = MentionMatches(mention);
            switch (e.Key)
            {
                case VirtualKey.Up: _mentionIndex = Math.Max(0, _mentionIndex - 1); break;
                case VirtualKey.Down: _mentionIndex = Math.Min(Math.Max(0, matches.Count - 1), _mentionIndex + 1); break;
                case VirtualKey.Escape: _dismissedMention = mention; break;
                case VirtualKey.Tab:
                case VirtualKey.Enter when !shift:
                    if (_mentionIndex < matches.Count) AttachMeeting(matches[_mentionIndex], mention);
                    break;
                default: return;
            }
            e.Handled = true;
            _renderedMention = CurrentMention();
            RenderMention(chat);
            return;
        }
        if (e.Key == VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Stop_Click(object sender, RoutedEventArgs e) => _chat?.Stop();

    private async void Send()
    {
        if (_chat is not { } chat || chat.IsBusy || chat.IsUpdatingChat) return;
        if (chat.ComposerDraft.Trim().Length == 0) return;
        if (!CanUseSummaryProvider(App.Settings.Load())) { chat.ErrorMessage = ProviderPrompt; return; }
        var question = chat.ComposerDraft;
        var draft = _preparedIsDraft;
        _preparedIsDraft = false;
        if (IsEmbedded) SetExpanded(true);
        await chat.SendAsync(question, draft);
        ScrollToEnd();
    }

    /// <summary>macOS MuesliController.canUseSummaryProvider for the providers Windows supports.</summary>
    private static bool CanUseSummaryProvider(MuesliSettings settings) => settings.MeetingSummaryProvider.Trim().ToLowerInvariant() switch
    {
        "chatgpt" => App.ComputerUse.ChatGpt.IsSignedIn,
        "openai" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? settings.ResolvedOpenAIApiKey),
        "openrouter" => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? settings.ResolvedOpenRouterApiKey),
        "ollama" => true,
        "lmstudio" => settings.LmStudioModel.Length > 0,
        "custom" => settings.CustomLlmModel.Length > 0 && settings.CustomLlmEndpoint.Length > 0,
        _ => false
    };

    private async Task PrepareAsync(MeetingChatQuickAction action, string? name)
    {
        if (_chat is not { } chat || Locked) return;
        var prompt = action.Prepare(chat.Scope, DateTimeOffset.Now, name);
        if (prompt.NeedsParticipantName)
        {
            var box = new TextBox { PlaceholderText = "Participant name" };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Whose next steps?", Content = box,
                PrimaryButtonText = "Prepare question", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false
            };
            box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await PrepareAsync(action, box.Text);
            return;
        }
        if (!await chat.ApplyPreparedPromptAsync(prompt)) return;
        _preparedIsDraft = prompt.IsDraft;
        ComposerBox.Focus(FocusState.Programmatic);
        ComposerBox.SelectionStart = ComposerBox.Text.Length;
    }

    // ── Header ──────────────────────────────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e) => _chat?.ShowIndex();

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _chat?.StartDraft(EmbeddedMeetingId is { } id ? new MeetingChatScope(MeetingChatSelection.Meetings([id])) : null);
        ComposerBox.Focus(FocusState.Programmatic);
    }

    private void HistoryFlyout_Opening(object sender, object e)
    {
        HistoryList.Children.Clear();
        if (_chat is not { } chat) return;
        foreach (var session in chat.Sessions)
        {
            var selected = chat.SelectedSessionId == session.Id;
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock { Text = session.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Foreground = selected ? Primary : Secondary });
            text.Children.Add(new TextBlock { Text = MeetingChatFormat.MonthDay(session.UpdatedAt), FontSize = 10, Foreground = Tertiary });
            var button = PlainButton(text);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(10, 9, 10, 9);
            if (selected) button.Background = (Brush)Application.Current.Resources["MuesliBackgroundRaisedBrush"];
            AutomationName(button, session.Title);
            var id = session.Id;
            var title = session.Title;
            button.Click += (_, _) => { chat.SelectChat(id); HistoryFlyout.Hide(); SetExpanded(true); };
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename", Icon = new FontIcon { Glyph = "" } };
            rename.Click += async (_, _) => { HistoryFlyout.Hide(); await RenameAsync(id, title); };
            var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
            delete.Click += async (_, _) => { HistoryFlyout.Hide(); await DeleteAsync(id); };
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            button.ContextFlyout = menu;
            HistoryList.Children.Add(button);
        }
    }

    private async Task RenameAsync(Guid id, string title)
    {
        var box = new TextBox { Text = title, PlaceholderText = "Chat title" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Rename chat", Content = box,
            PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0 || _chat is null) return;
        await _chat.RenameChatAsync(id, box.Text);
    }

    private async Task DeleteAsync(Guid id)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Delete this chat?",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || _chat is null) return;
        await _chat.DeleteChatAsync(id);
    }

    // ── Scope and model ─────────────────────────────────────────────────────────────────────────

    /// <summary>macOS MeetingChatScopePicker.</summary>
    private async void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (_chat is not { } chat) return;
        var scope = chat.Scope;
        var folders = App.Library.History.LoadMeetingFolders().ToList();
        var selected = scope.Selection.Kind == MeetingChatSelectionKind.Meetings ? scope.Selection.Ids.ToHashSet() : [];
        var mode = new RadioButtons { MaxColumns = 3 };
        foreach (var label in new[] { "All saved meetings", "Folder", "Specific meetings" }) mode.Items.Add(label);
        mode.SelectedIndex = (int)scope.Selection.Kind;
        var folder = new ComboBox { PlaceholderText = "Choose a folder", ItemsSource = folders.Select(item => item.Name).ToList(), MinWidth = 260 };
        folder.SelectedIndex = folders.FindIndex(item => item.Id == scope.Selection.FolderId);
        var folderPanel = new StackPanel { Spacing = 6 };
        folderPanel.Children.Add(folder);
        folderPanel.Children.Add(new TextBlock { Text = "This folder only; subfolders are excluded.", FontSize = 12, Foreground = Secondary });
        var query = new TextBox { PlaceholderText = "Find meetings by title" };
        var count = new TextBlock { FontSize = 12, Foreground = Secondary };
        var list = new StackPanel();
        var meetingsPanel = new StackPanel { Spacing = 6 };
        meetingsPanel.Children.Add(query);
        meetingsPanel.Children.Add(count);
        meetingsPanel.Children.Add(new ScrollViewer { Height = 240, Content = list });
        var datesEnabled = new CheckBox { Content = "Date range", IsChecked = scope.StartDate is not null || scope.EndDateExclusive is not null };
        var today = new DateTimeOffset(DateTime.Today);
        var start = new CalendarDatePicker { Header = "From", Date = scope.StartDate ?? today.AddDays(-6) };
        var end = new CalendarDatePicker { Header = "Through", Date = scope.EndDateExclusive?.AddSeconds(-1) ?? DateTimeOffset.Now };
        var datePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        datePanel.Children.Add(start);
        datePanel.Children.Add(end);
        var body = new StackPanel { Spacing = 16, Width = 532 };
        body.Children.Add(new TextBlock { Text = "Choose the meetings this conversation can search. Earlier answers keep their original sources.", TextWrapping = TextWrapping.Wrap, Foreground = Secondary });
        body.Children.Add(mode);
        body.Children.Add(folderPanel);
        body.Children.Add(meetingsPanel);
        body.Children.Add(datesEnabled);
        body.Children.Add(datePanel);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Meeting context", Content = new ScrollViewer { Content = body },
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };

        void Refresh()
        {
            folderPanel.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            meetingsPanel.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            datePanel.Visibility = datesEnabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            count.Text = $"{selected.Count} selected";
            dialog.IsPrimaryButtonEnabled = !(mode.SelectedIndex == 1 && folder.SelectedIndex < 0)
                && !(mode.SelectedIndex == 2 && selected.Count == 0)
                && !(datesEnabled.IsChecked == true && end.Date?.Date < start.Date?.Date);
        }
        void Fill()
        {
            list.Children.Clear();
            foreach (var meeting in chat.SourceChoices.Where(item => query.Text.Length == 0 || item.Title.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)))
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = meeting.Title });
                content.Children.Add(new TextBlock { Text = MeetingChatFormat.AbbreviatedDateTime(meeting.StartDate), FontSize = 12, Foreground = Secondary });
                var box = new CheckBox { Content = content, IsChecked = selected.Contains(meeting.Id) };
                box.Checked += (_, _) => { selected.Add(meeting.Id); Refresh(); };
                box.Unchecked += (_, _) => { selected.Remove(meeting.Id); Refresh(); };
                list.Children.Add(box);
            }
        }
        mode.SelectionChanged += (_, _) => Refresh();
        folder.SelectionChanged += (_, _) => Refresh();
        datesEnabled.Checked += (_, _) => Refresh();
        datesEnabled.Unchecked += (_, _) => Refresh();
        start.DateChanged += (_, _) => Refresh();
        end.DateChanged += (_, _) => Refresh();
        query.TextChanged += (_, _) => Fill();
        Fill();
        Refresh();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var selection = mode.SelectedIndex switch
        {
            1 => MeetingChatSelection.Folder(folders[folder.SelectedIndex].Id),
            2 => MeetingChatSelection.Meetings(selected.Order(StringComparer.Ordinal)),
            _ => MeetingChatSelection.All
        };
        DateTimeOffset? Day(DateTimeOffset? date, int add) => date is { } value ? new DateTimeOffset(value.Date.AddDays(add)) : null;
        var dated = datesEnabled.IsChecked == true;
        await chat.SetScopeAsync(new MeetingChatScope(selection, dated ? Day(start.Date, 0) : null, dated ? Day(end.Date, 1) : null));
    }

    /// <summary>macOS MeetingChatModelPicker: provider, model and thinking level shared with meeting summaries.</summary>
    private void Model_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Load();
        var current = settings.MeetingSummaryProvider.Trim().ToLowerInvariant();
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        var panel = new StackPanel { Width = 280, Spacing = 0 };

        var header = new Grid { Padding = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "Model", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var providers = new MenuFlyout();
        foreach (var provider in new[] { "chatgpt", "openai", "openrouter", "ollama", "lmstudio", "custom" })
        {
            if (provider != current && !CanUseSummaryProvider(settings with { MeetingSummaryProvider = provider })) continue;
            var item = new ToggleMenuFlyoutItem { Text = MeetingChatCoordinator.ProviderLabel(provider), IsChecked = provider == current };
            item.Click += (_, _) => { flyout.Hide(); if (provider != current) App.Settings.Save(App.Settings.Load() with { MeetingSummaryProvider = provider }); };
            providers.Items.Add(item);
        }
        providers.Items.Add(new MenuFlyoutSeparator());
        var connect = new MenuFlyoutItem { Text = "Connect a provider…" };
        connect.Click += (_, _) => { flyout.Hide(); OpenSettings(); };
        providers.Items.Add(connect);
        var providerButton = new DropDownButton
        {
            Content = MeetingChatCoordinator.ProviderLabel(current), Flyout = providers, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Resources["ChatPlainButtonStyle"]
        };
        AutomationName(providerButton, "Meeting AI provider");
        header.Children.Add(providerButton);
        panel.Children.Add(header);

        var model = MeetingSummaryModels.Model(settings);
        var presets = SummaryModelPreset.For(current);
        if (presets.Count > 0)
        {
            var choices = presets.Select(item => (item.Id, item.Label)).ToList();
            if (!choices.Any(item => item.Id == model)) choices.Add((model, model));
            var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = choices.Select(item => item.Label).ToList(),
                SelectedIndex = choices.FindIndex(item => item.Id == model), Margin = new Thickness(0, 0, 0, 12) };
            AutomationName(picker, "Meeting AI model");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex < 0 || choices[picker.SelectedIndex].Id == model) return;
                var id = choices[picker.SelectedIndex].Id;
                var latest = App.Settings.Load();
                // macOS presets(): choosing the first preset stores "" so the default can move with releases.
                App.Settings.Save(current == "chatgpt"
                    ? latest with { ChatGptSummaryModel = id == presets[0].Id ? "" : id }
                    : latest with { OpenAIModel = id });
                flyout.Hide();
            };
            panel.Children.Add(picker);
        }
        else panel.Children.Add(new TextBlock { Text = model.Length > 0 ? model : MeetingChatCoordinator.ProviderLabel(current), FontSize = 14, Margin = new Thickness(4, 0, 0, 12) });

        // macOS summary_thinking: only the ChatGPT and OpenAI providers expose thinking levels.
        var efforts = current is "chatgpt" or "openai" ? ReasoningEffortPolicy.SelectableEfforts(model) : [];
        if (efforts.Count > 0)
        {
            var selected = ReasoningEffortPolicy.Resolved(model, settings.MeetingSummaryReasoningEffort) ?? efforts[0];
            var label = new TextBlock { FontSize = 12, Foreground = Secondary };
            var slider = new Slider { Minimum = 0, Maximum = efforts.Count - 1, StepFrequency = 1, SnapsTo = SliderSnapsTo.StepValues,
                TickFrequency = 1, TickPlacement = TickPlacement.Outside, Value = Math.Max(0, efforts.ToList().IndexOf(selected)) };
            AutomationName(slider, "Thinking level");
            void Show() => label.Text = "Thinking · " + ReasoningEffortPolicy.Label(efforts[(int)slider.Value]);
            Show();
            slider.ValueChanged += (_, _) =>
            {
                Show();
                var effort = efforts[(int)slider.Value];
                var latest = App.Settings.Load();
                if (latest.MeetingSummaryReasoningEffort != effort) App.Settings.Save(latest with { MeetingSummaryReasoningEffort = effort });
            };
            panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["MuesliBorderBrush"] });
            var thinking = new StackPanel { Spacing = 4, Padding = new Thickness(0, 12, 0, 12) };
            thinking.Children.Add(label);
            thinking.Children.Add(slider);
            panel.Children.Add(thinking);
        }

        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["MuesliBorderBrush"] });
        var footer = new StackPanel { Spacing = 7, Padding = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "Shared with meeting summaries", FontSize = 10, Foreground = Secondary });
        var aiSettings = PlainButton(IconContent("", "AI Settings…"));
        aiSettings.Click += (_, _) => { flyout.Hide(); OpenSettings(); };
        footer.Children.Add(aiSettings);
        panel.Children.Add(footer);
        flyout.Content = panel;
        flyout.ShowAt(ModelButton);
    }

    private static void OpenSettings() => (App.Window as MainWindow)?.ShellPage?.NavigateTo("settings");

    // ── Turns ───────────────────────────────────────────────────────────────────────────────────

    private void RenderTurns(MeetingChatCoordinator chat)
    {
        var signature = string.Join('|', chat.Turns.Select(turn =>
            $"{turn.Id}:{turn.State}:{turn.OriginalAnswer?.Length}:{turn.EditableDraft?.Length}:{turn.Citations.Count}:{turn.Error}"))
            + $"#{chat.ActiveTurnId}#{chat.ActivityTurnId}#{chat.Phase}#{chat.IsBusy}#{chat.IsUpdatingChat}#{chat.SourceMutationVersion}#"
            + string.Join(',', chat.Activities.Select(item => $"{item.State}{item.Output?.Length}"))
            + $"#{chat.Sessions.Count}#{chat.Scope}#{chat.ReasoningIsStreaming}#{string.Join(',', chat.ReasoningSummaries.Select(item => item.Length))}";
        if (signature == _turnsSignature) return;
        _turnsSignature = signature;
        var sessionChanged = _renderedSession != chat.SelectedSessionId;
        _renderedSession = chat.SelectedSessionId;
        var atEnd = ConversationView.VerticalOffset >= ConversationView.ScrollableHeight - 24;

        _waitingLabels.Clear();
        TurnsHost.Children.Clear();
        for (var index = 0; index < chat.Turns.Count; index++)
        {
            var turn = chat.Turns[index];
            if ((index == 0 && !turn.Scope.Equals(chat.Scope)) || (index > 0 && !chat.Turns[index - 1].Scope.Equals(turn.Scope)))
                TurnsHost.Children.Add(new TextBlock { Text = turn.ScopeLabel ?? "Meeting context changed", FontSize = 12, Foreground = Secondary });
            TurnsHost.Children.Add(TurnView(chat, turn));
        }

        if (chat.Phase != _phase) { _phase = chat.Phase; _phaseStartedAt = DateTimeOffset.Now; _phrases = MeetingChatWaitingStatus.Phrases.OrderBy(_ => Random.Shared.Next()).ToList(); }
        if (_waitingLabels.Count > 0) { UpdateWaitingLabels(); _waitingTimer.Start(); }
        else _waitingTimer.Stop();
        if (sessionChanged || atEnd) ScrollToEnd();
    }

    private void ScrollToEnd() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
        () => ConversationView.ChangeView(null, ConversationView.ScrollableHeight, null, true));

    private void UpdateWaitingLabels()
    {
        foreach (var (label, phase) in _waitingLabels)
            label.Text = MeetingChatWaitingStatus.Label(phase, DateTimeOffset.Now - _phaseStartedAt, _phrases);
    }

    private UIElement TurnView(MeetingChatCoordinator chat, MeetingChatTurn turn)
    {
        var panel = new StackPanel { Spacing = 20 };
        var question = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560, CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 10, 14, 10),
            Background = (Brush)Application.Current.Resources["MuesliBackgroundRaisedBrush"],
            Child = new TextBlock { Text = turn.Question, FontSize = 14, LineHeight = 22, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
        };
        panel.Children.Add(question);

        var answer = new StackPanel { Spacing = 14 };
        var reasoning = turn.Id == chat.ActivityTurnId ? chat.ReasoningSummaries.Where(item => item.Trim().Length > 0).ToList() : [];
        if (turn.IsPending)
        {
            var phase = turn.Id == chat.ActiveTurnId && chat.Phase.Length > 0 ? chat.Phase
                : turn.State == MeetingChatTurnState.Finding ? "Finding meeting context" : "Writing answer";
            // Streaming reasoning already provides its own thinking indicator.
            if (!(chat.ReasoningIsStreaming && reasoning.Count > 0))
            {
                var label = new TextBlock { FontSize = 13, Foreground = Secondary };
                _waitingLabels.Add((label, phase));
                answer.Children.Add(label);
            }
            if (turn.Id == chat.ActiveTurnId && chat.Activities.Count > 0)
                answer.Children.Add(Disclosure($"activity:{turn.Id}", $"{chat.Activities.Count} research steps", "", ActivityRows(chat)));
        }
        if (turn.Id == chat.ActivityTurnId && !turn.IsPending && chat.Activities.Count > 0)
            answer.Children.Add(Disclosure($"activity:{turn.Id}", $"Meeting research · {chat.Activities.Count} steps", "", ActivityRows(chat)));
        if (reasoning.Count > 0)
        {
            // macOS AIReasoning: only provider-visible summaries, transient and never saved.
            var summary = AnswerBlock(chat, string.Join("\n\n", reasoning), []);
            summary.FontSize = 12;
            summary.LineHeight = 18;
            summary.Foreground = Secondary;
            answer.Children.Add(Disclosure($"reasoning:{turn.Id}", chat.ReasoningIsStreaming ? "Thinking…" : "Reasoning", "\uE82F", summary));
        }
        if (turn.OriginalAnswer is { } text)
        {
            answer.Children.Add(AnswerBlock(chat, text, turn.Citations));
            if (turn.Citations.Count > 0) answer.Children.Add(Disclosure($"sources:{turn.Id}", MeetingChatSourceGroup.Label(turn.Citations), "", SourceRows(chat, turn)));
            if (AnswerActions(chat, turn) is { } actions) answer.Children.Add(actions);
        }
        else if (turn.State == MeetingChatTurnState.SourceDeleted)
        {
            answer.Children.Add(IconLabel("", "Answer removed because a source meeting was deleted"));
        }
        else if (!turn.IsPending)
        {
            answer.Children.Add(IconLabel("", turn.Error ?? (turn.State == MeetingChatTurnState.Stopped ? "Request stopped" : "Request interrupted")));
            var retry = PlainButton(IconContent("", "Try again"));
            retry.IsEnabled = !Locked;
            retry.HorizontalAlignment = HorizontalAlignment.Left;
            retry.Click += async (_, _) => await chat.RetryAsync(turn.Id);
            answer.Children.Add(retry);
        }
        panel.Children.Add(answer);
        return panel;
    }

    private static UIElement ActivityRows(MeetingChatCoordinator chat)
    {
        var rows = new StackPanel { Spacing = 10 };
        foreach (var activity in chat.Activities)
        {
            var row = new StackPanel { Spacing = 3 };
            var glyph = activity.State switch
            {
                MeetingChatActivityState.Running => "",
                MeetingChatActivityState.Completed => "",
                MeetingChatActivityState.Failed => "",
                _ => ""
            };
            row.Children.Add(IconLabel(glyph, activity.Title));
            if (activity.Detail.Length > 0) row.Children.Add(Mono(activity.Detail));
            if (activity.Output is { Length: > 0 } output) row.Children.Add(Mono(output));
            if (activity.Error is { Length: > 0 } error) row.Children.Add(new TextBlock { Text = error, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MuesliWarningBrush"] });
            rows.Children.Add(row);
        }
        return rows;
    }

    private static TextBlock Mono(string text) => new()
    {
        Text = text, FontSize = 11, FontFamily = new FontFamily("Cascadia Mono, Consolas"), TextWrapping = TextWrapping.Wrap,
        MaxLines = 6, IsTextSelectionEnabled = true, Foreground = Secondary, Margin = new Thickness(22, 0, 0, 0)
    };

    /// <summary>macOS AIDisclosure: a collapsed row that keeps its open state across re-renders.</summary>
    private UIElement Disclosure(string key, string title, string glyph, UIElement content)
    {
        var expander = new Expander
        {
            Header = IconContent(glyph, title), Content = content, IsExpanded = _expanded.Contains(key),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanding += (_, _) => _expanded.Add(key);
        expander.Collapsed += (_, _) => _expanded.Remove(key);
        return expander;
    }

    /// <summary>macOS MeetingChatAnswerView: the notes Markdown subset, where only source citations stay links.</summary>
    private RichTextBlock AnswerBlock(MeetingChatCoordinator chat, string raw, IReadOnlyList<MeetingChatCitation> citations)
    {
        var markdown = MeetingChatClient.DisplayText(raw);
        for (var index = 0; index < citations.Count; index++)
            markdown = markdown.Replace($"[[{citations[index].SourceKey}]]", $"[{index + 1}]({CitationScheme}{citations[index].SourceKey})", StringComparison.Ordinal);
        var block = new RichTextBlock { FontSize = 15, LineHeight = 24, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        foreach (var source in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = MeetingNotesRichText.ParseLine(source);
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, line.Heading > 0 ? 8 : 4) };
            if (line.Heading > 0) paragraph.FontSize = Math.Max(15, 22 - (line.Heading - 1) * 2);
            foreach (var run in line.Runs)
            {
                var citation = run.Link is { } link && link.StartsWith(CitationScheme, StringComparison.Ordinal)
                    ? citations.FirstOrDefault(item => item.SourceKey == link[CitationScheme.Length..]) : null;
                var text = new Run
                {
                    Text = citation is null ? run.Text : $"[{run.Text}]",
                    FontWeight = run.Bold || line.Heading > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = run.Italic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal
                };
                if (run.Code) text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                if (citation is null) { paragraph.Inlines.Add(text); continue; }
                var hyperlink = new Hyperlink { UnderlineStyle = UnderlineStyle.None, Foreground = (Brush)Application.Current.Resources["MuesliAccentBrush"] };
                hyperlink.Inlines.Add(text);
                ToolTipService.SetToolTip(hyperlink, $"{citation.Title} · {citation.Kind.Label()}");
                hyperlink.Click += (_, _) => ShowCitation(chat, citation, block);
                paragraph.Inlines.Add(hyperlink);
            }
            block.Blocks.Add(paragraph);
        }
        return block;
    }

    /// <summary>macOS MeetingChatSourcesView: passages grouped by meeting, keeping answer numbers.</summary>
    private UIElement SourceRows(MeetingChatCoordinator chat, MeetingChatTurn turn)
    {
        var groups = new StackPanel { Spacing = 12, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var group in MeetingChatSourceGroup.Grouped(turn.Citations))
        {
            var panel = new StackPanel { Spacing = 7 };
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Secondary });
            var title = new TextBlock { Text = group.Title, FontSize = 14, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            var date = new TextBlock { Text = MeetingChatFormat.AbbreviatedDate(group.StartDate), FontSize = 11, Foreground = Secondary };
            Grid.SetColumn(date, 2);
            header.Children.Add(date);
            panel.Children.Add(header);
            foreach (var passage in group.Passages)
            {
                var content = new StackPanel { Spacing = 2 };
                content.Children.Add(new TextBlock { Text = $"[{passage.Number}] {passage.Metadata}", FontSize = 11, Foreground = Secondary });
                content.Children.Add(new TextBlock { Text = passage.Preview, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                var button = PlainButton(content);
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                ToolTipService.SetToolTip(button, $"Read passage {passage.Number} from {group.Title}");
                AutomationName(button, $"Passage {passage.Number}, {passage.Citation.Kind.Label()}{(passage.Citation.Timestamp is { } t ? ", " + t : "")}: {passage.Preview}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "meeting-chat-passage-" + passage.Citation.SourceKey);
                button.Click += (_, _) => ShowCitation(chat, passage.Citation, button);
                panel.Children.Add(button);
            }
            groups.Children.Add(panel);
        }
        return groups;
    }

    /// <summary>macOS MeetingChatCitationView: the cited passage, its freshness, and a way into the meeting.</summary>
    private async void ShowCitation(MeetingChatCoordinator chat, MeetingChatCitation citation, FrameworkElement anchor)
    {
        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(IconLabel("", citation.Kind.Label()));
        panel.Children.Add(new TextBlock { Text = citation.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = $"{MeetingChatFormat.AbbreviatedDateTime(citation.StartDate)} · {citation.Kind.Label()}{(citation.Timestamp is { } t ? " · " + t : "")}",
            FontSize = 12, Foreground = Secondary
        });
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = (Brush)Application.Current.Resources["MuesliBackgroundDeepBrush"],
            Child = new ScrollViewer
            {
                MaxHeight = 300,
                Content = new TextBlock { Text = citation.Excerpt, FontSize = 15, LineHeight = 24, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Margin = new Thickness(14) }
            }
        });
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(status);
        var open = new Button { Content = IconContent("", "Open in meeting"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(open, "meeting-chat-open-source");
        panel.Children.Add(open);
        var flyout = new Flyout { Content = panel };
        open.Click += (_, _) =>
        {
            flyout.Hide();
            OpenSource(chat, citation);
        };
        flyout.ShowAt(anchor);
        var source = await chat.SourceAsync(citation.MeetingId);
        open.IsEnabled = source is not null;
        if (source is null) { status.Text = "This source is no longer available."; status.Visibility = Visibility.Visible; }
        else if (source.Revision != citation.Revision)
        {
            status.Text = "This source changed since the answer was written.";
            status.Foreground = (Brush)Application.Current.Resources["MuesliWarningBrush"];
            status.Visibility = Visibility.Visible;
        }
    }

    private UIElement? AnswerActions(MeetingChatCoordinator chat, MeetingChatTurn turn)
    {
        if (chat.Sessions.FirstOrDefault(item => item.Id == turn.SessionId) is not { } session) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var copyLabel = new TextBlock { Text = "Copy", FontSize = 12 };
        var copyIcon = new FontIcon { Glyph = "", FontSize = 12 };
        var copyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, MinWidth = 64 };
        copyContent.Children.Add(copyIcon);
        copyContent.Children.Add(copyLabel);
        var copy = PlainButton(copyContent);
        ToolTipService.SetToolTip(copy, "Copy answer");
        copy.Click += async (_, _) =>
        {
            await App.Clipboard.SetTextAsync(MeetingChatExport.ClipboardText(turn));
            copyLabel.Text = "Copied"; copyIcon.Glyph = "";
            ToolTipService.SetToolTip(copy, "Answer copied");
            await Task.Delay(2000);
            copyLabel.Text = "Copy"; copyIcon.Glyph = "";
            ToolTipService.SetToolTip(copy, "Copy answer");
        };
        row.Children.Add(copy);
        var menu = new MenuFlyout();
        var exportAnswer = new MenuFlyoutItem { Text = "Export answer…" };
        exportAnswer.Click += async (_, _) => await ExportAsync(turn, session, false);
        menu.Items.Add(exportAnswer);
        if (turn.EditableDraft is not null)
        {
            var copyDraft = new MenuFlyoutItem { Text = "Copy edited draft" };
            copyDraft.Click += async (_, _) => await App.Clipboard.SetTextAsync(MeetingChatExport.ClipboardText(turn, useEditedDraft: true));
            var exportDraft = new MenuFlyoutItem { Text = "Export edited draft…" };
            exportDraft.Click += async (_, _) => await ExportAsync(turn, session, true);
            menu.Items.Add(copyDraft);
            menu.Items.Add(exportDraft);
        }
        var export = PlainButton(IconContent("", "Export"));
        export.Flyout = menu;
        row.Children.Add(export);
        if (turn.EditableDraft is not null)
        {
            var saved = new FontIcon { Glyph = "", FontSize = 12, Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(saved, "Edited draft saved");
            AutomationName(saved, "Edited draft saved");
            row.Children.Add(saved);
        }
        return row;
    }

    /// <summary>macOS MeetingChatExporter.export: Markdown or PDF through the meeting export writer, then open it.</summary>
    private async Task ExportAsync(MeetingChatTurn turn, MeetingChatSession session, bool useEditedDraft)
    {
        var markdown = MeetingChatExport.Markdown(turn, session, useEditedDraft);
        var picked = await App.FilePickers.PickSaveFileAsync(new FilePickerRequest("Export answer", MeetingDocumentWriter.ApprovedExportExtensions, "Meeting answer.md"));
        if (picked is null) return;
        try
        {
            await Task.Run(() => MeetingDocumentWriter.Write(markdown, picked.Path));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(picked.Path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            await App.Dialogs.ShowWarningAsync(error.Message, "Export failed");
        }
    }

    // ── Small view helpers ──────────────────────────────────────────────────────────────────────

    private static Brush Primary => (Brush)Application.Current.Resources["MuesliTextPrimaryBrush"];
    private static Brush Secondary => (Brush)Application.Current.Resources["MuesliTextSecondaryBrush"];
    private static Brush Tertiary => (Brush)Application.Current.Resources["MuesliTextTertiaryBrush"];

    private Button PlainButton(object content) => new() { Content = content, Style = (Style)Resources["ChatPlainButtonStyle"] };

    private static StackPanel IconContent(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        return panel;
    }

    private static StackPanel IconLabel(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, Foreground = Secondary });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
