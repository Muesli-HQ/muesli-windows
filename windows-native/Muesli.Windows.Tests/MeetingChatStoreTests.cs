using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingChatStoreTests.swift and MeetingChatRetrievalTests.swift (2f10b23d), on the
// Windows schema: GUID meetings, notes/transcripts in their own tables, hard deletion.
public sealed class MeetingChatStoreTests : IDisposable
{
    private readonly MuesliPersistenceStore _persistence = MuesliPersistenceStore.OpenInMemory();
    private readonly MeetingChatStore _chat;

    public MeetingChatStoreTests() => _chat = new MeetingChatStore(_persistence.Database);

    public void Dispose() => _persistence.Dispose();

    private string Meeting(string title, string transcript = "", string manual = "", string generated = "", string? folder = null,
        DateTimeOffset? created = null, MeetingSessionState state = MeetingSessionState.Completed)
    {
        var id = Guid.NewGuid().ToString();
        var at = created ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        _persistence.Meetings.Upsert(new MeetingRecord
        {
            Id = id, Title = title, CreatedAtUtc = at, UpdatedAtUtc = at, FolderId = folder, SessionState = state
        });
        if (transcript.Length > 0) _persistence.Meetings.SetTranscript(id, MeetingTranscriptKind.Raw, transcript);
        if (manual.Length > 0) _persistence.Meetings.SetNote(id, MeetingNoteKind.Manual, manual);
        if (generated.Length > 0) _persistence.Meetings.SetNote(id, MeetingNoteKind.Generated, generated);
        return id;
    }

    private (MeetingChatSession Session, MeetingChatTurn Turn) Answered(string meetingId, string question = "What was decided?", string answer = "Ship it [[S1]].")
    {
        var scope = new MeetingChatScope(MeetingChatSelection.Meetings([meetingId]));
        var session = _chat.CreateSession(scope, "New chat");
        var turn = _chat.BeginTurn(session.Id, question, scope, "chatgpt", "gpt");
        var evidence = _chat.Retrieve(question, scope, includeScopeContext: true);
        _chat.AttachEvidence(turn.Id, evidence.Dependencies, turn.AttemptId);
        Assert.True(_chat.FinishTurn(turn.Id, answer, evidence.Passages.Take(1).Select(passage => passage.Citation).ToList(), evidence.Dependencies,
            attemptId: turn.AttemptId));
        return (session, _chat.Turns(session.Id).Single(item => item.Id == turn.Id));
    }

    [Fact]
    public void RoundTripsSessionTurnAndEditedDraft()
    {
        var meeting = Meeting("Launch", "[00:00:05] Ana: We ship on Friday.");
        var (session, turn) = Answered(meeting);
        _chat.SaveDraft(turn.Id, "Edited");
        var stored = _chat.Turns(session.Id).Single();
        Assert.Equal(MeetingChatTurnState.Completed, stored.State);
        Assert.Equal("Edited", stored.EditableDraft);
        Assert.Equal("00:00:05", stored.Citations.Single().Timestamp);
        Assert.Equal("What was decided?", _chat.Sessions().Single().Title);
        Assert.Equal(meeting, _chat.Sessions().Single().AnchorMeetingId);
    }

    [Fact]
    public void RecoversPendingTurnsAsInterrupted()
    {
        var session = _chat.CreateSession(MeetingChatScope.All, "New chat");
        _chat.BeginTurn(session.Id, "Q", MeetingChatScope.All, "p", "m");
        _chat.InterruptPendingTurns();
        Assert.Equal(MeetingChatTurnState.Interrupted, _chat.Turns(session.Id).Single().State);
    }

    [Fact]
    public void DeletionScrubsAnswersIncludingInheritedDependencies()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var (session, first) = Answered(meeting);
        var followUp = _chat.BeginTurn(session.Id, "Rewrite that", first.Scope, "p", "m");
        var (history, inherited) = _chat.HistoryContext(followUp);
        Assert.Single(history);
        Assert.True(_chat.FinishTurn(followUp.Id, "Rewritten.", [], inherited, attemptId: followUp.AttemptId));

