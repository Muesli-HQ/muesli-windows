using Muesli.Windows.Core.Profiles;
using Muesli.Windows.Platform.Profiles;

namespace Muesli.Windows.Tests;

public sealed class WinUiMigrationFoundationTests
{
    [Fact]
    public void IsolatedProfileBuildsAllPathsUnderTheRequestedRoot()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);

        Assert.Equal(Path.GetFullPath(directory.Path), profile.RootDirectory);
        Assert.Equal(Path.Combine(profile.RootDirectory, "data"), profile.DataDirectory);
        Assert.Equal(Path.Combine(profile.RootDirectory, "captures"), profile.CaptureDirectory);
        Assert.Equal(Path.Combine(profile.RootDirectory, "logs"), profile.LogDirectory);
        Assert.Equal(Path.Combine(profile.RootDirectory, "windows-settings.json"), profile.SettingsPath);
    }

    [Fact]
    public void CurrentProfileHonorsProcessIsolatedRootOverride()
    {
        using var directory = new TestDirectory();
        var previous = Environment.GetEnvironmentVariable("MUESLI_PROFILE_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("MUESLI_PROFILE_ROOT", directory.Path);
            var current = MuesliProfilePaths.Current();
            Assert.Equal(Path.GetFullPath(directory.Path), current.RootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUESLI_PROFILE_ROOT", previous);
        }
    }

    [Fact]
    public void ProfileLeaseExcludesAnotherThreadAndCanBeReacquiredAfterRelease()
    {
        using var directory = new TestDirectory();
        var profile = MuesliProfilePaths.Isolated(directory.Path);
        var first = WindowsProfileLeaseFactory.Instance.TryAcquire(profile, TimeSpan.Zero);
        Assert.NotNull(first);

        IMuesliProfileLease? blocked = null;
        Exception? acquisitionError = null;
        var competingThread = new Thread(() =>
        {
            try
            {
                blocked = WindowsProfileLeaseFactory.Instance.TryAcquire(
                    profile,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (Exception exception)
            {
                acquisitionError = exception;
            }
        });
        competingThread.Start();
        competingThread.Join();

        Assert.Null(acquisitionError);
        Assert.Null(blocked);

        first.Dispose();
        var reacquired = WindowsProfileLeaseFactory.Instance.TryAcquire(profile, TimeSpan.Zero);
        Assert.NotNull(reacquired);
        reacquired.Dispose();
    }

    [Fact]
    public void SharedProjectsContainNoWpfReferences()
    {
        var root = FindRepositoryRoot();
        foreach (var projectName in new[]
                 {
                     "Muesli.Windows.Core",
                     "Muesli.Windows.Platform",
                     "Muesli.Windows.CommandHost",
                     "Muesli.Windows.WinUI"
                 })
        {
            var projectRoot = Path.Combine(root, "windows-native", projectName);
            // Bounded enumeration: source-only roots, no bin/obj/generated output, no reparse points.
            var sources = TestRepositoryLayout.EnumerateSourceFiles(Path.Combine("windows-native", projectName));
            foreach (var source in sources)
            {
                var text = File.ReadAllText(source);
                // WinForms is an acceptable platform adapter dependency (for example, NotifyIcon);
                // it is not WPF. Continue rejecting every other System.Windows namespace.
                var withoutWinForms = text.Replace("System.Windows.Forms", "", StringComparison.Ordinal);
                Assert.DoesNotContain("System.Windows", withoutWinForms, StringComparison.Ordinal);
                Assert.DoesNotContain("PresentationFramework", text, StringComparison.Ordinal);
                Assert.DoesNotContain("PresentationCore", text, StringComparison.Ordinal);
            }

            var project = File.ReadAllText(Path.Combine(projectRoot, $"{projectName}.csproj"));
            Assert.DoesNotContain("<UseWPF>true</UseWPF>", project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CoreIsFrameworkNeutralAndWinUiReferencesOnlySharedLayers()
    {
        var root = FindRepositoryRoot();
        var coreProject = File.ReadAllText(Path.Combine(
            root,
            "windows-native",
            "Muesli.Windows.Core",
            "Muesli.Windows.Core.csproj"));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", coreProject, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-windows", coreProject, StringComparison.OrdinalIgnoreCase);

        var winUiProject = File.ReadAllText(Path.Combine(
            root,
            "windows-native",
            "Muesli.Windows.WinUI",
            "Muesli.Windows.WinUI.csproj"));
        Assert.Contains("<UseWinUI>true</UseWinUI>", winUiProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Muesli.Windows.Core.csproj", winUiProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Muesli.Windows.Platform.csproj", winUiProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Muesli.Windows.csproj", winUiProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoreAndViewModelsContainNoUiFrameworkTypes()
    {
        var roots = new[]
        {
            Path.Combine("windows-native", "Muesli.Windows.Core"),
            Path.Combine("windows-native", "Muesli.Windows.WinUI", "ViewModels")
        };

        foreach (var sourceRoot in roots)
        {
            foreach (var source in TestRepositoryLayout.EnumerateSourceFiles(sourceRoot))
            {
                var text = File.ReadAllText(source);
                Assert.DoesNotContain("Microsoft.UI.Xaml", text, StringComparison.Ordinal);
                Assert.DoesNotContain("System.Windows", text, StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepositoryRoot() => TestRepositoryLayout.Root;
}
