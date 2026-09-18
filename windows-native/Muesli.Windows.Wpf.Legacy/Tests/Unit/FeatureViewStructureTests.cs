using Muesli.Windows.Features;

namespace Muesli.Windows.Tests;

public sealed class FeatureViewStructureTests
{
    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows", "MainWindow.xaml")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the Muesli repository root from the test output directory.");
    }

    [Fact]
    public void Feature_views_are_real_user_controls_with_independent_files()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Features/Dictations/DictationsView.xaml"] = "Muesli.Windows.Features.Dictations.DictationsView",
            ["Features/Search/SearchView.xaml"] = "Muesli.Windows.Features.Search.SearchView",
            ["Features/Meetings/MeetingsView.xaml"] = "Muesli.Windows.Features.Meetings.MeetingsView",
            ["Features/Meetings/MeetingDetailView.xaml"] = "Muesli.Windows.Features.Meetings.MeetingDetailView",
            ["Features/Dictionary/DictionaryView.xaml"] = "Muesli.Windows.Features.Dictionary.DictionaryView",
            ["Features/Models/ModelsView.xaml"] = "Muesli.Windows.Features.Models.ModelsView",
            ["Features/Shortcuts/ShortcutsView.xaml"] = "Muesli.Windows.Features.Shortcuts.ShortcutsView",
            ["Features/Settings/SettingsView.xaml"] = "Muesli.Windows.Features.Settings.SettingsView",
            ["Features/Settings/ComputerUseSettingsView.xaml"] = "Muesli.Windows.Features.Settings.ComputerUseSettingsView",
            ["Features/About/AboutView.xaml"] = "Muesli.Windows.Features.About.AboutView"
        };

        foreach (var (relativePath, className) in expected)
        {
            var path = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing feature view: {relativePath}");
            var xaml = File.ReadAllText(path);
            Assert.True(xaml.Contains("<UserControl", StringComparison.Ordinal) ||
                        xaml.Contains(":FeatureViewBase", StringComparison.Ordinal),
                $"Feature view is not rooted in a WPF UserControl: {relativePath}");
            Assert.Contains($"x:Class=\"{className}\"", xaml);
        }
    }

    [Fact]
    public void Main_window_uses_a_shell_content_host_and_does_not_embed_page_roots()
    {
        var path = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "MainWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("x:Name=\"ContentHost\"", xaml);
        Assert.DoesNotContain("x:Name=\"DictationsPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"SearchPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"MeetingsPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"MeetingDetailView\"", xaml);
        Assert.DoesNotContain("x:Name=\"ModelsPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"SettingsPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"DictionaryPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"ShortcutsPage\"", xaml);
        Assert.DoesNotContain("x:Name=\"AboutPage\"", xaml);
    }

    [Fact]
    public void Feature_views_do_not_depend_on_the_main_window_concrete_type()
    {
        var root = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features");

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)))
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("MainWindow", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Main_window_preserves_the_shell_contracts_for_bindings_preview_and_themes()
    {
        var xamlPath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "MainWindow.xaml");
        var codePath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs");
        var runtimePath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        var runtime = File.ReadAllText(runtimePath);

        Assert.Contains("DataContext", code);
        Assert.Contains("AutomationProperties.Name=\"Navigate to Dictations\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Navigate to Meetings\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Navigate to Models\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Navigate to Settings\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Navigate to About\"", xaml);
        Assert.Contains("LightThemeButton", xaml);
        Assert.Contains("DarkThemeButton", xaml);
        Assert.Contains("ShowPreviewPage", runtime);
        Assert.Contains("DisableVisualPreviewActions", runtime);
    }

    [Fact]
    public void Settings_tabs_and_preview_pages_remain_explicit_contracts()
    {
        var xamlPath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features", "Settings", "SettingsView.xaml");
        var shellXamlPath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "MainWindow.xaml");
        var codePath = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var shellXaml = File.ReadAllText(shellXamlPath);
        var code = File.ReadAllText(codePath);

        foreach (var tab in new[] { "General", "Dictation", "Meetings", "Computer Use", "Appearance" })
            Assert.Contains($"Header=\"{tab}\"", xaml);

        Assert.Contains("ComputerUseSettingsView", xaml);

        foreach (var page in new[] { "dashboard", "meetings", "search", "dictionary", "models", "shortcuts", "settings", "about" })
            Assert.Contains($"case \"{page}\"", code);

        Assert.Contains("ContentHost", shellXaml);
    }

    [Fact]
    public void App_services_owns_feature_construction_and_disposal()
    {
        var appServices = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows",
            "Services",
            "AppServices.cs"));
        var runtime = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows",
            "Features",
            "Runtime",
            "FeatureRuntime.xaml.cs"));
        var shell = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows",
            "MainWindow.xaml.cs"));

        Assert.Contains("CreateFeatureScope", appServices);
        Assert.Contains("FeatureServiceScope", appServices);
        Assert.Contains("void Dispose()", appServices);
        Assert.Contains("CreateFeatureScope", runtime);
        Assert.DoesNotContain("new GlobalHotkeyService", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsStore", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("new AppDataStore", runtime, StringComparison.Ordinal);
        Assert.Contains("_appServices.Dispose()", shell);
    }

    [Fact]
    public void Important_workflows_have_awaitable_entry_points_behind_event_wrappers()
    {
        var root = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features", "Runtime");
        var runtime = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        Assert.Contains("Task StartDictationAsync", runtime);
        Assert.Contains("Task StopDictationAsync", runtime);
        Assert.Contains("Task ToggleMeetingRecordingAsync", runtime);
        Assert.Contains("Task DownloadSelectedModelAsync", runtime);
        Assert.Contains("Task StartComputerUseVoiceCaptureAsync", runtime);
    }

    [Fact]
    public void Every_declared_feature_action_has_a_runtime_handler()
    {
        var featureRoot = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features");
        var actionPattern = new System.Text.RegularExpressions.Regex(
            "FeatureAction\\.(?:Name|MouseUpName)=\\\"([^\\\"]+)\\\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var actionNames = Directory.EnumerateFiles(featureRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => actionPattern.Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var runtimeMethods = typeof(FeatureRuntime)
            .GetMethods(System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(actionNames);
        Assert.DoesNotContain(actionNames, actionName => !runtimeMethods.Contains(actionName));
    }

    [Fact]
    public void Every_forwarded_feature_event_declares_action_metadata()
    {
        var featureRoot = Path.Combine(RepositoryRoot, "windows-native", "Muesli.Windows", "Features");
        var forwardedElementPattern = new System.Text.RegularExpressions.Regex(
            "<[^>]+=\\\"Forward(?:Click|MouseButton|PasswordChanged|ValueChanged)\\\"[^>]*>",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (var path in Directory.EnumerateFiles(featureRoot, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match match in forwardedElementPattern.Matches(File.ReadAllText(path)))
            {
                Assert.True(
                    match.Value.Contains("FeatureAction.Name=", StringComparison.Ordinal) ||
                    match.Value.Contains("FeatureAction.MouseUpName=", StringComparison.Ordinal),
                    $"Forwarded event is missing feature action metadata in {Path.GetRelativePath(RepositoryRoot, path)}: {match.Value}");
            }
        }
    }

    [Fact]
    public void Every_shell_forwarder_has_a_runtime_handler()
    {
        var shellCode = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "windows-native",
            "Muesli.Windows",
            "MainWindow.xaml.cs"));
        var forwardedActionPattern = new System.Text.RegularExpressions.Regex(
            "(?:ForwardShellAction|HandleShellAction)\\(nameof\\(([^)]+)\\)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var actionNames = forwardedActionPattern.Matches(shellCode)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var runtimeMethods = typeof(FeatureRuntime)
            .GetMethods(System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic)
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(actionNames);
        Assert.DoesNotContain(actionNames, actionName => !runtimeMethods.Contains(actionName));
    }

    [Fact]
    public void Feature_actions_bubble_from_nested_feature_views()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parent = new ActionProbeView();
                var child = new ActionProbeView("NestedAction");
                parent.Content = child;
                FeatureActionEventArgs? received = null;
                parent.ActionRequested += (_, args) => received = args;

                child.Trigger();

                Assert.NotNull(received);
                Assert.Equal("NestedAction", received.ActionName);
                Assert.Same(child.Button, received.Sender);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new Xunit.Sdk.XunitException($"Nested feature action did not bubble: {failure}");
    }

    private sealed class ActionProbeView : FeatureViewBase
    {
        public ActionProbeView(string? actionName = null)
        {
            Button = new System.Windows.Controls.Button();
            if (actionName is not null)
            {
                FeatureAction.SetName(Button, actionName);
                Button.Click += ForwardClick;
                Content = Button;
            }
        }

        public System.Windows.Controls.Button Button { get; }

        public void Trigger() => Button.RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }
}
