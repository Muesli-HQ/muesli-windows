using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Regression coverage for the confirmed drag defect: movement was derived from coordinates
/// relative to the moving window, so the gesture fed on its own displacement and oscillated.
/// <see cref="FloatingIndicatorLayout.DragTopLeft"/> takes a fixed origin plus an absolute
/// screen-pixel delta, which cannot oscillate.
/// </summary>
public sealed class IndicatorDragTests
{
    private static readonly DipRect WorkArea = new(0, 0, 1920, 1040);
    private static readonly DipSize Size = new(44, 28, 14);

    [Fact]
    public void Drag_follows_absolute_screen_delta_monotonically()
    {
        var origin = new DipPoint(500, 500);
        DipPoint? previous = null;

        for (var pixels = 0; pixels <= 400; pixels += 10)
        {
            var point = FloatingIndicatorLayout.DragTopLeft(origin, pixels, 0, 1.0, Size, WorkArea);
            if (previous is { } p)
            {
                Assert.True(point.X > p.X, "the pill must move forward with the pointer, never back");
                Assert.Equal(p.Y, point.Y, 6);
            }
            previous = point;
        }
    }

    [Fact]
    public void Drag_converts_physical_pixels_to_dips_with_the_monitor_scale()
    {
        // A 120 px move on a 150% monitor is 80 DIPs, so the DIP position advances by 80.
        var point = FloatingIndicatorLayout.DragTopLeft(new DipPoint(100, 100), 120, 0, 1.5, Size, WorkArea);
        Assert.Equal(180, point.X, 6);
        Assert.Equal(100, point.Y, 6);
    }

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 80)]
    [InlineData(1.5, 66.6666667)]
    [InlineData(1.75, 57.1428571)]
    [InlineData(2.0, 50)]
    public void Drag_is_dpi_aware_at_every_supported_scale(double scale, double expectedDipDelta)
    {
        var point = FloatingIndicatorLayout.DragTopLeft(new DipPoint(200, 200), 100, 0, scale, Size, WorkArea);
        Assert.Equal(200 + expectedDipDelta, point.X, 5);
    }

    [Fact]
    public void Drag_clamps_to_the_work_area()
    {
        var point = FloatingIndicatorLayout.DragTopLeft(new DipPoint(100, 100), 100000, 100000, 1.0, Size, WorkArea);
        Assert.True(point.X + Size.Width <= WorkArea.Right);
        Assert.True(point.Y + Size.Height <= WorkArea.Bottom);
    }

    [Fact]
    public void Drag_with_invalid_scale_does_not_divide_by_zero()
    {
        var point = FloatingIndicatorLayout.DragTopLeft(new DipPoint(100, 100), 50, 50, 0, Size, WorkArea);
        Assert.Equal(150, point.X, 6);
        Assert.Equal(150, point.Y, 6);
    }

    [Fact]
    public void Drag_result_depends_only_on_the_fixed_origin_not_the_windows_current_position()
    {
        // State or amplitude traffic between moves cannot reset the gesture: every sample is
        // computed from the position captured at mouse-down and the current absolute pointer
        // delta. A later delta always lands further along the same axis.
        var origin = new DipPoint(300, 300);
        var afterFirstSample = FloatingIndicatorLayout.DragTopLeft(origin, 40, 0, 1.0, Size, WorkArea);
        var afterStateTraffic = FloatingIndicatorLayout.DragTopLeft(origin, 40, 0, 1.0, Size, WorkArea);
        var afterLaterSample = FloatingIndicatorLayout.DragTopLeft(origin, 80, 0, 1.0, Size, WorkArea);

        Assert.Equal(afterFirstSample, afterStateTraffic);
        Assert.True(afterLaterSample.X > afterFirstSample.X);
    }

    [Fact]
    public void Drag_completion_produces_one_final_custom_centre()
    {
        // The companion persists the centre once on release: top-left plus half the pill size.
        var finalTopLeft = FloatingIndicatorLayout.DragTopLeft(new DipPoint(400, 400), 120, 60, 1.5, Size, WorkArea);
        var centre = new DipPoint(finalTopLeft.X + Size.Width / 2, finalTopLeft.Y + Size.Height / 2);

        Assert.Equal(480 + Size.Width / 2, centre.X, 6);
        Assert.Equal(440 + Size.Height / 2, centre.Y, 6);
    }

    [Fact]
    public void Drag_respects_the_work_area_margin()
    {
        var tiny = new DipRect(0, 0, 100, 100);
        var point = FloatingIndicatorLayout.DragTopLeft(new DipPoint(0, 0), 1000, 1000, 1.0, Size, tiny);
        // 8 DIP margin to every edge: 100 - 44 - 8 and 100 - 28 - 8.
        Assert.Equal(48, point.X, 6);
        Assert.Equal(64, point.Y, 6);
    }
}
