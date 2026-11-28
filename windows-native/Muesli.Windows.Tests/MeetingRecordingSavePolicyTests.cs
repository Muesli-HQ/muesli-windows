using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingRecordingSavePolicyTests
{
    [Fact]
    public void ANewInstallDoesNotKeepRecordingsLikeMacOS()
    {
        Assert.Equal(MeetingRecordingSavePolicies.Never, MeetingRecordingSavePolicies.Resolve(new MuesliSettings()));
    }

    [Theory]
    [InlineData(true, "always")]
    [InlineData(false, "never")]
    public void SettingsFromBeforeTheChoiceKeepTheirSwitch(bool saved, string expected)
    {
        Assert.Equal(expected, MeetingRecordingSavePolicies.Resolve(new MuesliSettings { SaveMeetingRecordings = saved }));
    }

    [Theory]
    [InlineData("never", false)]
    [InlineData("prompt", true)]
    [InlineData("always", true)]
    public void AskKeepsTheFilesUntilTheUserAnswers(string policy, bool retains)
    {
        var resolved = MeetingRecordingSavePolicies.Resolve(new MuesliSettings { MeetingRecordingSavePolicy = policy, SaveMeetingRecordings = true });
        Assert.Equal(policy, resolved);
        Assert.Equal(retains, MeetingRecordingSavePolicies.RetainsAtStop(resolved));
    }

    [Fact]
    public void ThePolicySurvivesASaveAndLoad()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(Path.Combine(directory.Path, "settings.json"), new InMemorySecretStore());
        store.Save(store.Load() with { MeetingRecordingSavePolicy = MeetingRecordingSavePolicies.Prompt });

        Assert.Equal(MeetingRecordingSavePolicies.Prompt, MeetingRecordingSavePolicies.Resolve(store.Load()));
    }
}
