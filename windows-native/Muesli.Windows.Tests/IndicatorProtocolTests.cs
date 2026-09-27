using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

public sealed class IndicatorProtocolTests
{
    [Fact]
    public void Snapshot_round_trips_through_json()
    {
        var original = new IndicatorSnapshot
        {
            SessionId = 42,
            State = "recording",
            Owner = IndicatorOwnerKind.Meeting,
            Visible = true,
            HotkeyLabel = "F8",
            Message = "Listening",
            RecordingColorHex = "EF4444",
            MeetingPaused = true,
            IndicatorAnchor = "Custom",
            SavedLeft = 1234.5,
            SavedTop = 900.25,
            Theme = "dark",
            HighContrast = false,
            Amplitude = 0.75f
        };

        var json = IndicatorProtocol.SerializeSnapshot(original);
        var roundTripped = IndicatorProtocol.DeserializeSnapshot(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.SessionId, roundTripped!.SessionId);
        Assert.Equal(original.State, roundTripped.State);
        Assert.Equal(original.Owner, roundTripped.Owner);
        Assert.Equal(original.HotkeyLabel, roundTripped.HotkeyLabel);
        Assert.Equal(original.RecordingColorHex, roundTripped.RecordingColorHex);
        Assert.Equal(original.MeetingPaused, roundTripped.MeetingPaused);
        Assert.Equal(original.IndicatorAnchor, roundTripped.IndicatorAnchor);
        Assert.Equal(original.SavedLeft, roundTripped.SavedLeft);
        Assert.Equal(original.SavedTop, roundTripped.SavedTop);
        Assert.Equal(original.Amplitude, roundTripped.Amplitude);
    }

    [Fact]
    public void Command_round_trips_through_json()
    {
        var original = new IndicatorCommand
        {
            SessionId = 7,
            Type = IndicatorCommandType.Drag,
            DragLeft = 640,
            DragTop = 360
        };

        var json = IndicatorProtocol.SerializeCommand(original);
        var roundTripped = IndicatorProtocol.DeserializeCommand(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.SessionId, roundTripped!.SessionId);
        Assert.Equal(original.Type, roundTripped.Type);
        Assert.Equal(original.DragLeft, roundTripped.DragLeft);
        Assert.Equal(original.DragTop, roundTripped.DragTop);
    }

    [Fact]
    public void Meeting_notification_and_action_round_trip_through_pipe_protocol()
    {
        var snapshot = new IndicatorSnapshot
        {
            MeetingNotification = new IndicatorMeetingNotification(
                "meeting-1", "Meeting detected", "Google Meet · Planning", "Google Meet",
                "", "#3380FF", "MEET", "Start Transcribing", false,
                Muesli.Windows.Services.MeetingJoinDefaultAction.TranscribeOnly, 15)
        };
        var received = IndicatorProtocol.DeserializeSnapshot(IndicatorProtocol.SerializeSnapshot(snapshot));
        Assert.Equal(snapshot.MeetingNotification, received?.MeetingNotification);

        var command = new IndicatorCommand
        {
            Type = IndicatorCommandType.MeetingNotificationAction,
            NotificationPromptId = "meeting-1",
            NotificationAction = "StartTranscribing"
        };
        var receivedCommand = IndicatorProtocol.DeserializeCommand(IndicatorProtocol.SerializeCommand(command));
        Assert.Equal(command, receivedCommand);
    }

    [Fact]
    public void DeserializeSnapshot_rejects_unsupported_version()
    {
        var future = new IndicatorSnapshot { Version = IndicatorProtocol.CurrentVersion + 1 };
        var json = System.Text.Json.JsonSerializer.Serialize(future, IndicatorProtocol.Json);
        Assert.Null(IndicatorProtocol.DeserializeSnapshot(json));
    }

    [Fact]
    public void DeserializeCommand_rejects_unsupported_version()
    {
        var future = new IndicatorCommand { Version = 999 };
        var json = System.Text.Json.JsonSerializer.Serialize(future, IndicatorProtocol.Json);
        Assert.Null(IndicatorProtocol.DeserializeCommand(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"Version\":1,\"State\":junk}")]
    public void DeserializeSnapshot_returns_null_for_malformed_input(string? json) =>
        Assert.Null(IndicatorProtocol.DeserializeSnapshot(json));

    [Fact]
    public void IsStaleSession_rejects_older_session_id()
    {
        Assert.True(IndicatorProtocol.IsStaleSession(commandSession: 5, liveSession: 9));
        Assert.False(IndicatorProtocol.IsStaleSession(commandSession: 9, liveSession: 9));
    }

    [Fact]
    public void PipeNameFor_sanitises_instance_id()
    {
        Assert.Equal("Muesli.Indicator.abc123", IndicatorProtocol.PipeNameFor("abc-123!"));
        Assert.Equal("Muesli.Indicator.default", IndicatorProtocol.PipeNameFor(""));
    }
}
