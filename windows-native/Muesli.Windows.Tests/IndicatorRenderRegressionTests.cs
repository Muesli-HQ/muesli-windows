using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Regression coverage for the confirmed WPF indicator defect: a full render was performed for
/// every amplitude snapshot. The fix hinges on <see cref="IndicatorProtocol.IsStructuralChange"/>,
/// which classifies an amplitude-only frame as non-structural so the renderer updates only the
/// live level.
/// </summary>
public sealed class IndicatorRenderRegressionTests
{
    private static IndicatorSnapshot Recording(float amplitude = 0f) => new()
    {
        SessionId = 7,
        State = "recording",
        Owner = IndicatorOwnerKind.Dictation,
        Visible = true,
        HotkeyLabel = "F8",
        Message = "",
        RecordingColorHex = "EF4444",
        MeetingPaused = false,
        IndicatorAnchor = "Custom",
        SavedLeft = 100,
        SavedTop = 200,
        Theme = "dark",
        HighContrast = false,
        Amplitude = amplitude
    };

    [Fact]
    public void Amplitude_only_frame_is_not_structural()
    {
        var previous = Recording(0.1f);
        var next = previous with { Amplitude = 0.9f };

        Assert.False(IndicatorProtocol.IsStructuralChange(previous, next));
    }

    [Fact]
    public void Repeated_amplitude_only_frames_never_become_structural()
    {
        // The host sends up to 30 amplitude frames per second. None of them may rebuild the
        // waveform or reposition the pill, however the level wanders.
        var frame = Recording(0.0f);
        for (var i = 1; i <= 300; i++)
        {
            var next = frame with { Amplitude = (i % 11) / 10f };
            Assert.False(IndicatorProtocol.IsStructuralChange(frame, next));
            frame = next;
        }
    }

    [Fact]
    public void Identical_snapshot_is_not_structural()
    {
        var baseline = Recording(0.5f);
        Assert.False(IndicatorProtocol.IsStructuralChange(baseline, baseline with { }));
    }

    [Fact]
    public void Every_structural_field_triggers_a_render()
    {
        var baseline = Recording(0.5f);

        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { Version = 99 }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { SessionId = 8 }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { State = "transcribing" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { Owner = IndicatorOwnerKind.Meeting }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { Visible = false }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { HotkeyLabel = "Ctrl+F8" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { Message = "Done" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { RecordingColorHex = "00FF00" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { MeetingPaused = true }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { IndicatorAnchor = "Top Left" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { SavedLeft = 5 }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { SavedTop = 5 }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { Theme = "light" }));
        Assert.True(IndicatorProtocol.IsStructuralChange(baseline, baseline with { HighContrast = true }));
    }

    [Fact]
    public void State_transition_into_recording_is_structural_but_same_state_is_not()
    {
        var idle = Recording() with { State = "idle", SessionId = 1 };
        var recording = idle with { State = "recording", SessionId = 2 };

        Assert.True(IndicatorProtocol.IsStructuralChange(idle, recording));
        Assert.False(IndicatorProtocol.IsStructuralChange(recording, recording with { Amplitude = 0.6f }));
    }
}
