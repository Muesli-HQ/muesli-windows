using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class TrayMenuModelTests
{
    private static string[] Labels(IEnumerable<TrayMenuEntry> entries) =>
        entries.Select(entry => entry.IsSeparator ? "-" : entry.Label).ToArray();

    [Fact]
    public void IdleMenuOffersStartActionsAndEveryDestination()
    {
        var entries = TrayMenuModel.Build(
            dictationRecording: false, meetingRecording: false, meetingPaused: false,
            canResumeSetup: false, canReplayFeatureTour: false);

        Assert.Equal(
            [
                "Open Muesli", "Start dictation", "Start Quick Note", "-",
                "Dictations", "Meetings", "Settings", "About and diagnostics", "-", "Quit"
            ],
            Labels(entries));
    }

    [Fact]
    public void RecordingMeetingOffersPauseAndStop()
    {
        var entries = TrayMenuModel.Build(
            dictationRecording: true, meetingRecording: true, meetingPaused: false,
            canResumeSetup: false, canReplayFeatureTour: false);

        Assert.Equal(["Open Muesli", "Stop dictation", "Pause meeting", "Stop and save meeting"], Labels(entries).Take(4));
        Assert.Contains(entries, entry => entry.Command == TrayCommand.PauseMeeting);
        Assert.DoesNotContain(entries, entry => entry.Command == TrayCommand.ResumeMeeting);
    }

    [Fact]
    public void PausedMeetingOffersResumeEvenWhileStillReportedAsRecording()
    {
        var entries = TrayMenuModel.Build(
            dictationRecording: false, meetingRecording: true, meetingPaused: true,
            canResumeSetup: false, canReplayFeatureTour: false);

        Assert.Equal(["Resume meeting", "Stop and save meeting"], Labels(entries).Skip(2).Take(2));
        Assert.DoesNotContain(entries, entry => entry.Command == TrayCommand.PauseMeeting);
    }

    [Theory]
    [InlineData(nameof(TrayCommand.None), false, true)]
    [InlineData(nameof(TrayCommand.ToggleDictation), false, true)]
    [InlineData(nameof(TrayCommand.PauseMeeting), false, true)]
    [InlineData(nameof(TrayCommand.ResumeMeeting), false, true)]
    [InlineData(nameof(TrayCommand.ToggleMeeting), false, false)]
    [InlineData(nameof(TrayCommand.ToggleMeeting), true, true)]
    [InlineData(nameof(TrayCommand.Open), true, false)]
    [InlineData(nameof(TrayCommand.Open), false, false)]
    [InlineData(nameof(TrayCommand.Settings), false, false)]
    [InlineData(nameof(TrayCommand.ResumeSetup), false, false)]
    [InlineData(nameof(TrayCommand.Quit), false, false)]
    public void OnlyCancelAndCaptureOnlyCommandsReturnFocusToTheTray(string command, bool meetingActive, bool expected) =>
        Assert.Equal(expected, TrayMenuModel.LeavesFocusInTray(Enum.Parse<TrayCommand>(command), meetingActive));

    [Fact]
    public void OptionalEntriesAppearOnlyWhenAvailableAndCommandsAreUnique()
    {
        var entries = TrayMenuModel.Build(
            dictationRecording: false, meetingRecording: false, meetingPaused: false,
            canResumeSetup: true, canReplayFeatureTour: true);

        Assert.Contains("Resume setup", Labels(entries));
        Assert.Contains("Replay feature tour", Labels(entries));
        // Each command is the native menu item id, so a duplicate would make two rows indistinguishable.
        var commands = entries.Where(entry => !entry.IsSeparator).Select(entry => entry.Command).ToArray();
        Assert.Equal(commands.Length, commands.Distinct().Count());
        Assert.Equal("Quit", entries[^1].Label);
    }
}
