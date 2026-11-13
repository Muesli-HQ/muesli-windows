using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingDiskSpaceSafetyPolicyTests
{
    [Fact]
    public void CaptureVolumeThresholdCanStopBeforeTheDriveIsFull()
    {
        using var directory = new TestDirectory();

        Assert.False(MeetingDiskSpaceSafetyPolicy.IsCriticallyLow(directory.Path, 0));
        Assert.True(MeetingDiskSpaceSafetyPolicy.IsCriticallyLow(directory.Path, long.MaxValue));
    }

    [Fact]
    public void UnreadableDrivePathDoesNotStopAHealthyRecording()
    {
        Assert.False(MeetingDiskSpaceSafetyPolicy.IsCriticallyLow("\0"));
    }
}
