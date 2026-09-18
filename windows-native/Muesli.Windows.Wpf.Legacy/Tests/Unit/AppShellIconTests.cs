using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class AppShellIconTests
{
    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows", "MainWindow.xaml")))
                    return directory.FullName;
            }

            throw new DirectoryNotFoundException("Could not locate the Muesli repository root from the test output directory.");
        }
    }

    private static string ReadWindowsSource(params string[] relativeParts)
    {
        var parts = new[] { RepositoryRoot, "windows-native", "Muesli.Windows" }.Concat(relativeParts).ToArray();
        return File.ReadAllText(Path.Combine(parts));
    }

    [Fact]
    public void Product_icon_includes_taskbar_and_thumbnail_sizes()
    {
        var path = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Assets", "muesli.ico");
        Assert.True(File.Exists(path), "Missing Assets/muesli.ico");
        var sizes = AppShellIcon.ReadIconDirectorySizes(path);
        Assert.Contains(16, sizes);
        Assert.Contains(24, sizes);
        Assert.Contains(32, sizes);
        Assert.Contains(256, sizes);
    }

    [Fact]
    public void Shell_windows_and_tray_use_the_product_ico()
    {
        var main = ReadWindowsSource("MainWindow.xaml");
        var onboarding = ReadWindowsSource("OnboardingWindow.xaml");
        var tray = ReadWindowsSource("Services", "TrayIconService.cs");
        var app = ReadWindowsSource("App.xaml.cs");
        var binder = ReadWindowsSource("Services", "AppShellIcon.cs");

        Assert.Contains("Icon=\"Assets/muesli.ico\"", main);
        Assert.Contains("Source=\"Assets/muesli_app_icon.png\"", main);
        Assert.DoesNotContain("menu_m_template@2x.png", main);
        Assert.Contains("Icon=\"Assets/muesli.ico\"", onboarding);
        Assert.Contains("AppShellIcon.CreateNotifyIcon()", tray);
        Assert.DoesNotContain("muesli_app_icon.png", tray);
        Assert.Contains("AppShellIcon.RegisterForApplication()", app);
        var firstLog = app.IndexOf("Muesli starting.", StringComparison.Ordinal);
        var register = app.IndexOf("AppShellIcon.RegisterForApplication()", StringComparison.Ordinal);
        Assert.True(firstLog >= 0 && register > firstLog, "Shell icon registration must run after the first startup log.");
        Assert.Contains("WmGetIcon = 0x007F", binder);
        Assert.Contains("WmSetIcon = 0x0080", binder);
        Assert.Contains("IconSmall = 0", binder);
        Assert.Contains("IconBig = 1", binder);
    }
}
