using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-30 (macOS 54e12876): the sidebar uses the shared Light / Follow System / Dark control.</summary>
public sealed class AppearanceToggleTests
{
    [Fact]
    public void Sidebar_toggle_offers_light_system_dark_in_macOS_order()
    {
        var shell = File.ReadAllText(Path.Combine(TestRepositoryLayout.Root, "windows-native", "Muesli.Windows.WinUI", "MainPage.xaml"));
        var tags = Regex.Matches(shell, "Tag=\"([a-z]+)\" Click=\"ThemeToggle_Click\"").Select(m => m.Groups[1].Value);
        Assert.Equal(["light", "system", "dark"], tags);
    }
}
