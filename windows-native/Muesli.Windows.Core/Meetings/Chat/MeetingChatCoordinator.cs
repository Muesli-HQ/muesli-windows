namespace Muesli.Windows.Services;

public enum MeetingChatActivityState { Running, Completed, Failed, Cancelled }

/// <summary>One retrieval operation the harness actually executed. Transient: never saved with the chat.</summary>
public sealed class MeetingChatActivity(string title, string detail)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public MeetingChatActivityState State { get; set; } = MeetingChatActivityState.Running;
    public string? Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Port of the macOS MeetingChatCoordinator (2f10b23d): UI-facing chat state shared by the Ask Meetings
/// page and the panel embedded in meeting detail. Call members from one thread (the UI thread); store
/// work runs on the thread pool and continuations return to the caller's synchronization context.
/// Engine progress may arrive on other threads and is marshalled through <c>post</c>.
/// </summary>
public sealed class MeetingChatCoordinator
{
    private readonly MeetingChatStore _store;
    private readonly MeetingChatEngine _engine;
    private readonly Func<IMeetingTextGenerator> _generator;
    private readonly Action<Action> _post;
    private readonly Dictionary<Guid, (Task Task, CancellationTokenSource Cancel)> _tasks = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _titleTasks = [];
    private readonly Dictionary<Guid, Guid> _titleAttempts = [];
    private readonly Dictionary<Guid, string> _composerDrafts = [];
    private readonly Dictionary<Guid, Guid> _composerUndoIdentities = [];
    private readonly Task _initialization;
    private string _untitledComposerDraft = "";
    private Guid _untitledComposerUndoIdentity = Guid.NewGuid();
    private Guid _composerEditVersion = Guid.NewGuid();
    private Guid _selectionVersion = Guid.NewGuid();
    private Guid? _activeSessionId;
    private MeetingChatScope _draftScope = MeetingChatScope.All;

    public MeetingChatCoordinator(MeetingChatStore store, Func<IMeetingTextGenerator> generator, Action<Action>? post = null)
    {
        _store = store;
        _engine = new MeetingChatEngine(store);
        _generator = generator;
        _post = post ?? (action => action());
        Reload();
        SourceMutationVersion = Safe(() => store.SourceMutationVersion(), 0L);
        _initialization = InitializeAsync();
    }

    /// <summary>Raised after any observable state change, on the thread that made it.</summary>
    public event Action? Changed;

