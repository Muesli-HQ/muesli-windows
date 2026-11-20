using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-31 (macOS 9d0ad706): indicator updates read cached settings, not the file on every tick.</summary>
public sealed class IndicatorFeedbackSettingsTests
{
    [Fact]
    public void Indicator_updates_use_the_cached_settings_and_only_the_position_save_reloads()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryLayout.Root, "windows-native", "Muesli.Windows.WinUI",
            "Services", "WinUiIndicatorHost.cs"));
        var lines = source.Split('\n').Where(line => line.Contains("App.Settings.Load()", StringComparison.Ordinal)).ToList();
        // One inside FeedbackSettings, one feeding the drag-position save.
        Assert.Equal(2, lines.Count);
        Assert.Contains("_feedbackSettings = App.Settings.Load();", source, StringComparison.Ordinal);
        Assert.Contains("stamp != _feedbackSettingsStamp", source, StringComparison.Ordinal);
        Assert.Contains("var settings = App.Settings.Load();\r\n                App.Settings.Save(", source.Replace("\r\n", "\n").Replace("\n", "\r\n"), StringComparison.Ordinal);
    }
}
