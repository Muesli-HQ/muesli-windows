namespace Muesli.Windows.Core.Services;

/// <summary>
/// The pure drag lifecycle for the floating indicator. Kept out of the WPF window so the rules that
/// cannot be exercised without real pointer capture can still be unit-tested:
/// <list type="bullet">
/// <item>movement is derived from a fixed screen-pixel origin captured at mouse-down;</item>
/// <item>a gesture persists its position at most once, on release or capture loss;</item>
/// <item>losing mouse capture ends the drag immediately, so a stuck gesture can never freeze the pill.</item>
/// </list>
/// Amplitude and state snapshots are never routed through this type, so they cannot move the window
/// while <see cref="IsDragging"/> is true.
/// </summary>
public sealed class FloatingIndicatorDragSession
{
    private bool _dragging;
    private bool _moved;
    private DipPoint _startScreenPixels;
    private DipPoint _originTopLeft;

    /// <summary>True between a mouse-down and its release/capture-loss. Automatic positioning is blocked while true.</summary>
    public bool IsDragging => _dragging;

    /// <summary>
    /// Starts a gesture. <paramref name="startScreenPixels"/> is the absolute pointer position in
    /// physical screen pixels; <paramref name="originTopLeft"/> is the window's top-left in DIPs.
    /// </summary>
    public void Begin(DipPoint startScreenPixels, DipPoint originTopLeft)
    {
        _dragging = true;
        _moved = false;
        _startScreenPixels = startScreenPixels;
        _originTopLeft = originTopLeft;
    }

    /// <summary>
    /// Computes a new top-left for the current absolute pointer position, or <c>null</c> when the
    /// gesture has not moved past <see cref="FloatingIndicatorLayout.DragThreshold"/> or is not
    /// active. The result depends only on the fixed origin, never on the window's own position.
    /// </summary>
    public DipPoint? Move(DipPoint currentScreenPixels, double scale, DipSize size, DipRect workArea)
    {
        if (!_dragging) return null;

        var deltaXPixels = currentScreenPixels.X - _startScreenPixels.X;
        var deltaYPixels = currentScreenPixels.Y - _startScreenPixels.Y;
        var effectiveScale = scale > 0 ? scale : 1.0;
        var deltaDipX = deltaXPixels / effectiveScale;
        var deltaDipY = deltaYPixels / effectiveScale;
        if (!_moved &&
            Math.Sqrt(deltaDipX * deltaDipX + deltaDipY * deltaDipY) < FloatingIndicatorLayout.DragThreshold)
        {
            return null;
        }

        _moved = true;
        return FloatingIndicatorLayout.DragTopLeft(
            _originTopLeft, deltaXPixels, deltaYPixels, effectiveScale, size, workArea);
    }

    /// <summary>
    /// Ends the gesture and returns the single custom-position command to persist, or <c>null</c>
    /// when nothing moved or the gesture was already ended. Safe to call twice (mouse-up after
    /// capture loss), so a gesture can never persist two positions.
    /// </summary>
    public IndicatorCommand? End(long sessionId, DipPoint currentTopLeft, DipSize size)
    {
        if (!_dragging) return null;
        _dragging = false;
        if (!_moved) return null;

        var centre = new DipPoint(currentTopLeft.X + size.Width / 2, currentTopLeft.Y + size.Height / 2);
        return new IndicatorCommand
        {
            SessionId = sessionId,
            Type = IndicatorCommandType.Drag,
            DragLeft = centre.X,
            DragTop = centre.Y
        };
    }

    /// <summary>
    /// True when a gesture already ended (capture loss) after moving, so a later mouse-up must be
    /// swallowed instead of being interpreted as a click. Resets the gesture state.
    /// </summary>
    public bool ConsumeEndedMovedGesture()
    {
        if (_dragging || !_moved) return false;
        _moved = false;
        return true;
    }
}
