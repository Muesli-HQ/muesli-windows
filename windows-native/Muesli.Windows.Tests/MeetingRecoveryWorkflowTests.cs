using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class MeetingRecoveryWorkflowTests
{
    [Fact]
    public void DiscoverMapsFinalizingJournalsToRecoverableInterruptionWithoutDeletingAudio()
    {
        using var directory = new TestDirectory();
        var persistence = CreatePersistence(directory);
        var workflow = new MeetingRecoveryWorkflow(persistence);
        var journal = persistence.Create("meet_retry", "Retry", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 3200).ToArray());
        journal = persistence.AppendPart(journal, MeetingAudioChannel.Microphone, source) with
        {
            State = MeetingSessionState.Finalizing
        };
        persistence.Save(journal);

        var recoverable = Assert.Single(workflow.Discover());
        Assert.Equal(MeetingSessionState.RecoverableInterruption, recoverable.Journal.State);
        Assert.True(recoverable.Journal.RecoveredFromInterruption);
        Assert.True(Directory.Exists(persistence.GetSessionDirectory(journal.SessionId)));
    }

    [Fact]
    public void HydrateRebuildsUnifiedLiveOwnershipAndAddsTheRecoveryWarning()
    {
        using var directory = new TestDirectory();
        var persistence = CreatePersistence(directory);
        var journal = persistence.Create(
            "meet_live",
            "Live",
            DateTimeOffset.UnixEpoch,
            "mic",
            "parakeet-v3",
            true,
            null,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal) with
        {
            LiveTranscriptSegments =
            [
                new LiveTranscriptSegment("seg", LiveTranscriptChannel.Microphone, 0, 1600, "hello")
            ],
            LiveDroppedPacketCount = 4
        };
        var recovery = new RecoverableMeetingSession(journal, 1, 0, []);

        var hydration = MeetingRecoveryWorkflow.Hydrate(recovery);

        Assert.True(hydration.Journal.RecoveredFromInterruption);
        Assert.Contains(MeetingRecoveryWorkflow.RecoveredSessionWarning, hydration.Warnings);
        Assert.NotNull(hydration.LiveResult);
        Assert.Equal(LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, hydration.LiveResult!.OwnershipMode);
        Assert.Equal(4, hydration.LiveResult.DroppedPackets);
        Assert.Equal(2, hydration.LiveResult.Gaps.Count(gap => gap.Reason == "recoverable-interruption"));
    }

    [Fact]
    public void PreserveRecoverableKeepsAudioAndFailWithoutAudioDoesNotDeleteItEither()
    {
        using var directory = new TestDirectory();
        var persistence = CreatePersistence(directory);
        var workflow = new MeetingRecoveryWorkflow(persistence);
        var journal = persistence.Create("meet_preserve", "Preserve", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var transitions = new List<MeetingSessionTrigger>();
        var states = new List<MeetingSessionState>();
        var host = new MeetingRecoveryHost(
            Transition: (trigger, _) => transitions.Add(trigger),
            CanApply: _ => true,
            AddWarning: _ => { },
            SaveState: (state, _) => states.Add(state),
            LogDiagnostic: _ => { });

        workflow.PreserveRecoverable(host, "finalization-cancelled");
        workflow.FailWithoutAudio(host, "application shutdown", "application-shutdown");

        Assert.Equal([MeetingSessionTrigger.Interrupt, MeetingSessionTrigger.Fail], transitions);
        Assert.Equal([MeetingSessionState.RecoverableInterruption, MeetingSessionState.Failed], states);
        Assert.True(Directory.Exists(persistence.GetSessionDirectory(journal.SessionId)));
    }

    [Fact]
    public void CancelAndDiscardDeleteWorkingJournalsAndLeaveOwnedRecordings()
    {
        using var directory = new TestDirectory();
        var persistence = CreatePersistence(directory);
        var workflow = new MeetingRecoveryWorkflow(persistence);
        var cancelJournal = persistence.Create("meet_cancel", "Cancel", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var discardJournal = persistence.Create("meet_discard", "Discard", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)700, 2400).ToArray());
        discardJournal = persistence.AppendPart(discardJournal, MeetingAudioChannel.Microphone, source);
        var owned = persistence.BuildFinalTracks(discardJournal);
        var cancelDir = persistence.GetSessionDirectory(cancelJournal.SessionId);
        var discardDir = persistence.GetSessionDirectory(discardJournal.SessionId);

        workflow.TryDeleteWorkingSession(cancelJournal.SessionId, MeetingAudioCleanupReason.Cancelled, "cancel failed.");
        workflow.DiscardRecoverable(discardJournal.SessionId);

        Assert.False(Directory.Exists(cancelDir));
        Assert.False(Directory.Exists(discardDir));
        Assert.True(File.Exists(owned.MicrophonePath));
    }

    private static MeetingPersistenceBoundary CreatePersistence(TestDirectory directory) =>
        new(new MeetingSessionJournalStore(directory.Path));

    private static void WritePcmWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }
}
