using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace Muesli.Windows.Tests;

public sealed class DpiManifestAndPlacementTests
{
    [Fact]
    public void Source_manifest_declares_per_monitor_v2_and_legacy_dpi_aware_without_elevation()
    {
        var xml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "app.manifest"));
        AssertDpiContract(xml);
        Assert.Contains("level=\"asInvoker\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("highestAvailable", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Built_executable_manifest_declares_per_monitor_v2_and_legacy_dpi_aware()
    {
        var executable = FindBuiltMuesliExecutable();
        var xml = Win32ManifestExtractor.Extract(executable);
        Assert.False(string.IsNullOrWhiteSpace(xml), $"RT_MANIFEST from '{executable}' was empty.");
        AssertDpiContract(xml);
        Assert.Contains("asInvoker", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("requireAdministrator", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Visual_capture_script_extracts_the_built_executable_manifest()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "verify-phase12-ui.ps1"));
        Assert.Contains("Assert-BuiltExecutableDpiManifest", script);
        Assert.Contains("RT_MANIFEST", script);
        Assert.Contains("PerMonitorV2", script);
        Assert.Contains("true/pm", script);
    }

    [Fact]
    public void Project_keeps_application_high_dpi_mode_and_suppresses_winforms_manifest_conflict_warning()
    {
        var project = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "Muesli.Windows.csproj"));
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project);
        Assert.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", project);
        Assert.Contains("WFO0003", project);
        Assert.DoesNotContain("SetHighDpiMode", File.ReadAllText(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "App.xaml.cs")));
    }

    [Fact]
    public void Window_placement_at_100_percent_dpi_is_an_identity_pixel_to_dip_conversion()
    {
        var converted = WindowPlacementService.PixelRectToDipRect(
            new WindowPlacementService.PixelRect(0, 0, 1920, 1080),
            Matrix.Identity,
            new WindowPlacementService.ScreenCoordinateAnchor(new(0, 0), new(0, 0)));
        Assert.Equal(0, converted.Left);
        Assert.Equal(0, converted.Top);
        Assert.Equal(1920, converted.Width);
        Assert.Equal(1080, converted.Height);
    }

    [Theory]
    [InlineData("dashboard", 1240, 820)]
    [InlineData("onboarding", 820, 700)]
    [InlineData("toast", 220, 36)]
    [InlineData("meeting-prompt", 360, 120)]
    [InlineData("live-transcript", 380, 340)]
    public void Named_windows_restore_fully_on_screen_at_100_percent_dpi(string window, double width, double height)
    {
        var work = new Rect(0, 0, 1920, 1040);
        var restored = WindowPlacementService.ClampToWorkArea(new Rect(40, 40, width, height), work);
        AssertFullyOnScreen(restored, work, window);
        Assert.Equal(width, restored.Width);
        Assert.Equal(height, restored.Height);
    }

    [Theory]
    [InlineData("dashboard", 1240, 820)]
    [InlineData("onboarding", 820, 700)]
    [InlineData("toast", 220, 36)]
    [InlineData("meeting-prompt", 360, 120)]
    [InlineData("live-transcript", 380, 340)]
    public void Named_windows_clamp_off_screen_and_missing_monitor_bounds_at_100_percent_dpi(string window, double width, double height)
    {
        var remaining = new Rect(0, 0, 1920, 1040);
        var disconnected = WindowPlacementService.ClampToWorkArea(new Rect(3840, 0, width, height), remaining);
        var parked = WindowPlacementService.ClampToWorkArea(new Rect(-32000, -32000, width, height), remaining);
        AssertFullyOnScreen(disconnected, remaining, window);
        AssertFullyOnScreen(parked, remaining, window);
        Assert.Equal(remaining.Right - width, disconnected.Left);
        Assert.Equal(remaining.Top, disconnected.Top);
        Assert.Equal(remaining.Left, parked.Left);
        Assert.Equal(remaining.Top, parked.Top);
    }

    [Fact]
    public void Oversized_dashboard_restore_shrinks_to_the_remaining_work_area_instead_of_clipping()
    {
        var work = new Rect(0, 0, 700, 500);
        var restored = WindowPlacementService.ClampToWorkArea(new Rect(0, 0, 1240, 820), work);
        AssertFullyOnScreen(restored, work, "dashboard");
        Assert.Equal(work.Width, restored.Width);
        Assert.Equal(work.Height, restored.Height);
    }

    private static void AssertFullyOnScreen(Rect bounds, Rect workArea, string window)
    {
        Assert.True(bounds.Width >= 1, $"{window}: restored width must stay finite.");
        Assert.True(bounds.Height >= 1, $"{window}: restored height must stay finite.");
        Assert.InRange(bounds.Left, workArea.Left, workArea.Right - bounds.Width);
        Assert.InRange(bounds.Top, workArea.Top, workArea.Bottom - bounds.Height);
        Assert.True(bounds.Right <= workArea.Right, $"{window}: right edge {bounds.Right} exceeds work area {workArea.Right}.");
        Assert.True(bounds.Bottom <= workArea.Bottom, $"{window}: bottom edge {bounds.Bottom} exceeds work area {workArea.Bottom}.");
    }

    private static void AssertDpiContract(string manifestXml)
    {
        var xml = XDocument.Parse(manifestXml);
        XNamespace smi2005 = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
        XNamespace smi2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
        var dpiAware = xml.Descendants(smi2005 + "dpiAware").Select(node => Normalize(node.Value)).FirstOrDefault();
        var dpiAwareness = xml.Descendants(smi2016 + "dpiAwareness").Select(node => Normalize(node.Value)).FirstOrDefault();
        Assert.Equal("true/pm", dpiAware);
        Assert.Contains("PerMonitorV2", dpiAwareness ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PerMonitor", dpiAwareness ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"PerMonitorV2\s*,\s*PerMonitor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), dpiAwareness ?? "");
    }

    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string FindBuiltMuesliExecutable()
    {
        var candidates = new List<string>();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Muesli.exe"));
        candidates.Add(Path.Combine(FindRepositoryRoot(), "windows-native", "Muesli.Windows", "bin", configuration, "net10.0-windows", "Muesli.exe"));
        var match = candidates.FirstOrDefault(File.Exists);
        if (match is not null) return match;
        throw new FileNotFoundException(
            "Built Muesli.exe was not found next to the test host or in the matching Muesli.Windows output folder. Build the Windows app before this assertion.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static class Win32ManifestExtractor
    {
        private const uint LoadLibraryAsDatafile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;
        private static readonly IntPtr RtManifest = new(24);
        private static readonly IntPtr CreateProcessManifest = new(1);

        public static string Extract(string executablePath)
        {
            var module = LoadLibraryEx(executablePath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
                throw new InvalidOperationException($"LoadLibraryEx failed for '{Path.GetFileName(executablePath)}' (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                var resource = FindResource(module, CreateProcessManifest, RtManifest);
                if (resource == IntPtr.Zero)
                    throw new InvalidOperationException($"'{Path.GetFileName(executablePath)}' has no RT_MANIFEST (type 24, id 1) resource.");
                var size = SizeofResource(module, resource);
                var data = LoadResource(module, resource);
                if (size == 0 || data == IntPtr.Zero)
                    throw new InvalidOperationException($"RT_MANIFEST for '{Path.GetFileName(executablePath)}' could not be loaded.");
                var locked = LockResource(data);
                var bytes = new byte[size];
                Marshal.Copy(locked, bytes, 0, (int)size);
                return DecodeXml(bytes);
            }
            finally
            {
                FreeLibrary(module);
            }
        }

        private static string DecodeXml(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0)
                return Encoding.Unicode.GetString(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr hResData);

        [DllImport("kernel32.dll")]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
    }
}
