using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingChatNavigationTests, MeetingChatReviewTests and MeetingChatPresentationTests
// (2f10b23d): coordinator ownership rules and the presentation helpers behind the Ask Meetings UI.
public sealed class MeetingChatCoordinatorTests : IDisposable
{
    private sealed class Replies(params string[] replies) : IMeetingTextGenerator
    {
        private readonly Queue<string> _replies = new(replies);
        public TaskCompletionSource? Gate { get; init; }
        public string Provider => "openai";
        public string Model => "test-model";

        public async Task<string> GenerateAsync(MeetingTextGenerationRequest request, CancellationToken cancellationToken)
        {
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            return _replies.Count > 0 ? _replies.Dequeue() : """{"title":"Launch timing"}""";
        }
    }

    private readonly MuesliPersistenceStore _persistence = MuesliPersistenceStore.OpenInMemory();

    public void Dispose() => _persistence.Dispose();

    private string Meeting(string title, string transcript)
    {
        var id = Guid.NewGuid().ToString();
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        _persistence.Meetings.Upsert(new MeetingRecord { Id = id, Title = title, CreatedAtUtc = at, UpdatedAtUtc = at });
        _persistence.Meetings.SetTranscript(id, MeetingTranscriptKind.Raw, transcript);
        return id;
    }

    private MeetingChatCoordinator Coordinator(IMeetingTextGenerator generator) =>
        new(new MeetingChatStore(_persistence.Database), () => generator);

    [Fact]
    public async Task FirstSendRegistersChatClearsComposerAndGeneratesTitle()
    {
        Meeting("Launch", "Launch Friday");
        var coordinator = Coordinator(new Replies("""{"status":"answered","markdown":"Friday [[S1]]"}"""));
        coordinator.ComposerDraft = "When is launch?";
        await coordinator.SendAsync(coordinator.ComposerDraft);
        await coordinator.WaitForIdleAsync();
        await Task.Delay(50);

        var session = Assert.Single(coordinator.Sessions);
        Assert.Equal(session.Id, coordinator.SelectedSessionId);
        Assert.Equal("", coordinator.ComposerDraft);
        Assert.False(coordinator.IsBusy);
        var turn = Assert.Single(coordinator.Turns);
        Assert.Equal(MeetingChatTurnState.Completed, turn.State);
        Assert.Equal("OpenAI API", turn.Provider);
        coordinator.Reload();
        Assert.Equal("Launch timing", coordinator.SelectedSession!.Title);
    }

    [Fact]
    public async Task EmptyExplicitSelectionRejectsSendWithoutReservingBusyState()
    {
        Meeting("Launch", "Launch Friday");
        var coordinator = Coordinator(new Replies());
        coordinator.StartDraft(new MeetingChatScope(MeetingChatSelection.Meetings([])));
        coordinator.ComposerDraft = "launch?";
        await coordinator.SendAsync("launch?");
        Assert.False(coordinator.IsBusy);
        Assert.Empty(coordinator.Sessions);
        Assert.StartsWith("Attach a meeting", coordinator.ErrorMessage);
        Assert.Equal("launch?", coordinator.ComposerDraft);
    }

    [Fact]
    public async Task AttachingClearsDatesAndRemovingTheLastMeetingKeepsAnEmptyScope()
    {
        var id = Meeting("Launch", "Launch Friday");
        var coordinator = Coordinator(new Replies());
        coordinator.StartDraft(MeetingChatScope.All with { StartDate = DateTimeOffset.UtcNow.AddDays(-1) });
        coordinator.ComposerDraft = "keep me";
        Assert.True(await coordinator.AddMeetingToScopeAsync(id));
        Assert.Equal([id], coordinator.Scope.Selection.Ids);
        Assert.Null(coordinator.Scope.StartDate);
        Assert.Equal("keep me", coordinator.ComposerDraft);
        Assert.Equal(id, coordinator.SelectedSession!.AnchorMeetingId);

        Assert.True(await coordinator.RemoveMeetingFromScopeAsync(id));
        Assert.Equal(MeetingChatSelectionKind.Meetings, coordinator.Scope.Selection.Kind);
        Assert.Empty(coordinator.Scope.Selection.Ids);
    }