        Assert.True(_persistence.Meetings.Delete(meeting));
        Assert.All(_chat.Turns(session.Id), turn =>
        {
            Assert.Equal(MeetingChatTurnState.SourceDeleted, turn.State);
            Assert.Null(turn.OriginalAnswer);
            Assert.Empty(turn.Citations);
        });
        Assert.Null(_chat.Sessions().Single().AnchorMeetingId);
        Assert.Empty(_chat.Retrieve("Friday", MeetingChatScope.All).Passages);
    }

    [Fact]
    public void SourceEditRejectsCompletionAndCompletionChecksStoredDependencies()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var scope = new MeetingChatScope(MeetingChatSelection.Meetings([meeting]));
        var session = _chat.CreateSession(scope, "New chat");
        var turn = _chat.BeginTurn(session.Id, "When?", scope, "p", "m");
        var evidence = _chat.Retrieve("Friday", scope);
        _chat.AttachEvidence(turn.Id, evidence.Dependencies, turn.AttemptId);
        _persistence.Meetings.SetTranscript(meeting, MeetingTranscriptKind.Edited, "We ship on Monday.");
        // The caller omits dependencies; the stored ones still make the stale answer fail.
        Assert.False(_chat.FinishTurn(turn.Id, "Friday.", [], [], attemptId: turn.AttemptId));
        Assert.Throws<MeetingChatException>(() => _chat.AttachEvidence(turn.Id, evidence.Dependencies, turn.AttemptId));
    }

    [Fact]
    public void StopBetweenEvidenceAndWritingCannotReviveTurnAndAttemptIsChecked()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var scope = new MeetingChatScope(MeetingChatSelection.Meetings([meeting]));
        var session = _chat.CreateSession(scope, "New chat");
        var turn = _chat.BeginTurn(session.Id, "When?", scope, "p", "m");
        _chat.SetTurnState(turn.Id, MeetingChatTurnState.Stopped, attemptId: turn.AttemptId);
        _chat.SetTurnState(turn.Id, MeetingChatTurnState.Writing, attemptId: turn.AttemptId);
        Assert.Equal(MeetingChatTurnState.Stopped, _chat.Turns(session.Id).Single().State);
        var retried = _chat.RestartTurn(turn.Id, "p", "m")!;
        Assert.False(_chat.FinishTurn(turn.Id, "late", [], [], attemptId: turn.AttemptId));
        Assert.True(_chat.FinishTurn(turn.Id, "fresh", [], [], attemptId: retried.AttemptId));
    }

    [Fact]
    public void AnchorSurvivesScopeChangesAndMultipleSelectionInventsNoAnchor()
    {
        var first = Meeting("One", "alpha");
        var second = Meeting("Two", "beta");
        var anchored = _chat.CreateSession(new MeetingChatScope(MeetingChatSelection.Meetings([first])), "New chat");
        _chat.UpdateSession(anchored.Id, null, MeetingChatScope.All);
        Assert.Equal(first, _chat.Sessions().Single(session => session.Id == anchored.Id).AnchorMeetingId);
        var both = _chat.CreateSession(new MeetingChatScope(MeetingChatSelection.Meetings([first, second])), "New chat");
        Assert.Null(both.AnchorMeetingId);
        Assert.Contains(_chat.Sessions(second), session => session.Id == both.Id);
        Assert.DoesNotContain(_chat.Sessions(second), session => session.Id == anchored.Id);
    }

    [Fact]
    public void AutomaticTitleIsGuardedAndScrubbedWhenSourceChanges()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var (session, turn) = Answered(meeting);
        Assert.False(_chat.UpdateTitleIfAutomatic(turn.Id, Guid.NewGuid(), "Stale attempt"));
        Assert.True(_chat.UpdateTitleIfAutomatic(turn.Id, turn.AttemptId!.Value, "Friday launch"));
        Assert.Equal("Friday launch", _chat.Sessions().Single().Title);
        Assert.False(_chat.UpdateTitleIfAutomatic(turn.Id, turn.AttemptId!.Value, "Again"));

        _persistence.Meetings.SetNote(meeting, MeetingNoteKind.Manual, "Changed");
        Assert.Equal("New chat", _chat.Sessions().Single().Title);

        _chat.UpdateSession(session.Id, "Mine", null);
        _persistence.Meetings.SetNote(meeting, MeetingNoteKind.Manual, "Changed again");
        Assert.Equal("Mine", _chat.Sessions().Single().Title);
    }

    [Fact]
    public void HistoryExcludesEditedSourceAnswersButKeepsSourceFreeConversation()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var (session, _) = Answered(meeting);
        var chatOnly = _chat.BeginTurn(session.Id, "Say hi", MeetingChatScope.All, "p", "m");
        Assert.True(_chat.FinishTurn(chatOnly.Id, "Hi [[S9]]", [], [], attemptId: chatOnly.AttemptId));
        var next = _chat.BeginTurn(session.Id, "Next", MeetingChatScope.All, "p", "m");
        Assert.Equal(2, _chat.HistoryContext(next).Turns.Count);

        _persistence.Meetings.SetTranscript(meeting, MeetingTranscriptKind.Edited, "We ship on Monday.");
        var history = _chat.HistoryContext(next).Turns;
        Assert.Equal("Say hi", Assert.Single(history).Question);
        Assert.DoesNotContain("[[S9]]", MeetingChatHistory.PromptBlock(history[0]));
    }

    [Fact]
    public void RollingSummaryValidatesExactPrefixAndIsScrubbedBySourceEdit()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        var (session, first) = Answered(meeting);
        var request = _chat.BeginTurn(session.Id, "Next", first.Scope, "p", "m");
        Assert.True(_chat.SaveRollingSummary("They ship Friday.", [first], request));
        Assert.Equal("They ship Friday.", _chat.RollingSummary(request)?.Text);

        _chat.SetTurnState(request.Id, MeetingChatTurnState.Stopped, attemptId: request.AttemptId);
        Assert.False(_chat.SaveRollingSummary("Late.", [first], request));

        _persistence.Meetings.SetTranscript(meeting, MeetingTranscriptKind.Edited, "We ship on Monday.");
        Assert.Null(_chat.Sessions().Single().RollingSummary);
    }

    [Fact]
    public void PickerListsFinishedMeetingsWithContentOnly()
    {
        var ready = Meeting("Ready", "hello");
        Meeting("Recording", "live", state: MeetingSessionState.Recording);
        Meeting("Empty");
        Meeting("Failed", generated: "## Summary failed\n\nboom");
        Assert.Equal([ready], _chat.SourceChoices().Select(choice => choice.Id));
    }

    [Fact]
    public void RetrievalFindsFtsMatchesWithExactUnicodeOffsetsAndSkipsHeadings()
    {
        var meeting = Meeting("Café sync", "[00:01:00] Zoë: naïve plan 🎉 budget approved\nsecond line", generated: "## Decisions\nBudget approved by Zoë.");
        var evidence = _chat.Retrieve("What about the budget?", MeetingChatScope.All);
        Assert.NotEmpty(evidence.Passages);
        Assert.DoesNotContain(evidence.Passages, passage => passage.Excerpt.StartsWith('#'));
        var transcript = _persistence.Meetings.ListTranscripts(meeting).Single().Content;
        foreach (var passage in evidence.Passages.Where(passage => passage.Kind == MeetingChatSourceKind.Transcript))
            Assert.Equal(passage.Excerpt, transcript.Substring(passage.Range.Location, passage.Range.Length));
        Assert.Equal("S1", evidence.Passages[0].SourceKey);
        Assert.Equal(meeting, evidence.Dependencies.Single().MeetingId);
    }

    [Fact]
    public void WarmSearchDoesNotReadUnchangedMeetingContentAndEditsReindex()
    {
        var meeting = Meeting("Launch", "We ship on Friday.");
        Assert.Equal(1, _chat.PrepareIndex().ReindexedMeetingCount);
        Assert.Equal(0, _chat.Retrieve("Friday", MeetingChatScope.All).Metrics.SourceSnapshotCount);
        _persistence.Meetings.SetTranscript(meeting, MeetingTranscriptKind.Edited, "We ship on Monday.");
        var evidence = _chat.Retrieve("Monday", MeetingChatScope.All);
        Assert.Equal(1, evidence.Metrics.ReindexedMeetingCount);
        Assert.Contains("Monday", evidence.Passages.Single().Excerpt);
    }

    [Fact]
    public void ScopeFiltersFolderDatesAndSelection()
    {
        var folder = Guid.NewGuid().ToString();
        _persistence.Folders.Upsert(new FolderRecord { Id = folder, Name = "Clients", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        var filed = Meeting("Filed", "roadmap filed", folder: folder);
        var old = Meeting("Old", "roadmap old", created: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal([filed], _chat.Retrieve("roadmap", new MeetingChatScope(MeetingChatSelection.Folder(folder))).Dependencies.Select(item => item.MeetingId));
        Assert.Equal([filed], _chat.Retrieve("roadmap", new MeetingChatScope(MeetingChatSelection.All, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            .Dependencies.Select(item => item.MeetingId));
        Assert.Empty(_chat.Retrieve("roadmap", new MeetingChatScope(MeetingChatSelection.Meetings([]))).Passages);
        Assert.Equal([old], _chat.Retrieve("roadmap", new MeetingChatScope(MeetingChatSelection.Meetings([old]))).Dependencies.Select(item => item.MeetingId));

        // Folder moves change scope, never content: answers stay valid.
        var (_, turn) = Answered(filed, "roadmap?");
        _persistence.Meetings.MoveToFolder(filed, null);
        Assert.True(_chat.DependenciesAreCurrent(_chat.Dependencies(turn.Id)));
        Assert.Empty(_chat.Retrieve("roadmap", new MeetingChatScope(MeetingChatSelection.Folder(folder))).Passages);
    }

    [Fact]
    public void AttachedContextSuppliesSourcesWithoutLexicalMatchesAndLikeFallbackEscapes()
    {
        var meeting = Meeting("Standup", "Ana: blockers are none", generated: "Summary paragraph");
        var scope = new MeetingChatScope(MeetingChatSelection.Meetings([meeting]));
        Assert.Empty(_chat.Retrieve("zebra", scope).Passages);
        var context = _chat.Retrieve("zebra", scope, includeScopeContext: true);
        Assert.Contains(context.Passages, passage => passage.Kind == MeetingChatSourceKind.Transcript);
        Assert.Contains(context.Passages, passage => passage.Kind == MeetingChatSourceKind.GeneratedNotes);

        Meeting("Wildcards", "growth was 100% in Q3");
        Assert.DoesNotContain(_chat.Retrieve("%", MeetingChatScope.All).Passages, passage => !passage.Excerpt.Contains('%'));
    }

    [Fact]
    public void BroadRecapReportsPartialCoverage()
    {
        for (var index = 0; index < 30; index++) Meeting($"M{index}", string.Join("\n", Enumerable.Repeat($"line {index} " + new string('x', 400), 5)));
        var evidence = _chat.Retrieve("recap", MeetingChatScope.All, broadRecap: true);
        Assert.True(evidence.Coverage.IsPartialRecap);
        Assert.Equal(30, evidence.Coverage.EligibleMeetingCount);
    }
}
