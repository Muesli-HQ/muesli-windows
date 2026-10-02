using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class MeetingBehaviorParityTests
{
    [Fact]
    public async Task BatchMeetingModelPublishesBothSpeakersBeforeStopAndKeepsTheTail()
    {
        var first = new TaskCompletionSource<LiveTranscriptSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var preview = new MeetingRollingTranscriber(_ => Task.FromResult(new TranscriptionResult($"caption {Interlocked.Increment(ref calls)}")));
        LiveTranscriptSnapshot? latest = null;
        preview.SnapshotChanged += (_, snapshot) => { latest = snapshot; first.TrySetResult(snapshot); };

        preview.Feed(new(LiveTranscriptChannel.Microphone, new float[16 * 16000], 16000));
        var live = await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("You: caption 1", live.CommittedText);
        Assert.Equal(16000, Assert.Single(live.Committed).StartSample);
        preview.Feed(new(LiveTranscriptChannel.System, new float[16000], 0));
        await preview.FinishAsync();

        Assert.NotNull(latest);
        Assert.Equal(3, latest.Committed.Count);
        Assert.Contains("Others:", latest.CommittedText);
        Assert.Equal(LiveTranscriptChannel.System, latest.Committed[0].Channel);
        Assert.Equal(17 * 16000, latest.Committed.Max(segment => segment.EndSample));
    }

    [Fact]
    public async Task CancellingPreviewSuppressesInFlightResultsAndDoesNotDecodeTheTail()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = 0;
        var preview = new MeetingRollingTranscriber(_ => { entered.TrySetResult(); return release.Task; });
        preview.SnapshotChanged += (_, _) => publications++;
        preview.Feed(new(LiveTranscriptChannel.Microphone, new float[16 * 16000], 0));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = preview.FinishAsync(cancel: true);
        release.SetResult(new TranscriptionResult("must not reach the next meeting"));
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, publications);
    }

    [Fact]
    public async Task PreviewBacklogIsBoundedAndDecodeFailuresDoNotStopCapture()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var failures = 0;
        LiveTranscriptSnapshot? latest = null;
        var preview = new MeetingRollingTranscriber(_ => Interlocked.Increment(ref calls) == 1
            ? WaitAndFail() : Task.FromResult(new TranscriptionResult("continued")));
        async Task<TranscriptionResult> WaitAndFail()
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("recognition unavailable");
        }
        preview.Failed += (_, _) => failures++;
        preview.SnapshotChanged += (_, snapshot) => latest = snapshot;
        preview.Feed(new(LiveTranscriptChannel.Microphone, new float[16 * 16000], 0));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 1; i < 20; i++)
            preview.Feed(new(LiveTranscriptChannel.Microphone, new float[16 * 16000], i * 16 * 16000L));
        release.SetResult(new TranscriptionResult("ignored"));
        await preview.FinishAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, failures);
        Assert.InRange(calls, 2, 6); // One in flight, four queued, one final tail.
        Assert.True(latest!.DroppedPackets > 0);
        Assert.Contains("continued", latest.CommittedText);
    }

    [Fact]
    public void FinalizationAndRecoveryPreserveWrittenNotesAndManualTitle()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        var notes = "    code block\n\n- follow up\n";
        var active = MeetingNotesComposer.ApplyManualNotes(new PersistedMeeting
        {
            Id = "meet_active", Title = "Chosen title", TitleIsManual = true, FolderId = "folder",
            SessionState = MeetingSessionState.Recording
        }, notes);
        store.SaveMeetings([active]);
        var recorded = new PersistedMeeting { Id = active.Id, Title = "Generated title", Transcript = "Actual speech" };
        var merged = MeetingNotesComposer.ApplyRecordedMeeting(recorded, Assert.Single(store.LoadMeetings()));
        var final = MeetingNotesComposer.ApplyResummarization(merged, "Generated summary", "Standard Meeting Notes", "Another title");
        Assert.Equal(notes, final.ManualNotes);
        Assert.Equal("Chosen title", final.Title);
        Assert.Equal("folder", final.FolderId);
        Assert.Equal("Actual speech", final.Transcript);
        Assert.Throws<ArgumentException>(() => MeetingNotesComposer.ApplyRecordedMeeting(recorded with { Id = "other" }, active));
    }

    [Fact]
    public void WrittenNotesTakePriorityForTheMeetingTopicWithoutReplacingAManualTitle()
    {
        var generated = MeetingTitleService.Generate("You: Hello everyone.", DateTime.Now, "Quick Note", "Launch review and next steps.");
        Assert.Equal("Launch review and next steps", generated);
        Assert.Equal("My title", MeetingTitleService.Resolve("My title", true, generated));
    }

    [Fact]
    public void KeepingNotesAfterDiscardRoundTripsThroughWindowsSqliteWithoutInventingSpeech()
    {
        using var directory = new TestDirectory();
        Assert.True(new PersistenceCutover(directory.Path).EnsureMigrated().Succeeded);
        using var history = SqliteLibraryHistoryAdapter.Open(directory.Path);
        var notesOnly = new PersistedMeeting
        {
            Id = "meet_notes_only", Title = "Written notes", ManualNotes = "    Keep indentation\n- Follow up\n",
            CreatedAt = DateTime.Now, SessionState = MeetingSessionState.NoteOnly
        };
        history.SaveMeetings([notesOnly]);
        var loaded = Assert.Single(history.LoadMeetings());
        Assert.Equal(MeetingSessionState.NoteOnly, loaded.SessionState);
        Assert.Equal(notesOnly.ManualNotes, loaded.ManualNotes);
        Assert.Empty(loaded.Transcript);
        Assert.Null(loaded.MicrophoneAudioPath);
        Assert.Null(loaded.SystemAudioPath);
    }

    [Fact]
    public void SignalLossWaitsForSpeechToQuietAndRespectsDismissal()
    {
        var now = DateTimeOffset.UnixEpoch;
        var tracker = new MeetingAutoStopTracker(MeetingRecordingStartOrigin.DetectedMeeting, "room");
        tracker.Observe("room", now);
        tracker.NoteTranscriptActivity(now);
        Assert.False(tracker.Observe(null, now.AddSeconds(1)));
        Assert.False(tracker.Observe(null, now.AddSeconds(21)));
        Assert.False(tracker.Observe(null, now.AddSeconds(44)));
        Assert.True(tracker.Observe(null, now.AddSeconds(45)));
        tracker.SuppressWarning();
        Assert.False(tracker.Observe(null, now.AddSeconds(60)));
        tracker.Observe("room", now.AddSeconds(61));
        tracker.DismissWarning();
        tracker.Observe(null, now.AddSeconds(62));
        tracker.Observe(null, now.AddSeconds(80));
        Assert.False(tracker.Observe(null, now.AddHours(1)));
    }

    [Fact]
    public void LostSignalNotificationStopsOnlyOnAnExplicitUserAction()
    {
        var request = new MeetingNotificationRequest("lost", MeetingNotificationKind.SignalLost,
            "Meeting signal lost", "Recording continues", "Meeting", "", "", "", "Stop Transcribing", null);
        var suppression = new MeetingNotificationSuppressionState();
        Assert.True(suppression.Evaluate(request, false, isRecording: true, isBusy: false).Show);
        Assert.False(suppression.Evaluate(request, true, isRecording: false, isBusy: false).Show);
        Assert.Equal(MeetingNotificationAction.StopTranscribing, request.SingleAction);
        Assert.Equal(MeetingNotificationAction.StartTranscribing,
            (request with { Kind = MeetingNotificationKind.ActiveDetected }).SingleAction);
        Assert.False(request.HasJoinActions);
    }

    [Fact]
    public void IdleNativeClientsAndUnrelatedMicrophonesDoNotCountAsMeetings()
    {
        var native = new MeetingPresenceSnapshot(MeetingEvidenceStrength.Strong, false, false, "teams",
            RequiresMediaActivity: true);
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(native));
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(native with { MicrophoneInUse = true }));
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(native with { CandidateMicrophoneInUse = true }));
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(native with
            { MicrophoneInUse = true, CameraInUse = true, IsForeground = true }));
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(native with { RequiresMediaActivity = false }));
        var duplex = native with { RequiresDuplexAudio = true, CandidateMicrophoneInUse = true };
        Assert.False(MeetingCandidateResolver.QualifiesAsMeeting(duplex));
        Assert.True(MeetingCandidateResolver.QualifiesAsMeeting(duplex with { CandidateOutputInUse = true }));
    }
}