    [Fact]
    public async Task MeetingEntryResumesOnlyAnchoredChatsThatStillIncludeTheMeeting()
    {
        var id = Meeting("Launch", "Launch Friday");
        var coordinator = Coordinator(new Replies());
        await coordinator.CreateChatAsync(MeetingChatScope.All);
        Assert.True(coordinator.EnterMeetingChat(id));
        Assert.Null(coordinator.SelectedSessionId);
        Assert.Equal([id], coordinator.Scope.Selection.Ids);

        var anchored = await coordinator.CreateChatAsync(new MeetingChatScope(MeetingChatSelection.Meetings([id])));
        coordinator.ShowIndex();
        Assert.True(coordinator.EnterMeetingChat(id));
        Assert.Equal(anchored, coordinator.SelectedSessionId);
    }

    [Fact]
    public async Task PreparingAMeetingDraftNeverRetargetsAConversationOrTypedDraft()
    {
        var id = Meeting("Launch", "Launch Friday");
        var coordinator = Coordinator(new Replies());
        coordinator.ComposerDraft = "typed";
        coordinator.PrepareMeetingDraft(id);
        Assert.Equal(MeetingChatSelectionKind.All, coordinator.Scope.Selection.Kind);

        coordinator.ComposerDraft = "";
        await coordinator.CreateChatAsync(MeetingChatScope.All);
        coordinator.PrepareMeetingDraft(id);
        Assert.Equal(MeetingChatSelectionKind.All, coordinator.Scope.Selection.Kind);
    }

    [Fact]
    public async Task StopMarksTheTurnStoppedAndNothingIsPublished()
    {
        Meeting("Launch", "Launch Friday");
        var gate = new TaskCompletionSource();
        var coordinator = Coordinator(new Replies("""{"status":"answered","markdown":"Friday [[S1]]"}""") { Gate = gate });
        await coordinator.SendAsync("launch?");
        Assert.True(coordinator.IsBusy);
        coordinator.Stop();
        Assert.False(coordinator.IsBusy);
        gate.SetResult();
        await coordinator.WaitForIdleAsync();
        await Task.Delay(50);
        coordinator.Reload();
        var turn = Assert.Single(coordinator.Turns);
        Assert.Equal(MeetingChatTurnState.Stopped, turn.State);
        Assert.Null(turn.OriginalAnswer);
    }

