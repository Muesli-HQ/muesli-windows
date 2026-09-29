using System;
using System.IO;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>
/// Phase C contract: the shipping renderer is the WPF indicator companion under
/// <c>Indicator\</c>. The in-process WinUI fallback is diagnostic only and must never render at the
/// same time as a healthy companion. Because the companion runs in a separate process, these
/// mutual-exclusion invariants are pinned at the source level; the live cross-process launch is an
/// explicit operator step in docs/WINDOWS_UI_QUALIFICATION.md.
/// </summary>
public sealed class IndicatorHostContractTests
{
    private static string Source() => File.ReadAllText(TestRepositoryLayout.Combine(
        "windows-native", "Muesli.Windows.WinUI", "Services", "WinUiIndicatorHost.cs"));

    [Fact]
    public void Fallback_is_created_once_and_only_when_the_companion_is_not_healthy()
    {
        var source = Source();
        Assert.Contains("public bool IsFallbackActive", source, StringComparison.Ordinal);
        Assert.Contains("if (_fallback is not null || Volatile.Read(ref _disposed) != 0) return;", source, StringComparison.Ordinal);
        Assert.Contains("_fallback = new DictationIndicatorWindow(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_owned_state_never_also_publishes_to_the_fallback()
    {
        var source = Source();
        // Every state/publish/command path bails out once the fallback owns rendering.
        Assert.Contains("_disposed) != 0 || _fallback is not null", source, StringComparison.Ordinal);
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
    public void Companion_recovery_is_bounded_and_then_falls_back()
    {
        var source = Source();
        Assert.Contains("if (_restarts < 1)", source, StringComparison.Ordinal);
        Assert.Contains("_restarts++;", source, StringComparison.Ordinal);
        Assert.Contains("_dispatcher.TryEnqueue(ActivateFallback);", source, StringComparison.Ordinal);
    }
}