    public IReadOnlyList<MeetingChatSession> Sessions { get; private set; } = [];
    public bool HasChatHistory { get; private set; }
    public IReadOnlyList<MeetingChatTurn> Turns { get; private set; } = [];
    public Guid? SelectedSessionId { get; private set; }
    public Guid? ActiveRequestId { get; private set; }
    public Guid? ActiveTurnId { get; private set; }
    public string Phase { get; private set; } = "";
    public List<MeetingChatActivity> Activities { get; private set; } = [];
    public Guid? ActivityTurnId { get; private set; }
    /// <summary>macOS reasoningSummaries: one public provider summary per model iteration of the
    /// activity turn. Transient display text; never saved.</summary>
    public List<string> ReasoningSummaries { get; private set; } = [];
    public bool ReasoningIsStreaming { get; private set; }
    public long SourceMutationVersion { get; private set; }
    public IReadOnlyList<MeetingChatSourceChoice> SourceChoices { get; private set; } = [];
    public bool IsUpdatingChat { get; private set; }
    public bool IsBusy => ActiveRequestId is not null;

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set { _errorMessage = value; Notify(); } }

    public MeetingChatScope Scope => Sessions.FirstOrDefault(item => item.Id == SelectedSessionId)?.Scope ?? _draftScope;

    public MeetingChatSession? SelectedSession => Sessions.FirstOrDefault(item => item.Id == SelectedSessionId);

    /// <summary>Unsent text per conversation; the untitled composer is keyed separately.</summary>
    public string ComposerDraft
    {
        get => SelectedSessionId is { } id ? _composerDrafts.GetValueOrDefault(id, "") : _untitledComposerDraft;
        set
        {
            if (SelectedSessionId is { } id) _composerDrafts[id] = value;
            else _untitledComposerDraft = value;
            _composerEditVersion = Guid.NewGuid();
            Notify();
        }
    }

    /// <summary>A change means the editor's undo history must be cleared (chat switch or accepted send).</summary>
    public Guid ComposerUndoIdentity => SelectedSessionId is { } id
        ? _composerUndoIdentities.GetValueOrDefault(id, id)
        : _untitledComposerUndoIdentity;

    private async Task InitializeAsync()
    {
        try { await Task.Run(_store.InterruptPendingTurns); Reload(); }
        catch { ErrorMessage = "Could not recover meeting chat history."; }
        try { SourceChoices = await Task.Run(_store.SourceChoices); Notify(); }
        catch { ErrorMessage = "Could not prepare meeting search. Retry by asking a question."; }
    }

    /// <summary>Return to the index without discarding per-conversation drafts or stopping a response.</summary>
    public void ShowIndex()
    {
        _selectionVersion = Guid.NewGuid();
        SelectedSessionId = null;
        Turns = [];
        _errorMessage = null;
        Notify();
    }

    /// <summary>Opening a composer is not a persisted conversation. The first send registers it.</summary>
    public void StartDraft(MeetingChatScope? scope = null)
    {
        if (IsBusy || IsUpdatingChat) return;
        _selectionVersion = Guid.NewGuid();
        SelectedSessionId = null;
        Turns = [];
        _draftScope = scope ?? MeetingChatScope.All;
        _untitledComposerDraft = "";
        _composerEditVersion = Guid.NewGuid();
        _untitledComposerUndoIdentity = Guid.NewGuid();
        _errorMessage = null;
        Notify();
    }

    /// <summary>Browsing notes may seed an unused draft, never retarget an existing conversation.</summary>
    public void PrepareMeetingDraft(string meetingId)
    {
        if (SelectedSessionId is not null || ComposerDraft.Length > 0 || IsBusy || IsUpdatingChat) return;
        _draftScope = new MeetingChatScope(MeetingChatSelection.Meetings([meetingId]));
        Notify();
    }

    /// <summary>
    /// macOS MuesliController.showMeetingChat(meetingID:): resume the newest conversation anchored on this
    /// meeting that still explicitly includes it, or prepare a draft for it. Archive chats that merely
    /// cited the meeting are not matched. Refused while a response or chat update is running.
    /// </summary>
    public bool EnterMeetingChat(string meetingId)
    {
        if (IsBusy || IsUpdatingChat) return false;
        var session = Sessions.FirstOrDefault(item => item.AnchorMeetingId == meetingId
            && item.Scope.Selection.Kind == MeetingChatSelectionKind.Meetings && item.Scope.Selection.Ids.Contains(meetingId));
        if (session is not null) { SelectChat(session.Id); return true; }
        ShowIndex();
        PrepareMeetingDraft(meetingId);
        return true;
    }

    public void Reload()
    {
        try
        {
            Sessions = _store.Sessions();
            HasChatHistory = _store.HasChatHistory();
            var ids = Sessions.Select(item => item.Id).ToHashSet();
            foreach (var key in _composerDrafts.Keys.Where(key => !ids.Contains(key)).ToList()) _composerDrafts.Remove(key);
            if (SelectedSessionId is { } id && ids.Contains(id)) Turns = _store.Turns(id);
            else { SelectedSessionId = null; Turns = []; }
        }
        catch { _errorMessage = "Could not load meeting chat history."; }
        Notify();
    }

    public async Task<Guid?> CreateChatAsync(MeetingChatScope scope)
    {
        if (IsUpdatingChat) return null;
        IsUpdatingChat = true;
        Notify();
        var version = Guid.NewGuid();
        _selectionVersion = version;
        try
        {
            var expected = _store.SourceMutationVersion();
            await _initialization;
            if (_selectionVersion != version) return null;
            var session = await Task.Run(() => _store.CreateSession(scope, "New chat", expectedSourceVersion: expected));
            if (_selectionVersion == version) { SelectedSessionId = session.Id; _errorMessage = null; }
            Reload();
            return session.Id;
        }
        catch (Exception error) { ErrorMessage = error.Message; return null; }
        finally { IsUpdatingChat = false; Notify(); }
    }

    public void SelectChat(Guid id)
    {
        _selectionVersion = Guid.NewGuid();
        SelectedSessionId = id;
        _errorMessage = null;
        Reload();
    }

    public async Task<Guid?> SetScopeAsync(MeetingChatScope scope)
    {
        if (SelectedSessionId is null)
        {
            if (await CreateChatAsync(scope) is not { } created) return null;
            AdoptUntitledComposerDraft(created);
            return created;
        }
        if (SelectedSessionId is not { } id || IsUpdatingChat) return null;
        IsUpdatingChat = true;
        Notify();
        try { await Task.Run(() => _store.UpdateSession(id, null, scope)); Reload(); return id; }
        catch (Exception error) { ErrorMessage = error.Message; return null; }
        finally { IsUpdatingChat = false; Notify(); }
    }

    /// <summary>Composer attachments are an explicit scope, never a hint that can widen retrieval.</summary>
    public Task<bool> AddMeetingToScopeAsync(string meetingId) => UpdateMeetingAttachmentAsync(meetingId, adding: true);

    public Task<bool> RemoveMeetingFromScopeAsync(string meetingId) => UpdateMeetingAttachmentAsync(meetingId, adding: false);

    private async Task<bool> UpdateMeetingAttachmentAsync(string meetingId, bool adding)
    {
        if (IsBusy || IsUpdatingChat) return false;
        var target = SelectedSessionId;
        long expected;
        try { expected = _store.SourceMutationVersion(); }
        catch (Exception error) { ErrorMessage = error.Message; return false; }
        var version = _selectionVersion;
        var attached = Scope.Selection.Kind == MeetingChatSelectionKind.Meetings ? Scope.Selection.Ids.ToList() : [];
        if (!adding && !attached.Contains(meetingId)) return false;
        var scope = Scope;
        if (adding)
        {
            if (!attached.Contains(meetingId)) attached.Add(meetingId);
            // A chosen meeting must not remain hidden behind an old date filter.
            scope = scope with { StartDate = null, EndDateExclusive = null };
        }
        else attached.Remove(meetingId);
        scope = scope with { Selection = MeetingChatSelection.Meetings(attached) };
        IsUpdatingChat = true;
        Notify();
        try
        {
            await _initialization;
            if (_selectionVersion != version) return false;
            if (adding)
            {
                var source = await SourceAsync(meetingId);
                if (source is null || (source.Transcript + source.ManualNotes + source.GeneratedNotes).Trim().Length == 0)
                {
                    _errorMessage = "This meeting is no longer available for chat.";
                    return false;
                }
            }
            if (_selectionVersion != version) return false;
            if (target is { } id) await Task.Run(() => _store.UpdateSession(id, null, scope));
            else
            {
                var session = await Task.Run(() => _store.CreateSession(scope, "New chat", expectedSourceVersion: expected));
                // Preserve the untitled composer's latest edits while registration awaits.
                if (_selectionVersion == version)
                {
                    _composerDrafts[session.Id] = _untitledComposerDraft;
                    _untitledComposerDraft = "";
                    TransferUntitledComposerUndoIdentity(session.Id);
                    SelectedSessionId = session.Id;
                }
            }
            Reload();
            if (_selectionVersion != version) return false;
            _errorMessage = null;
            return true;
        }
        catch (Exception error)
        {
            if (_selectionVersion == version) _errorMessage = error.Message;
            return false;
        }
        finally { IsUpdatingChat = false; Notify(); }
    }

    public async Task<bool> ApplyPreparedPromptAsync(MeetingChatPreparedPrompt prompt)
    {
        var editVersion = _composerEditVersion;
        Guid? target;
        if (!prompt.Scope.Equals(Scope))
        {
            if (await SetScopeAsync(prompt.Scope) is not { } id) return false;
            target = id;
        }
        else target = SelectedSessionId;
        if (SelectedSessionId != target) return false;
        // SetScopeAsync already transfers any edits made during registration.
        if (_composerEditVersion != editVersion) return false;
        ComposerDraft = prompt.Text;
        return true;
    }

    public async Task RenameChatAsync(Guid id, string title)
    {
        CancelTitle(id);
        try { await Task.Run(() => _store.UpdateSession(id, title, null)); Reload(); }
        catch (Exception error) { ErrorMessage = error.Message; }
    }

    public async Task SendAsync(string question, bool isDraft = false, int? contextWindowTokens = null)
    {
        if (IsBusy || IsUpdatingChat) return;
        question = question.Trim();
        if (question.Length == 0) return;
        if (question.Length > 2_000) { ErrorMessage = MeetingChatException.QuestionTooLong().Message; return; }
        if (Scope.Selection.Kind == MeetingChatSelectionKind.Meetings && Scope.Selection.Ids.Count == 0
            && !Turns.Any(turn => turn.State == MeetingChatTurnState.Completed && turn.OriginalAnswer is not null))
        {
            ErrorMessage = "Attach a meeting or choose All saved meetings before sending. Your question is still in the composer.";
            return;
        }
        var requestId = Guid.NewGuid();
        ActiveRequestId = requestId;
        Phase = "Saving question";
        ActivityTurnId = null; ReasoningSummaries = [];
        var draftKey = SelectedSessionId;
        var submittedDraft = ComposerDraft;
        _activeSessionId = draftKey;
        var requestScope = Scope;
        Notify();
        try
        {
            await _initialization;
            if (ActiveRequestId != requestId) return;
            var sessionId = draftKey;
            if (sessionId is null)
            {
                if (SelectedSessionId is not null) return;
                sessionId = await CreateChatAsync(requestScope);
                // An implicit first send turns the untitled composer into this chat. Move its latest
                // text, including edits or a Stop during registration.
                if (sessionId is { } created) AdoptUntitledComposerDraft(created);
            }
            if (ActiveRequestId != requestId || sessionId is not { } id) return;
            _activeSessionId = id;
            MeetingChatTurn turn;
            var generator = _generator();
            try
            {
                turn = await Task.Run(() => _store.BeginTurn(id, question, requestScope, ProviderLabel(generator.Provider), generator.Model, requestId));
            }
            catch (Exception error)
            {
                if (ActiveRequestId != requestId) return;
                ActiveRequestId = null; ActiveTurnId = null; _activeSessionId = null; Phase = "";
                _errorMessage = error.Message;
                Reload();
                return;
            }
            if (ActiveRequestId != requestId)
            {
                await Task.Run(() => TrySetState(turn.Id, MeetingChatTurnState.Stopped, null, requestId));
                Reload();
                return;
            }
            ActiveTurnId = turn.Id;
            // An accepted send is an undo boundary even if the user started a new draft while saving.
            _composerUndoIdentities[id] = Guid.NewGuid();
            if (_composerDrafts.GetValueOrDefault(id) == submittedDraft && submittedDraft.Trim() == question) _composerDrafts.Remove(id);
            _errorMessage = null;
            Reload();
            Launch(turn, generator, isDraft, contextWindowTokens);
        }
        finally
        {
            if (ActiveRequestId == requestId && !_tasks.ContainsKey(requestId))
            {
                ActiveRequestId = null; ActiveTurnId = null; _activeSessionId = null; Phase = "";
            }
            Notify();
        }
    }

    /// <summary>Registration may finish after a refresh invalidated selection ownership. Only the session
    /// actually showing the composer may take its unsent text.</summary>
    public void AdoptUntitledComposerDraft(Guid sessionId)
    {
        if (SelectedSessionId != sessionId || _composerDrafts.ContainsKey(sessionId)) return;
        _composerDrafts[sessionId] = _untitledComposerDraft;
        _untitledComposerDraft = "";
        TransferUntitledComposerUndoIdentity(sessionId);
        Notify();
    }

    public async Task RetryAsync(Guid turnId, int? contextWindowTokens = null)
    {
        if (IsBusy || IsUpdatingChat) return;
        if (Turns.FirstOrDefault(item => item.Id == turnId) is { } previous
            && previous.Scope.Selection.Kind == MeetingChatSelectionKind.Meetings && previous.Scope.Selection.Ids.Count == 0
            && !Turns.Any(item => item.Ordinal < previous.Ordinal && item.State == MeetingChatTurnState.Completed && item.OriginalAnswer is not null))
        {
            ErrorMessage = "This question had no meetings attached. Attach a meeting or choose All saved meetings, then send the question again.";
            return;
        }
        var reservation = Guid.NewGuid();
        ActiveRequestId = reservation;
        Phase = "Saving question";
        ActivityTurnId = null; ReasoningSummaries = [];
        _activeSessionId = Turns.FirstOrDefault(item => item.Id == turnId)?.SessionId;
        Notify();
        try
        {
            var generator = _generator();
            var turn = await Task.Run(() => _store.RestartTurn(turnId, ProviderLabel(generator.Provider), generator.Model));
            if (turn is null) return;
            if (ActiveRequestId != reservation)
            {
                await Task.Run(() => TrySetState(turn.Id, MeetingChatTurnState.Stopped, null, turn.AttemptId));
                Reload();
                return;
            }
            Reload();
            Launch(turn, generator, turn.IsDraft, contextWindowTokens);
        }
        catch (Exception error)
        {
            if (ActiveRequestId == reservation) { ActiveRequestId = null; Phase = ""; _errorMessage = error.Message; }
        }
        finally
        {
            if (ActiveRequestId == reservation && !_tasks.ContainsKey(reservation)) { ActiveRequestId = null; _activeSessionId = null; Phase = ""; }
            Notify();
        }
    }

    private void Launch(MeetingChatTurn turn, IMeetingTextGenerator generator, bool isDraft, int? contextWindowTokens)
    {
        CancelTitle(turn.SessionId);
        var requestId = turn.AttemptId ?? Guid.NewGuid();
        ActiveRequestId = requestId;
        ActiveTurnId = turn.Id;
        _activeSessionId = turn.SessionId;
        Phase = "Finding meeting context";
        Activities = []; ReasoningSummaries = []; ReasoningIsStreaming = false;
        ActivityTurnId = turn.Id;
        var cancel = new CancellationTokenSource();
        var progress = new MeetingChatProgress
        {
            Phase = phase => _post(() => { if (ActiveRequestId == requestId) { ReasoningIsStreaming = false; Phase = phase; Notify(); } }),
            Activity = (call, completed) => _post(() =>
            {
                if (ActiveRequestId != requestId) return;
                ReasoningIsStreaming = false;
                if (completed) { if (Activities.Count > 0) Activities[^1].State = MeetingChatActivityState.Completed; }
                else Activities.Add(new MeetingChatActivity(call.Title, call.Detail));
                Notify();
            }),
            ActivityOutput = output => _post(() =>
            {
                if (ActiveRequestId == requestId && Activities.Count > 0) { Activities[^1].Output = output; Notify(); }
            }),
            ReasoningSummary = async (iteration, summary) =>
            {
                if (cancel.IsCancellationRequested) return;
                // Summaries contain meeting data too. Check persisted invalidation before displaying a
                // delta, even when the UI has not yet refreshed after deletion.
                var valid = await Task.Run(() => Safe(() => _engine.CanPublishSummary(turn), false));
                _post(() =>
                {
                    if (ActiveRequestId != requestId || cancel.IsCancellationRequested) return;
                    if (!valid) { ReasoningSummaries = []; Stop(); return; }
                    while (ReasoningSummaries.Count <= iteration) ReasoningSummaries.Add("");
                    ReasoningIsStreaming = true;
                    ReasoningSummaries[iteration] = summary;
                    Phase = "Preparing answer";
                    Notify();
                });
            },
        };
        _tasks[requestId] = (RunAsync(), cancel);
        Notify();

        async Task RunAsync()
        {
            await Task.Yield();
            try
            {
                var answer = await _engine.AnswerAsync(turn, generator, isDraft, progress, contextWindowTokens, cancel.Token);
                if (ActiveRequestId == requestId && !cancel.IsCancellationRequested && !answer.InsufficientEvidence)
                    ScheduleTitle(turn, generator);
            }
            catch (Exception error)
            {
                if (ActiveRequestId == requestId)
                {
                    ReasoningIsStreaming = false;
                    var stopped = cancel.IsCancellationRequested || error is OperationCanceledException;
                    var message = stopped ? null : MeetingChatEngine.FailureMessage(error);
                    foreach (var activity in Activities.Where(item => item.State == MeetingChatActivityState.Running))
                    {
                        activity.State = stopped ? MeetingChatActivityState.Cancelled : MeetingChatActivityState.Failed;
                        activity.Error = message;
                    }
                    await Task.Run(() => TrySetState(turn.Id, stopped ? MeetingChatTurnState.Stopped : MeetingChatTurnState.Failed, message, turn.AttemptId));
                }
            }
            finally
            {
                _tasks.Remove(requestId);
                if (ActiveRequestId == requestId) { ActiveRequestId = null; ActiveTurnId = null; _activeSessionId = null; Phase = ""; ReasoningIsStreaming = false; }
                cancel.Dispose();
                Reload();
            }
        }
    }

    private void ScheduleTitle(MeetingChatTurn turn, IMeetingTextGenerator generator)
    {
        CancelTitle(turn.SessionId);
        var token = Guid.NewGuid();
        var cancel = new CancellationTokenSource();
        _titleAttempts[turn.SessionId] = token;
        _titleTasks[turn.SessionId] = cancel;
        _ = RunTitleAsync();

        async Task RunTitleAsync()
        {
            try
            {
                var saved = await _engine.GenerateTitleAsync(turn, generator, cancel.Token);
                if (saved && _titleAttempts.GetValueOrDefault(turn.SessionId) == token) Reload();
            }
            catch
            {
                // A title is optional metadata. Keep the provisional title and completed answer.
            }
            finally
            {
                if (_titleAttempts.GetValueOrDefault(turn.SessionId) == token)
                {
                    _titleTasks.Remove(turn.SessionId);
                    _titleAttempts.Remove(turn.SessionId);
                }
            }
        }
    }

    private void CancelTitle(Guid sessionId)
    {
        if (_titleTasks.Remove(sessionId, out var cancel)) cancel.Cancel();
        _titleAttempts.Remove(sessionId);
    }

    public void Stop()
    {
        ActivityTurnId = null; ReasoningSummaries = [];
        if (ActiveRequestId is not { } id) { Notify(); return; }
        if (_tasks.TryGetValue(id, out var task)) task.Cancel.Cancel();
        if (ActiveTurnId is { } turnId)
        {
            _ = Task.Run(() => TrySetState(turnId, MeetingChatTurnState.Stopped, null, id)).ContinueWith(_ => _post(Reload), TaskScheduler.Default);
            var index = Turns.ToList().FindIndex(item => item.Id == turnId && item.IsPending);
            if (index >= 0) Turns[index].State = MeetingChatTurnState.Stopped;
        }
        ActiveRequestId = null; ActiveTurnId = null; _activeSessionId = null; Phase = ""; ReasoningIsStreaming = false;
        Notify();
    }

    public async Task DeleteChatAsync(Guid id)
    {
        CancelTitle(id);
        if (ActivityTurnId is { } activity && Safe(() => _store.Turns(id).Any(turn => turn.Id == activity), false)) { ActivityTurnId = null; ReasoningSummaries = []; }
        if (_activeSessionId == id) Stop();
        try
        {
            await Task.Run(() => _store.DeleteSession(id));
            _composerDrafts.Remove(id);
            _composerUndoIdentities.Remove(id);
            Reload();
        }
        catch (Exception error) { ErrorMessage = error.Message; }
    }

    public async Task SaveDraftAsync(Guid turnId, string text)
    {
        try { await Task.Run(() => _store.SaveDraft(turnId, text)); Reload(); }
        catch (Exception error) { ErrorMessage = error.Message; }
    }

    public async Task RefreshChoicesAsync()
    {
        try { SourceChoices = await Task.Run(_store.SourceChoices); Notify(); }
        catch (Exception error) { ErrorMessage = error.Message; }
    }

    public async Task<MeetingChatSourceSnapshot?> SourceAsync(string meetingId)
    {
        try { return await Task.Run(() => _store.SourceSnapshots(new MeetingChatScope(MeetingChatSelection.Meetings([meetingId]))).FirstOrDefault()); }
        catch { return null; }
    }

    public async Task WaitForIdleAsync()
    {
        await _initialization;
        while (_tasks.Values.FirstOrDefault().Task is { } task) await task;
    }

    /// <summary>Called after meeting edits or deletion; invalidates setup ownership, titles and answers in flight.</summary>
    public void RefreshForSourceChanges()
    {
        long version;
        try { version = _store.SourceMutationVersion(); } catch { return; }
        if (version == SourceMutationVersion) return;
        SourceMutationVersion = version;
        // Invalidate setup ownership too: there may not be a turn or session ID yet.
        _selectionVersion = Guid.NewGuid();
        if (ActiveRequestId is not null && ActiveTurnId is null) Stop();
        foreach (var sessionId in _titleTasks.Keys.ToList()) CancelTitle(sessionId);
        ActivityTurnId = null; ReasoningSummaries = [];
        if (_activeSessionId is { } session && ActiveTurnId is { } turnId)
        {
            var current = Safe(() => _store.Turns(session).FirstOrDefault(item => item.Id == turnId), null);
            if (current is null || current.State == MeetingChatTurnState.SourceDeleted) Stop();
        }
        Reload();
        _ = RefreshChoicesAsync();
    }

    private void TransferUntitledComposerUndoIdentity(Guid sessionId)
    {
        _composerUndoIdentities[sessionId] = _untitledComposerUndoIdentity;
        _untitledComposerUndoIdentity = Guid.NewGuid();
    }

    private void TrySetState(Guid turnId, MeetingChatTurnState state, string? error, Guid? attemptId)
    {
        try { _store.SetTurnState(turnId, state, error, attemptId); } catch { }
    }

    public static string ProviderLabel(string provider) => SummaryProviderDisclosure.For(provider).DisplayName;

    private void Notify() => Changed?.Invoke();

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
