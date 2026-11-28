using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingDetectionMuteTests
{
    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("msedge", "msedge")]
    [InlineData("ms-teams", "teams")]
    [InlineData("CptHost", "zoom")]
    [InlineData("Zoom", "zoom")]
    [InlineData("webexmta", "webex")]
    [InlineData("CiscoCollabHost", "webex")]
    [InlineData("atmgr", "webex")]
    [InlineData("Amazon Chime", "chime")]
    public void DetectedProcessesMapToTheirListedApp(string process, string app)
    {
        Assert.Equal(app, MeetingDetectionApps.ForProcess(process)?.Id);
    }

    [Fact]
    public void OnlyTheMutedAppIsSilenced()
    {
        string[] muted = ["chrome"];

        Assert.True(MeetingDetectionApps.IsMuted(muted, "chrome"));
        Assert.False(MeetingDetectionApps.IsMuted(muted, "msedge"));
        Assert.False(MeetingDetectionApps.IsMuted(muted, "notepad"));
        Assert.False(MeetingDetectionApps.IsMuted(null, "chrome"));
    }

    [Fact]
    public void NothingIsMutedByDefaultAndTheListSurvivesASaveAndLoad()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"), new InMemorySecretStore());
        Assert.Empty(store.Load().MutedMeetingDetectionApps);

        store.Save(store.Load() with { MutedMeetingDetectionApps = ["chrome", "zoom"] });

        Assert.Equal(["chrome", "zoom"], store.Load().MutedMeetingDetectionApps);
    }
}
