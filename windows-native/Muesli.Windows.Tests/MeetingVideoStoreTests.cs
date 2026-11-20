using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

// Ported from macOS MeetingVideoStoreTests (2f10b23d) on the Windows schema: GUID meetings, hard deletion.
public sealed class MeetingVideoStoreTests : IDisposable
{
    private readonly MuesliPersistenceStore _persistence = MuesliPersistenceStore.OpenInMemory();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muesli-video-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingVideoStore _store;

    public MeetingVideoStoreTests() => _store = new MeetingVideoStore(_persistence.Database, _root);

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Meeting()
    {
        var id = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;
        _persistence.Meetings.Upsert(new MeetingRecord { Id = id, Title = "Standup", CreatedAtUtc = at, UpdatedAtUtc = at });
        return id;
    }

    private MeetingVideoClip Clip(string meetingId, string? id = null)
    {
        id ??= Guid.NewGuid().ToString();
        var clip = new MeetingVideoClip(id, meetingId, DateTimeOffset.UtcNow, "Display 1", id + ".mp4", Duration: 4, Bytes: 3);
        File.WriteAllBytes(_store.FullPath(clip.Path), [1, 2, 3]);
        return clip;
    }

    [Fact]
    public void DeletingAMeetingQueuesItsClipsAndTheSweepRemovesTheFiles()
    {
        var meeting = Meeting();
        var clip = Clip(meeting);
        _store.PersistCompleted(clip);
        Assert.Equal([clip], _store.Clips(meeting));
        Assert.True(_store.HasMedia(meeting));

        Assert.True(_persistence.Meetings.Delete(meeting));
        Assert.Empty(_store.Clips());
        Assert.True(File.Exists(_store.FullPath(clip.Path)));
        _store.Maintain();
        Assert.False(File.Exists(_store.FullPath(clip.Path)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void DeletingAllMeetingsQueuesEveryClip()
    {
        var first = Clip(Meeting());
        var second = Clip(Meeting());
        _store.PersistCompleted(first);
        _store.PersistCompleted(second);

        _persistence.Meetings.DeleteAll();
        _store.Maintain();

        Assert.Empty(_store.Clips());
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void SavingForAMissingMeetingFailsAndPathsStayInsideTheRoot()
    {
        Assert.Throws<PersistenceException>(() => _store.Save([Clip(Guid.NewGuid().ToString())]));
        Assert.Throws<InvalidOperationException>(() => _store.FullPath(@"..\escape.mp4"));
        Assert.Throws<InvalidOperationException>(() => _store.FullPath(@"C:\escape.mp4"));
    }

    [Fact]
    public void RecoveryPublishesOnlyPlayableUnfinishedClipsOfLiveMeetingsAndSkipsActiveWriters()
    {
        var meeting = Meeting();
        var active = Clip(meeting);
        var playable = Clip(meeting);
        var broken = Clip(meeting);
        var orphan = Clip(Meeting());
        foreach (var clip in new[] { active, playable, broken, orphan }) _store.BeginRecording(clip);
        _store.EndRecording(playable); _store.EndRecording(broken); _store.EndRecording(orphan);
        _persistence.Meetings.Delete(orphan.MeetingId);

        _store.Maintain(path => path == _store.FullPath(playable.Path) ? 9.5 : null);

        var saved = Assert.Single(_store.Clips(meeting));
        Assert.Equal(playable.Id, saved.Id);
        Assert.Equal(9.5, saved.Duration);
        Assert.True(File.Exists(_store.FullPath(active.Path + ".recording.json")));
        Assert.True(File.Exists(_store.FullPath(broken.Path + ".recording.json")));
        Assert.False(File.Exists(_store.FullPath(orphan.Path)));
    }

    [Fact]
    public void DeletingClipsIncludesJournalsNotYetSaved()
    {
        var meeting = Meeting();
        var saved = Clip(meeting);
        var journaled = Clip(meeting);
        _store.PersistCompleted(saved);
        _store.BeginRecording(journaled);
        _store.EndRecording(journaled);

        _store.Delete(new HashSet<string> { saved.Id, journaled.Id });

        Assert.Empty(_store.Clips());
        Assert.False(_store.HasMedia(meeting));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }
}
