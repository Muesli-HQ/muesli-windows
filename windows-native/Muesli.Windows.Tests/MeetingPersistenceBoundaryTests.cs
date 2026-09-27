using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class MeetingPersistenceBoundaryTests
{
    [Fact]
    public void WorkingAudioCannotBeDeletedBeforeDurablePersistence()
    {
        using var directory = new TestDirectory();
        var boundary = CreateBoundary(directory);
        var journal = boundary.Create("meet_keep_audio", "Keep", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)900, 3200).ToArray());
        boundary.AppendPart(journal, MeetingAudioChannel.Microphone, source);

        var blocked = Assert.Throws<InvalidOperationException>(() =>
            boundary.DeleteWorkingSession(journal.SessionId, (MeetingAudioCleanupReason)int.MaxValue));
        Assert.Contains("not durably persisted", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(boundary.GetSessionDirectory(journal.SessionId)));
    }

    [Theory]
    [InlineData(nameof(MeetingAudioCleanupReason.DurableMeetingPersisted))]
    [InlineData(nameof(MeetingAudioCleanupReason.Cancelled))]
    [InlineData(nameof(MeetingAudioCleanupReason.Discarded))]
    [InlineData(nameof(MeetingAudioCleanupReason.FailedStartWithoutAudio))]
    public void AllowedCleanupReasonsDeleteOnlyTheWorkingSession(string reasonName)
    {
        var reason = Enum.Parse<MeetingAudioCleanupReason>(reasonName);
        using var directory = new TestDirectory();
        var boundary = CreateBoundary(directory);
        var journal = boundary.Create($"meet_{reason}", "Session", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WritePcmWav(source, Enumerable.Repeat((short)1100, 4800).ToArray());
        journal = boundary.AppendPart(journal, MeetingAudioChannel.Microphone, source);
        var owned = boundary.BuildFinalTracks(journal);
        var workingSession = boundary.GetSessionDirectory(journal.SessionId);

        boundary.DeleteWorkingSession(journal.SessionId, reason);

        Assert.False(Directory.Exists(workingSession));
        Assert.True(File.Exists(owned.MicrophonePath));
    }

    [Fact]
    public void RetainedOwnedPathsAreCopiedOutAndNonRetainedPathsStayInSession()
    {
        using var directory = new TestDirectory();
        var retain = CreateBoundary(directory);
        var retained = retain.Create("meet_owned", "Owned", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("owned.wav");
        WritePcmWav(source, Enumerable.Repeat((short)800, 2400).ToArray());
        retained = retain.AppendPart(retained, MeetingAudioChannel.Microphone, source);
        var retainedTracks = retain.BuildFinalTracks(retained);
        Assert.Equal(retainedTracks.MicrophonePath, MeetingPersistenceBoundary.OwnedMicrophonePath(true, retainedTracks));
        Assert.Null(MeetingPersistenceBoundary.OwnedMicrophonePath(false, retainedTracks));
        Assert.Contains(Path.Combine("recordings", "meet_owned"), retainedTracks.MicrophonePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HasAudioRequiresAtLeastOneCapturedPart()
    {
        using var directory = new TestDirectory();
        var boundary = CreateBoundary(directory);
        var empty = boundary.Create("meet_empty", "Empty", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        Assert.False(MeetingPersistenceBoundary.HasAudio(null));
        Assert.False(MeetingPersistenceBoundary.HasAudio(empty));

        var source = directory.File("part.wav");
        WritePcmWav(source, Enumerable.Repeat((short)500, 1600).ToArray());
        var withAudio = boundary.AppendPart(empty, MeetingAudioChannel.System, source);
        Assert.True(MeetingPersistenceBoundary.HasAudio(withAudio));
    }

    private static MeetingPersistenceBoundary CreateBoundary(TestDirectory directory) =>
        new(new MeetingSessionJournalStore(directory.Path));

    private static void WritePcmWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }
}
