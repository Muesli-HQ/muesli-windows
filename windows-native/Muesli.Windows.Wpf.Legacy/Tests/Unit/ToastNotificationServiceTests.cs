using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class ToastNotificationServiceTests
{
    [Fact]
    public void ClampWindowPositionKeepsIndicatorInsideWorkArea()
    {
        var area = new System.Windows.Rect(0, 0, 1920, 1040);
        var size = new System.Windows.Size(220, 36);

        var clamped = ToastNotificationService.ClampWindowPosition(
            area,
            size,
            new System.Windows.Point(1900, 1030));

        Assert.Equal(1692, clamped.X);
        Assert.Equal(996, clamped.Y);
    }

    [Fact]
    public void ClampWindowPositionSupportsNegativeOriginMonitors()
    {
        var area = new System.Windows.Rect(-2560, -320, 2560, 1400);
        var size = new System.Windows.Size(260, 36);

        var clamped = ToastNotificationService.ClampWindowPosition(
            area,
            size,
            new System.Windows.Point(-5000, -800));

        Assert.Equal(-2552, clamped.X);
        Assert.Equal(-312, clamped.Y);
        Assert.True(clamped.X + size.Width <= area.Right);
        Assert.True(clamped.Y + size.Height <= area.Bottom);
    }

    [Fact]
    public void ClampWindowPositionRepairsSavedPositionAfterIndicatorExpands()
    {
        var area = new System.Windows.Rect(0, 0, 1280, 720);
        var expandedSize = new System.Windows.Size(260, 36);

        var clamped = ToastNotificationService.ClampWindowPosition(
            area,
            expandedSize,
            new System.Windows.Point(1258, 700));

        Assert.Equal(1012, clamped.X);
        Assert.Equal(676, clamped.Y);
    }
}
