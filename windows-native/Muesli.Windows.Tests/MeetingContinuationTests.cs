using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class MeetingContinuationTests
{
    [Fact]
    public void ResumeAppendsToTheSameDocumentAndRetainsAllAudioAndWriting()
    {
        var prior = new PersistedMeeting { Id = "old", Title = "Chosen title", TitleIsManual = true,
            CreatedAt = new DateTime(2020, 1, 1), DurationMs = 1000, Transcript = "Prior speech", Summary = "Old notes",
            ManualNotes = "My writing", FolderId = "folder", SourcePath = "old-mic.wav; old-system.wav",
            MicrophoneAudioPath = "old-mic.wav", SystemAudioPath = "old-system.wav" };
        var captured = new PersistedMeeting { Id = "new-session", DurationMs = 2000, Transcript = "New speech",
            MicrophoneAudioPath = "new-mic.wav", SessionState = MeetingSessionState.Completed };
        var merged = MeetingContinuation.AppendRecording(prior, captured);
        Assert.Equal("old", merged.Id);
        Assert.Equal(prior.CreatedAt, merged.CreatedAt);
        Assert.Equal("Prior speech\n\n— Resumed —\n\nNew speech", merged.Transcript);
        Assert.Equal(3000, merged.DurationMs);
        Assert.Equal("My writing", merged.ManualNotes);
        Assert.Equal("Chosen title", merged.Title);
        Assert.Equal("folder", merged.FolderId);
        Assert.Equal("old-mic.wav; old-system.wav; new-mic.wav", merged.SourcePath);
        Assert.Equal(merged, MeetingContinuation.AppendRecording(prior, captured) with { HealthWarnings = merged.HealthWarnings });
        var empty = MeetingContinuation.AppendRecording(prior, captured with { Transcript = " \n" });
        Assert.Equal(prior.Transcript, empty.Transcript);
        Assert.Equal(prior.Summary, empty.Summary);
        Assert.True(MeetingContinuation.CanContinue(prior));
        Assert.False(MeetingContinuation.CanContinue(prior with { SessionState = MeetingSessionState.NoteOnly }));
    }

    [Fact]
    public void FollowUpsDoNotStackPrefixesOrCarryUnboundedContext()
    {
        Assert.Equal("Follow-up: Planning", MeetingContinuation.FollowUpTitle(" Follow-up: Follow-up: Planning "));
        Assert.Equal("Follow-up meeting", MeetingContinuation.FollowUpTitle("Follow-up:"));
        Assert.Null(MeetingContinuation.CarriedNotes(new PersistedMeeting()));
        var context = MeetingContinuation.CarriedNotes(new PersistedMeeting { Summary = new string('a', 6100) })!;
        Assert.StartsWith(new string('a', 6000), context);
        Assert.EndsWith("[…previous notes truncated]", context);
    }

    [Fact]
    public void ResumeBaselineSurvivesTheJournalRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"muesli-resume-{Guid.NewGuid():N}");
        try
        {
            var store = new MeetingSessionJournalStore(directory);
            var journal = store.Create("capture", "Title", DateTimeOffset.UtcNow, "default", "model", true, null)
                with { ResumedMeeting = new PersistedMeeting { Id = "prior", Transcript = "Saved speech", ManualNotes = "Writing", DurationMs = 1200 } };
            store.Save(journal);
            var restored = new AtomicJsonFile().Load<MeetingSessionJournal?>(Path.Combine(store.GetSessionDirectory("capture"), "session.json"), null).Value;
            Assert.NotNull(restored);
            Assert.Equal("prior", restored.ResumedMeeting?.Id);
            Assert.Equal("Saved speech", restored.ResumedMeeting?.Transcript);
            Assert.Equal(1200, restored.ResumedMeeting?.DurationMs);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResumedPlaybackIsOwnedByTheOriginalMeetingAndDoesNotPrependAudioBeforeRecognition()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"muesli-resume-audio-{Guid.NewGuid():N}");
        try
        {
            var store = new MeetingSessionJournalStore(directory);
            var priorPath = Path.Combine(directory, "prior.wav");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(priorPath, DictationRollingTranscriber.ToWav(new float[16000]));
            var journal = store.Create("new_capture", "Title", DateTimeOffset.UtcNow, "default", "model", true, null)
                with { ResumedMeeting = new PersistedMeeting { Id = "old_meeting", MicrophoneAudioPath = priorPath } };
            var capturedPath = Path.Combine(store.GetSessionDirectory(journal.SessionId), "capture.wav");
            File.WriteAllBytes(capturedPath, DictationRollingTranscriber.ToWav(new float[32000]));
            journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, capturedPath);
            var recognition = store.BuildFinalTracks(journal);
            using (var reader = new NAudio.Wave.WaveFileReader(recognition.MicrophonePath!)) Assert.Equal(2d, reader.TotalTime.TotalSeconds);
            var playback = store.BuildResumedPlaybackTracks(journal, recognition.MicrophonePath, recognition.SystemPath);
            Assert.True(new CaptureStorageService(directory).IsOwnedMeetingAudioPath("old_meeting", playback.MicrophonePath!));
            using (var reader = new NAudio.Wave.WaveFileReader(playback.MicrophonePath!)) Assert.Equal(3d, reader.TotalTime.TotalSeconds);
            var retry = store.BuildResumedPlaybackTracks(journal, recognition.MicrophonePath, recognition.SystemPath);
            using (var reader = new NAudio.Wave.WaveFileReader(retry.MicrophonePath!)) Assert.Equal(3d, reader.TotalTime.TotalSeconds);
            using (var reader = new NAudio.Wave.WaveFileReader(priorPath)) Assert.Equal(1d, reader.TotalTime.TotalSeconds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void FollowUpLinksSurviveOrdinaryHistorySavesAndDeletingThePredecessor()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"muesli-thread-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            using var adapter = SqliteLibraryHistoryAdapter.Open(directory);
            var parent = new PersistedMeeting { Id = "parent", Title = "Prior meeting", CreatedAt = DateTime.Now };
            var child = new PersistedMeeting { Id = "child", Title = "Follow-up: Prior meeting", CreatedAt = DateTime.Now };
            adapter.SaveMeetings([parent, child]);
            adapter.Store.Meetings.UpsertFollowUp(new FollowUpRecord { Id = "meeting_thread_child", MeetingId = parent.Id,
                LinkedMeetingId = child.Id, Text = child.Title, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            adapter.SaveMeetings([parent, child with { ManualNotes = "Fresh writing" }]);
            Assert.Equal(parent.Id, Assert.Single(adapter.Store.Meetings.ListFollowUpsLinkedTo(child.Id)).MeetingId);
            adapter.SaveMeetings([child], afterExplicitDeletion: true);
            Assert.Empty(adapter.Store.Meetings.ListFollowUpsLinkedTo(child.Id));
            Assert.Equal(child.Title, Assert.Single(adapter.LoadMeetings()).Title);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("one\r\ntwo\r\nthree", 1, 7, "- ", "- one\r\n- two\r")]
    [InlineData("**word**", 0, 8, "**", "word")]
    [InlineData("one\ntwo\nthree", 0, 4, "- [ ] ", "- [ ] one")]
    [InlineData("word", 0, 4, "_", "_word_")]
    public void NoteFormattingKeepsSelectionsAndLineEndings(string text, int start, int length, string marker, string replacement)
    {
        Assert.Equal(replacement, MeetingNoteFormatting.Apply(text, start, length, marker).Replacement);
    }

    [Fact]
    public async Task SpeechBoundariesPublishEarlyAndSilenceNeverReachesTheRecognizer()
    {
        var vad = new BoundaryVad();
        var decoded = new TaskCompletionSource<LiveTranscriptSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var preview = new MeetingRollingTranscriber(_ => Task.FromResult(new TranscriptionResult($"speech {++calls}")), vad: vad);
        preview.SnapshotChanged += (_, snapshot) => decoded.TrySetResult(snapshot);
        preview.Feed(new(LiveTranscriptChannel.Microphone, new float[16000], 32000));
        var live = await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(32000, Assert.Single(live.Committed).StartSample);
        preview.Feed(new(LiveTranscriptChannel.System, new float[32 * 16000], 0));
        await preview.FinishAsync();
        Assert.Equal(1, calls);
        Assert.True(vad.Disposed);
    }

    private sealed class BoundaryVad : ILiveVad
    {
        private int _samples;
        public bool Disposed;
        public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
        {
            if (channel != LiveTranscriptChannel.Microphone) return [];
            var prior = _samples;
            _samples += samples.Length;
            return prior < 8000 && _samples >= 8000 ? [(0, 7000)] : [];
        }
        public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel) => [];
        public void Dispose() => Disposed = true;
    }
}
