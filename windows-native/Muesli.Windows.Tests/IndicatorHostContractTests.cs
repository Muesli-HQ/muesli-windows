using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// The floating indicator is drawn by the Muesli process itself. These invariants need a live WinUI
/// runtime to exercise, so they are pinned at the source level.
/// </summary>
public sealed class IndicatorHostContractTests
{
    private static string HostSource() => File.ReadAllText(TestRepositoryLayout.Combine(
        "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiIndicatorHost.cs"));

    private static string WindowSource() => File.ReadAllText(TestRepositoryLayout.Combine(
        "windows-native", "Muesli.Windows.WinUI", "Indicator", "FloatingIndicatorWindow.cs"));

    [Fact]
    public void The_indicator_never_runs_as_a_second_process_or_over_a_pipe()
    {
        var host = HostSource();
        Assert.DoesNotContain("Process.Start", host, StringComparison.Ordinal);
        Assert.DoesNotContain("IndicatorPipe", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Indicator.Wpf", host, StringComparison.Ordinal);
        // The project file, not the folder: ignored bin/obj output survives a branch switch.
        Assert.False(File.Exists(TestRepositoryLayout.Combine(
                "windows-native", "Muesli.Windows.Indicator.Wpf", "Muesli.Windows.Indicator.Wpf.csproj")),
            "The retired WPF companion project must not come back.");
    }

    [Fact]
    public void Microphone_levels_are_never_marshalled_to_the_ui_thread_per_buffer()
    {
        var host = HostSource();
        Assert.Contains(
            "private void OnRecordingLevel(object? sender, float peak) => Volatile.Write(ref _amplitude",
            host,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_never_activates_and_stays_out_of_the_taskbar_and_switchers()
    {
        var window = WindowSource();
        Assert.Contains("AppWindow.IsShownInSwitchers = false;", window, StringComparison.Ordinal);
        Assert.Contains("WsExToolWindow | WsExNoActivate", window, StringComparison.Ordinal);
        Assert.Contains("AppWindow.Show(false);", window, StringComparison.Ordinal);
        Assert.Contains("return (IntPtr)MaNoActivate;", window, StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_that_cannot_be_created_leaves_muesli_running_without_a_pill()
    {
        var host = HostSource();
        Assert.Contains("continuing without the floating pill", host, StringComparison.Ordinal);
        Assert.Contains("_window is null) return;", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Rendering_failures_from_input_timers_and_display_changes_drop_the_pill_instead_of_crashing()
    {
        var window = WindowSource();
        Assert.Contains("_waveTimer.Tick += (_, _) => Guarded(AnimateWaveform);", window, StringComparison.Ordinal);
        Assert.Contains("_collapseTimer.Tick += (_, _) => Guarded(CollapseHover);", window, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => Guarded(", window, StringComparison.Ordinal);
        foreach (var handler in new[] { "OnPointerEntered", "OnPointerExited", "OnPointerPressed", "OnPointerMoved", "OnPointerReleased", "OnPointerCaptureLost", "OnRightTapped" })
        {
            Assert.Matches($@"private void {handler}\([^)]*\) => Guarded\(", window);
        }

        Assert.Contains("OnWindowFailure", HostSource(), StringComparison.Ordinal);
    }
}