    [Fact]
    public void WeeklyRecapIntersectsTheFilterAndNeverWidensIt()
    {
        var now = new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.FromHours(5.5));
        var recap = MeetingChatQuickAction.WeeklyRecap.Prepare(MeetingChatScope.All, now, null);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, now.Offset), recap.Scope.StartDate);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, now.Offset), recap.Scope.EndDateExclusive);

        var old = MeetingChatScope.All with { StartDate = now.AddDays(-30), EndDateExclusive = now.AddDays(-20) };
        var empty = MeetingChatQuickAction.WeeklyRecap.Prepare(old, now, null);
        Assert.Equal(empty.Scope.StartDate, empty.Scope.EndDateExclusive);
        Assert.StartsWith("The selected date range does not overlap", empty.Text);

        Assert.True(MeetingChatQuickAction.MyNextSteps.Prepare(MeetingChatScope.All, now, " ").NeedsParticipantName);
        Assert.Contains("assigned to Asha,", MeetingChatQuickAction.MyNextSteps.Prepare(MeetingChatScope.All, now, "Asha").Text);
        Assert.True(MeetingChatQuickAction.DraftFollowUp.Prepare(MeetingChatScope.All, now, null).IsDraft);
    }

    [Fact]
    public void MentionsAreCaretAwareAndRemoveExactlyTheirText()
    {
        var mention = MeetingChatMentionQuery.Find("ask @lau", 8);
        Assert.Equal(new MeetingChatMentionQuery(4, 4, "lau"), mention);
        Assert.Equal("ask ", mention!.RemovingFrom("ask @lau"));
        Assert.Null(MeetingChatMentionQuery.Find("mail@x", 6));
        Assert.Null(MeetingChatMentionQuery.Find("@a\nb", 4));
        Assert.Null(MeetingChatMentionQuery.Find("@abc", 4, 1));
        Assert.Equal("ask @other", mention.RemovingFrom("ask @other"));
    }

    [Fact]
    public void ExportsRenumberCitationsAndGroupSourcesByMeeting()
    {
        MeetingChatCitation Cite(string key, string meeting) => new(key, meeting, "Launch", DateTimeOffset.UnixEpoch,
            MeetingChatSourceKind.Transcript, "line one\nline two", new(0, 1), "00:00:05", "r1");
        var turn = new MeetingChatTurn
        {
            Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), Ordinal = 0, Question = "When?", Scope = MeetingChatScope.All,
            State = MeetingChatTurnState.Completed, OriginalAnswer = "**Friday** [[S4]] then [[S7]]", Provider = "OpenAI", Model = "m",
            Citations = [Cite("S4", "a"), Cite("S7", "a")], Coverage = new(3, 1, true)
        };
        Assert.Equal("Friday [1] then [2]", MeetingChatExport.ClipboardText(turn));
        var session = new MeetingChatSession { Id = turn.SessionId, Title = "Launch chat", Scope = MeetingChatScope.All, CreatedAt = default, UpdatedAt = default };
        var markdown = MeetingChatExport.Markdown(turn, session, useEditedDraft: false);
        Assert.Contains("    line two", markdown);
        Assert.Contains("Partial recap. Evidence from 1 of 3 eligible saved meetings.", markdown);
        var group = Assert.Single(MeetingChatSourceGroup.Grouped(turn.Citations));
        Assert.Equal([1, 2], group.Passages.Select(item => item.Number));
        Assert.Equal("1 meeting · 2 passages", MeetingChatSourceGroup.Label(turn.Citations));
    }

    [Fact]
    public void WaitingPhrasesRotateOnlyDuringProviderWaits()
    {
        Assert.Equal("Searching meetings", MeetingChatWaitingStatus.Label("Searching meetings", TimeSpan.FromSeconds(30)));
        Assert.Equal("Thinking…", MeetingChatWaitingStatus.Label("Writing answer", TimeSpan.FromSeconds(1), ["Thinking", "Next"]));
        Assert.Equal("Next…", MeetingChatWaitingStatus.Label("Writing answer", TimeSpan.FromSeconds(6), ["Thinking", "Next"]));
    }

    [Fact]
    public void DocumentTargetsFindMovedPassagesAndTheirTranscriptBubble()
    {
        var citation = new MeetingChatCitation("S1", "m", "Launch", DateTimeOffset.UnixEpoch, MeetingChatSourceKind.Transcript,
            "Friday works", new(9, 12), null, "r1");
        var target = new MeetingChatDocumentTarget(citation, Guid.NewGuid());
        Assert.True(target.ShowsTranscript);
        Assert.Equal(new MeetingChatTextRange(9, 12), target.Locate("You: hi\n\nFriday works"));
        Assert.Equal(new MeetingChatTextRange(17, 12), target.Locate("Others: moved on\nFriday works"));
        Assert.Null(target.Locate("nothing here"));
        var transcript = "You: hi\n\nOthers: ok\r\nYou: Friday works";
        Assert.Equal(0, MeetingChatDocumentTarget.TranscriptMessageIndex(transcript, new(2, 1)));
        Assert.Equal(1, MeetingChatDocumentTarget.TranscriptMessageIndex(transcript, new(9, 1)));
        Assert.Equal(2, MeetingChatDocumentTarget.TranscriptMessageIndex(transcript, new(25, 3)));
        Assert.Null(MeetingChatDocumentTarget.TranscriptMessageIndex(transcript, new(99, 1)));
    }
}
