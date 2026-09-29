using System.Diagnostics;

namespace Muesli.Windows.Tests;

/// <summary>
/// Regression cover for <c>scripts/select-packaged-layout.ps1</c>, the function the shipping
/// launcher uses to decide which packaged layout to run.
/// </summary>
/// <remarks>
/// The launcher previously selected any manifest whose directory was literally named "AppX" and
/// ranked by manifest timestamp. The build output also contains historical nested copies
/// (…\win-x64\AppX\AppX) that MSBuild stopped refreshing, so a successful build could still launch
/// an old Muesli.Windows.WinUI.dll — observed live, and it silently ran stale indicator code.
/// These tests drive the real PowerShell function against synthetic layouts.
/// </remarks>
public sealed class PackagedLayoutSelectorTests
{
    [Fact]
    public void The_freshly_built_layout_wins_over_a_nested_stale_copy()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;

        var fresh = CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow);
        var stale = CreateLayout(root, @"net10.0\win-x64\AppX\AppX", DateTime.UtcNow.AddDays(-3));

        var selected = RunSelector(root);

        Assert.Equal(fresh, selected, ignoreCase: true);
        Assert.NotEqual(stale, selected, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_recursively_nested_layout_is_rejected_even_when_it_is_the_newest()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;

        // The nested copy is deliberately the most recently written file on disk.
        var real = CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow.AddMinutes(-10));
        CreateLayout(root, @"net10.0\win-x64\AppX\AppX", DateTime.UtcNow);

        Assert.Equal(real, RunSelector(root), ignoreCase: true);
    }

    [Fact]
    public void A_layout_is_not_required_to_live_in_a_directory_called_AppX()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;
        var layout = CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow);

        Assert.Equal(layout, RunSelector(root), ignoreCase: true);
    }

    [Fact]
    public void A_manifest_without_its_payload_is_never_selected()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;

        var withPayload = CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow.AddMinutes(-10));

        // A manifest with no sibling DLL is not a runnable layout, however new it is.
        var orphan = Path.Combine(root, @"net10.0\win-x64\Orphan");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "AppxManifest.xml"), "<Package/>");

        Assert.Equal(withPayload, RunSelector(root), ignoreCase: true);
    }

    [Fact]
    public void A_build_that_produced_newer_output_refuses_to_launch_a_stale_layout()
    {
        // This is the defect the launcher shipped: build succeeds, layout is old, app runs anyway.
        using var directory = new TestDirectory();
        var root = directory.Path;
        CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow.AddHours(-2));

        var error = Assert.Throws<InvalidOperationException>(
            () => RunSelector(root, minimumDllWriteTimeUtc: DateTime.UtcNow));

        Assert.Contains("stale packaged layout", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_layout_from_the_current_build_passes_the_freshness_gate()
    {
        using var directory = new TestDirectory();
        var root = directory.Path;
        var layout = CreateLayout(root, @"net10.0\win-x64", DateTime.UtcNow);

        Assert.Equal(
            layout,
            RunSelector(root, minimumDllWriteTimeUtc: DateTime.UtcNow.AddSeconds(-30)),
            ignoreCase: true);
    }

    [Fact]
    public void An_output_root_with_no_layout_reports_that_clearly()
    {
        using var directory = new TestDirectory();
        var error = Assert.Throws<InvalidOperationException>(() => RunSelector(directory.Path));
        Assert.Contains("layout not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The packaged identity must declare the microphone device capability. Without it the audio
    /// stack denies WASAPI capture with E_ACCESSDENIED from
    /// <c>WasapiCapture.InitializeCaptureDevice</c> even when Windows' global microphone consent
    /// is allowed — observed live, and it made every packaged dictation fail.
    /// </summary>
    [Fact]
    public void The_package_manifest_declares_the_microphone_device_capability()
    {
        var manifest = Path.Combine(
            RepositoryRoot, "windows-native", "Muesli.Windows.WinUI", "Package.appxmanifest");
        Assert.True(File.Exists(manifest), $"Package manifest not found at {manifest}.");

        var document = System.Xml.Linq.XDocument.Load(manifest);
        var foundation = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/appx/manifest/foundation/windows10";

        var declared = document
            .Descendants(foundation + "DeviceCapability")
            .Select(element => (string?)element.Attribute("Name"))
            .ToList();

        Assert.Contains("microphone", declared);
    }

    private static string CreateLayout(string root, string relative, DateTime writeTimeUtc)
    {
        var directory = Path.Combine(root, relative);
        Directory.CreateDirectory(directory);

        var manifest = Path.Combine(directory, "AppxManifest.xml");
        var payload = Path.Combine(directory, "Muesli.Windows.WinUI.dll");
        File.WriteAllText(manifest, "<Package/>");
        File.WriteAllText(payload, "payload");
        File.SetLastWriteTimeUtc(payload, writeTimeUtc);
        File.SetLastWriteTimeUtc(manifest, writeTimeUtc);
        return manifest;
    }

    /// <summary>Invokes the real PowerShell selector and returns the manifest it chose.</summary>
    private static string RunSelector(string outputRoot, DateTime? minimumDllWriteTimeUtc = null)
    {
        var script = Path.Combine(RepositoryRoot, "scripts", "select-packaged-layout.ps1");
        Assert.True(File.Exists(script), $"Selector script not found at {script}.");

        // Ticks, not a formatted string: PowerShell's [datetime]::Parse converts a UTC string to
        // local time, so re-labelling the result as UTC would shift it by the machine's offset.
        var minimum = minimumDllWriteTimeUtc is { } stamp
            ? $" -MinimumDllWriteTimeUtc ([datetime]::new({stamp.Ticks}L, [System.DateTimeKind]::Utc))"
            : "";

        var command =
            $". '{script}'; " +
            $"Select-MuesliPackagedLayout -OutputRoot '{outputRoot}'{minimum}";

        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit(60_000);

        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? $"Selector failed with exit code {process.ExitCode}." : error);
        }

        return output;
    }

    /// <summary>
    /// Walks up from the test binaries to the repository root. Deliberately anchored on the
    /// selector script rather than on a project directory name, so a project rename cannot break
    /// discovery the way it did for the packaging tests.
    /// </summary>
    private static string RepositoryRoot => TestRepositoryLayout.Root;
}
