namespace Muesli.Windows.UITests;

/// <summary>
/// Phase-preview visual capture stays on scripts/verify-phase12-ui.ps1.
/// Production-startup UI Automation in this project must never launch that matrix.
/// </summary>
public sealed class PhasePreviewIsolationTests
{
    [Fact]
    public void Phase12_preview_script_remains_the_preview_path()
    {
        var script = Path.Combine(TestPaths.RepositoryRoot, "scripts", "verify-phase12-ui.ps1");
        Assert.True(File.Exists(script), "scripts/verify-phase12-ui.ps1 must remain the Phase 12 preview capture path.");
        var text = File.ReadAllText(script);
        Assert.Contains("22 exact page/cases", text, StringComparison.Ordinal);
        Assert.Contains("--phase12-page-case", File.ReadAllText(
            Path.Combine(TestPaths.RepositoryRoot, "windows-native", "Muesli.Windows.Tests", "Phase12ProductExperienceTests.cs")));
    }

    [Fact]
    public void Production_harness_never_passes_phase12_preview_arguments()
    {
        var session = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "windows-native", "Muesli.Windows.UITests", "MuesliUiSession.cs"));
        var profile = File.ReadAllText(Path.Combine(
            TestPaths.RepositoryRoot, "windows-native", "Muesli.Windows.UITests", "MuesliCleanProfile.cs"));
        Assert.DoesNotContain("phase12", session, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArgumentList", session, StringComparison.Ordinal);
        Assert.Contains("LaunchProduction", session, StringComparison.Ordinal);
        Assert.Contains("ParkedProfile", profile, StringComparison.Ordinal);
        Assert.Contains("MarkerFileName", profile, StringComparison.Ordinal);
        Assert.Contains("GetFolderPath", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Screenshot_artifact_folder_is_created_by_the_harness()
    {
        Directory.CreateDirectory(UiScreenshot.ArtifactDirectory);
        Assert.True(Directory.Exists(UiScreenshot.ArtifactDirectory));
        Assert.Contains(
            Path.Combine("artifacts", "ui-automation"),
            UiScreenshot.ArtifactDirectory,
            StringComparison.OrdinalIgnoreCase);
    }
}
