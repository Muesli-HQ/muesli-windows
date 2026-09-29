using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

/// <summary>
/// Locks the meeting coordinator's public contract and durable-save sequencing before capture
/// and finalization are split. These must keep passing after extraction without UI involvement.
/// </summary>
public sealed class MeetingCoordinatorCharacterizationTests
{
    [Fact]
    public void PublicLifecycleSurfaceRemainsTheUiContract()
    {
        var type = typeof(MeetingRecordingCoordinator);
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.StartAsync)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.StopAsync)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.FinalizeRecoverableAsync)));
        Assert.True(type.GetMethods().Count(method => method.Name == nameof(MeetingRecordingCoordinator.SuspendAsync)) >= 2);
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.ResumeAsync)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.PreserveForShutdownAsync)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.CancelAsync)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.AcknowledgePersisted)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.DiscardRecoverable)));
        Assert.NotNull(type.GetMethod(nameof(MeetingRecordingCoordinator.DiscoverRecoverableSessions)));
        Assert.NotNull(type.GetEvent(nameof(MeetingRecordingCoordinator.StateChanged)));
        Assert.NotNull(type.GetEvent(nameof(MeetingRecordingCoordinator.HealthChanged)));
        Assert.NotNull(type.GetEvent(nameof(MeetingRecordingCoordinator.LevelChanged)));
        Assert.NotNull(type.GetEvent(nameof(MeetingRecordingCoordinator.LiveTranscriptChanged)));
        Assert.NotNull(type.GetEvent(nameof(MeetingRecordingCoordinator.LiveTranscriptionFailed)));
    }

    [Fact]
    public async Task IdleCoordinatorIsDeterministicForStopCancelSuspendResumeAndShutdown()
    {
        using var directory = new TestDirectory();
        using var coordinator = CreateCoordinator(directory);

        Assert.Equal(MeetingSessionState.Idle, coordinator.State);
        Assert.False(coordinator.IsRecording);
        Assert.False(coordinator.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StopAsync("title", retainRecording: true));
        await coordinator.CancelAsync();
        await coordinator.SuspendAsync();
        await coordinator.ResumeAsync();
        await coordinator.PreserveForShutdownAsync();
        Assert.Equal(MeetingSessionState.Idle, coordinator.State);
        Assert.Empty(coordinator.DiscoverRecoverableSessions());
    }

    [Fact]
    public void AcknowledgePersistedRejectsAMismatchedMeetingIdWithoutDeletingSessions()
    {
        using var directory = new TestDirectory();
        using var coordinator = CreateCoordinator(directory);
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_keep", "Keep", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);

        var mismatch = Assert.Throws<InvalidOperationException>(() => coordinator.AcknowledgePersisted("meet_other"));
        Assert.Contains("does not match", mismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(store.GetSessionDirectory(journal.SessionId)));
    }

    [Fact]
    public void DiscardRecoverableDeletesTheWorkingSessionAndLeavesOwnedRecordings()
    {
        using var directory = new TestDirectory();
        using var coordinator = CreateCoordinator(directory);
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_discard", "Discard", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("source.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 3200).ToArray());
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, source);
        var owned = store.BuildFinalTracks(journal);
        Assert.NotNull(owned.MicrophonePath);
        var workingSession = store.GetSessionDirectory(journal.SessionId);

        coordinator.DiscardRecoverable(journal.SessionId);

        Assert.False(Directory.Exists(workingSession));
        Assert.True(File.Exists(owned.MicrophonePath));
    }

    [Fact]
    public void RetainedAudioIsCopiedOutOfTheWorkingSessionBeforeJournalCleanup()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_retain", "Retain", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)1100, 4800).ToArray());
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, source);

        var tracks = store.BuildFinalTracks(journal);
        var workingSession = store.GetSessionDirectory(journal.SessionId);
        store.DeleteSession(journal.SessionId);

        Assert.NotNull(tracks.MicrophonePath);
        Assert.True(File.Exists(tracks.MicrophonePath));
        Assert.Contains(Path.Combine("recordings", "meet_retain"), tracks.MicrophonePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(workingSession));
    }

    [Fact]
    public void NonRetainedFinalTracksStayInTheWorkingSessionAndAreRemovedWithIt()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_ephemeral", "Ephemeral", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", false, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)700, 3200).ToArray());
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, source);

        var tracks = store.BuildFinalTracks(journal);
        Assert.NotNull(tracks.MicrophonePath);
        Assert.Contains("in-progress", tracks.MicrophonePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("microphone-final.wav", tracks.MicrophonePath, StringComparison.OrdinalIgnoreCase);

        store.DeleteSession(journal.SessionId);
        Assert.False(File.Exists(tracks.MicrophonePath));
    }

    [Fact]
    public void FinalizingJournalsRemainRecoverableUntilPersistenceIsAcknowledged()
    {
        using var directory = new TestDirectory();
        using var coordinator = CreateCoordinator(directory);
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_finalizing", "Finalizing", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)800, 2400).ToArray());
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, source) with
        {
            State = MeetingSessionState.Finalizing
        };
        store.Save(journal);

        var recoverable = Assert.Single(coordinator.DiscoverRecoverableSessions());
        Assert.Equal(MeetingSessionState.RecoverableInterruption, recoverable.Journal.State);
        Assert.True(recoverable.Journal.RecoveredFromInterruption);
        Assert.Equal(1, recoverable.MicrophonePartCount);
    }

    [Fact]
    public void JournalSchemaStaysAtVersion3()
    {
        Assert.Equal(3, MeetingSessionJournal.CurrentSchemaVersion);
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_schema", "Schema", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        Assert.Equal(MeetingSessionJournal.CurrentSchemaVersion, journal.SchemaVersion);
    }

    private static MeetingRecordingCoordinator CreateCoordinator(TestDirectory directory) =>
        new(
            new NativeTranscriptionClient(
                TranscriptionModelCatalog.GetRequired(TranscriptionModelCatalog.DefaultModelId),
                new StubTranscriptionSessionFactory()),
            logService: null,
            journalStore: new MeetingSessionJournalStore(directory.Path));

    private static void WritePcmWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }

    private sealed class StubTranscriptionSessionFactory : ITranscriptionModelSessionFactory
    {
        public ITranscriptionModelSession Create(TranscriptionModelDefinition model) => new StubTranscriptionSession();
    }

    private sealed class StubTranscriptionSession : ITranscriptionModelSession
    {
        public Task<ModelOperationResult> InitializeAsync() =>
            Task.FromResult(new ModelOperationResult("stub"));

        public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
            Task.FromResult(new TranscriptionResult(""));

        public Task<TranscriptionResult> TranscribeFileAsync(
            string title,
            string filePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptionResult(""));

        public void Dispose()
        {
        }
    }
}
