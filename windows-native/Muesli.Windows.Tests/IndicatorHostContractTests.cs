using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Phase C contract: the only renderer is the WPF indicator companion under <c>Indicator\</c>.
/// When it cannot run, Muesli carries on without a floating pill (the pill is optional). Because
/// the companion runs in a separate process, these invariants are pinned at the source level; the
/// live cross-process launch is an explicit operator step in docs/WINDOWS_UI_QUALIFICATION.md.
/// </summary>
public sealed class IndicatorHostContractTests
{
    private static string Source() => File.ReadAllText(TestRepositoryLayout.Combine(
        "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiIndicatorHost.cs"));

    [Fact]
    public void A_lost_companion_never_spawns_an_in_process_pill()
    {
        var source = Source();
        Assert.Contains("_companionLost = true;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DictationIndicatorWindow", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_companion_stops_publishing()
    {
        var source = Source();
        // Every state/publish/command path bails out once the companion is gone for good.
        Assert.Contains("_disposed) != 0 || _companionLost", source, StringComparison.Ordinal);
        Assert.Contains("_server is null", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Launching_a_companion_kills_any_previous_one()
    {
        var source = Source();
        Assert.Contains("Never allow a second companion", source, StringComparison.Ordinal);
        Assert.Contains("_companion?.Kill()", source, StringComparison.Ordinal);
        Assert.Contains("Indicator", source, StringComparison.Ordinal);
        Assert.Contains("Muesli.Windows.Indicator.Wpf.exe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_recovery_is_bounded()
    {
        var source = Source();
        Assert.Contains("if (_restarts < 1)", source, StringComparison.Ordinal);
        Assert.Contains("_restarts++;", source, StringComparison.Ordinal);
        Assert.Contains("disconnected again; continuing without the floating pill", source, StringComparison.Ordinal);
    }
}
