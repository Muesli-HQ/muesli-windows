using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Phase D regression coverage for the WPF pill drag lifecycle. The confirmed defect was that a
/// lost mouse capture (another app grabbing the pointer, Alt+Tab, a touch transition) left the drag
/// flag set forever, permanently blocking automatic positioning. These tests pin the extracted
/// <see cref="FloatingIndicatorDragSession"/> rules that make capture loss safe and persistence
/// exactly-once.
/// </summary>
public sealed class IndicatorDragSessionTests
{
    private static readonly DipRect WorkArea = new(0, 0, 1920, 1040);
    private static readonly DipSize Size = new(44, 28, 14);

    [Fact]
    public void Capture_loss_ends_the_drag_and_persists_exactly_once()
    {
        var session = new FloatingIndicatorDragSession();
        session.Begin(new DipPoint(100, 100), new DipPoint(10, 20));

        var moved = session.Move(new DipPoint(160, 100), 1.0, Size, WorkArea);
        Assert.NotNull(moved);
        Assert.True(session.IsDragging);

        // Capture is lost: the window ends the gesture itself, without a mouse-up.
        var command = session.End(42, new DipPoint(moved!.Value.X, moved.Value.Y), Size);
        Assert.NotNull(command);
        Assert.Equal(IndicatorCommandType.Drag, command!.Type);
        Assert.Equal(42, command.SessionId);
        Assert.False(session.IsDragging, "capture loss must clear the drag flag so the pill stays responsive");

        Assert.Null(session.End(42, new DipPoint(0, 0), Size));
    }

    [Fact]
    public void Mouse_up_after_a_capture_loss_is_swallowed_and_never_becomes_a_click()
    {
        var session = new FloatingIndicatorDragSession();
        session.Begin(new DipPoint(100, 100), new DipPoint(10, 20));
        var moved = session.Move(new DipPoint(200, 200), 1.0, Size, WorkArea);
        Assert.NotNull(moved);
        Assert.NotNull(session.End(7, new DipPoint(moved!.Value.X, moved.Value.Y), Size));

        // The later mouse-up has no active gesture and no new Drag command, and must not be treated
        // as a stop/cancel click.
        Assert.Null(session.End(7, new DipPoint(0, 0), Size));
        Assert.True(session.ConsumeEndedMovedGesture());
        Assert.False(session.ConsumeEndedMovedGesture());
    }

    [Fact]
    public void A_press_without_movement_is_left_for_the_click_handler()
    {
        var session = new FloatingIndicatorDragSession();
        session.Begin(new DipPoint(100, 100), new DipPoint(10, 20));

        // Below the 4-DIP drag threshold: not a drag.
        Assert.Null(session.Move(new DipPoint(101, 101), 1.0, Size, WorkArea));
        Assert.Null(session.End(9, new DipPoint(10, 20), Size));
        Assert.False(session.ConsumeEndedMovedGesture());
        Assert.False(session.IsDragging);
    }

    [Fact]
    public void Movement_uses_the_fixed_origin_and_cannot_oscillate()
    {
        var session = new FloatingIndicatorDragSession();
        session.Begin(new DipPoint(0, 0), new DipPoint(300, 300));

        var first = session.Move(new DipPoint(40, 0), 1.0, Size, WorkArea);
        var repeated = session.Move(new DipPoint(40, 0), 1.0, Size, WorkArea);
        var later = session.Move(new DipPoint(80, 0), 1.0, Size, WorkArea);

        Assert.Equal(first, repeated);
        Assert.True(later!.Value.X > first!.Value.X);
        Assert.Equal(300, first.Value.Y);
    }

    [Fact]
    public void Move_before_begin_does_nothing()
    {
        var session = new FloatingIndicatorDragSession();
        Assert.Null(session.Move(new DipPoint(500, 500), 1.0, Size, WorkArea));
        Assert.Null(session.End(1, new DipPoint(0, 0), Size));
    }

    [Fact]
    public void Window_wires_capture_loss_and_guards_positioning_while_dragging()
    {
        var directory = TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.Indicator.Wpf");
        var xaml = File.ReadAllText(Path.Combine(directory, "IndicatorWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(directory, "IndicatorWindow.xaml.cs"));

        Assert.Contains("LostMouseCapture=\"Pill_LostMouseCapture\"", xaml, StringComparison.Ordinal);
        Assert.Contains("private void Pill_LostMouseCapture", code, StringComparison.Ordinal);
        Assert.Contains("_drag.IsDragging", code, StringComparison.Ordinal);
        Assert.Contains("FloatingIndicatorDragSession", code, StringComparison.Ordinal);
        // The old independent flag must be gone so capture loss cannot strand it.
        Assert.DoesNotContain("private bool _dragging;", code, StringComparison.Ordinal);
    }
}
