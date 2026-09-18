using Muesli.Windows.Services;
using Muesli.Windows;
using System.Windows;
using System.Windows.Media;

namespace Muesli.Windows.Tests;

public sealed class Phase12ProductExperienceTests
{
    [Fact]
    public void Selection_transitions_clear_only_the_verification_evidence_they_invalidate()
    {
        var progress = new OnboardingProgress
        {
            MicrophoneVerified = true, ModelsVerified = true, HotkeyVerified = true, PipelineVerified = true,
            VerifiedMicrophone = "mic-a", VerifiedDictationModelId = "dictation-a", VerifiedFinalModelId = "final-a",
            VerifiedLiveModelId = "live-a", VerifiedHotkey = "F8"
        };
        var original = new OnboardingSelection("mic-a", "dictation-a", "final-a", "live-a", "F8");

        var microphoneChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { Microphone = "mic-b" });
        Assert.False(microphoneChanged.MicrophoneVerified);
        Assert.False(microphoneChanged.PipelineVerified);
        Assert.True(microphoneChanged.ModelsVerified);
        Assert.True(microphoneChanged.HotkeyVerified);
        Assert.Equal("", microphoneChanged.VerifiedMicrophone);

        var modelChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { LiveModelId = null });
        Assert.False(modelChanged.ModelsVerified);
        Assert.False(modelChanged.PipelineVerified);
        Assert.Equal("", modelChanged.VerifiedDictationModelId);
        Assert.Equal("", modelChanged.VerifiedFinalModelId);
        Assert.Null(modelChanged.VerifiedLiveModelId);

        var hotkeyChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { Hotkey = "F9" });
        Assert.False(hotkeyChanged.HotkeyVerified);
        Assert.Equal("", hotkeyChanged.VerifiedHotkey);
        Assert.True(hotkeyChanged.PipelineVerified);
        Assert.Equal(progress, OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original));
    }

    [Fact]
    public void Candidate_hook_result_remains_decisive_when_previous_hook_restoration_fails()
    {
        var restored = false;
        var result = HotkeyCandidateHookTest.Run(() => true, () => { restored = true; return false; });
        Assert.True(restored);
        Assert.True(result.CandidateRegistered);
        Assert.False(result.PreviousHookRestored);
    }

    [Theory]
    [InlineData("F8", "F8", true)]
    [InlineData("F8", "F9", false)]
    public void Reconciliation_invalidates_hotkey_when_selection_changes(string verified, string selected, bool expected)
    {
        var progress = new OnboardingProgress { HotkeyVerified = true, VerifiedHotkey = verified };
        var result = OnboardingProgressReconciler.Reconcile(progress, new("mic", "d", "f", null, selected), _ => true, _ => true);
        Assert.Equal(expected, result.HotkeyVerified);
    }

    [Fact]
    public void Reconciliation_requires_current_model_lifecycle_readiness()
    {
        var progress = new OnboardingProgress { ModelsVerified = true, PipelineVerified = true, VerifiedMicrophone = "mic", MicrophoneVerified = true, VerifiedDictationModelId = "d", VerifiedFinalModelId = "f" };
        var result = OnboardingProgressReconciler.Reconcile(progress, new("mic", "d", "f", null, "F8"), id => id == "d", _ => true);
        Assert.False(result.ModelsVerified); Assert.False(result.PipelineVerified);
    }

    [Theory]
    [InlineData(unchecked((int)0x8889000A), MicrophoneProbeFailure.Busy)]
    [InlineData(unchecked((int)0x88890004), MicrophoneProbeFailure.Disconnected)]
    [InlineData(unchecked((int)0x80070005), MicrophoneProbeFailure.Denied)]
    [InlineData(unchecked((int)0x80004005), MicrophoneProbeFailure.Unknown)]
    public void Microphone_hresult_mapping_is_pure(int hresult, MicrophoneProbeFailure expected) =>
        Assert.Equal(expected, WindowsMicrophoneAccessService.ClassifyFailure(new System.Runtime.InteropServices.COMException("test", hresult)));

    [Fact]
    public void Progress_serialization_never_includes_transcript_or_secrets()
    {
        var progress = new OnboardingProgress { LastStatus = "Pipeline test passed", VerifiedMicrophone = "mic" };
        var json = System.Text.Json.JsonSerializer.Serialize(progress);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Feature_tour_version_is_a_settings_authoritative_non_secret_choice()
    {
        var settings = new MuesliSettings { LastCompletedFeatureTourVersion = FeatureTourWindow.CurrentVersion };
        Assert.Equal(FeatureTourWindow.CurrentVersion, settings.LastCompletedFeatureTourVersion);
    }

    [Fact]
    public void Phase12_xaml_contracts_include_focus_resume_and_responsive_floor()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "App.xaml"));
        var main = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml"));
        var dictations = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Dictations", "DictationsView.xaml"));
        var onboarding = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "OnboardingWindow.xaml"));
        Assert.Contains("MuesliFocusVisual", app);
        Assert.Contains("MinWidth=\"760\"", main);
        Assert.Contains("Resume setup", dictations);
        Assert.Contains("AutomationProperties", onboarding);
        foreach (var style in new[] { "ThemeSegmentButton", "MuesliTextBox", "MuesliCheckBox", "MuesliTabItem", "SidebarButton", "PrimaryButton", "SecondaryButton", "ChromeButton", "MuesliComboBox", "GhostButton", "MuesliListBoxItem" })
        {
            var start = app.IndexOf($"x:Key=\"{style}\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing named style {style}.");
            Assert.Contains("FocusVisualStyle", app.Substring(start, Math.Min(700, app.Length - start)));
        }
    }

    [Fact]
    public void Onboarding_contract_routes_each_model_role_through_real_lifecycle_callbacks()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "OnboardingWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs"));
        var runtime = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Dictations.cs"));
        Assert.DoesNotContain("private void ShowLegacyOnboardingForm", main);
        Assert.DoesNotContain("private Border BuildOnboardingRow", main);
        Assert.Contains("PrepareOfflineModelAsync", onboarding);
        Assert.Contains("PrepareLiveModelAsync", onboarding);
        Assert.Contains("CancellationToken", onboarding);
        Assert.Contains("CancelLiveModel", onboarding);
        Assert.Contains("_streamingModelLifecycle.PrepareAsync", runtime);
        Assert.Contains("_modelLifecycle.PrepareAsync", runtime);
    }

    [Fact]
    public void Onboarding_optional_and_compact_accessibility_contracts_are_present()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "OnboardingWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs")) +
                   Environment.NewLine +
                   File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Dictations.cs")) +
                   Environment.NewLine +
                   File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Navigation.cs"));
        Assert.Contains("SummaryProviderDisclosure.DisclosureFor", onboarding);
        Assert.Contains("PreviewIndicator", onboarding);
        Assert.Contains("ResetIndicatorPosition", onboarding);
        Assert.Contains("IsCompactLayout", main);
        Assert.Contains("SizeChanged=\"Window_SizeChanged\"", main);
        Assert.Contains("AutomationProperties.Name=\"Navigate to Dictations\"", main);
        Assert.Contains("AutomationProperties.Name=\"Close Muesli window\"", main);
        Assert.Contains("x:Key=\"CompactSidebarLabel\"", main);
        Assert.Contains("Binding=\"{Binding DataContext.IsCompactLayout", main);
        Assert.Contains("Style=\"{StaticResource CompactSidebarLabel}\"", main);
        Assert.DoesNotContain("Retired onboarding implementation", code);
        Assert.DoesNotContain("RetiredOnboardingForm", code);
        Assert.DoesNotContain("RetiredOnboardingRow", code);
        Assert.Contains("Window_SizeChanged", code);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "windows-native", "Muesli.Windows"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
    [Fact]
    public void Preview_mode_is_opt_in_and_uses_safe_defaults()
    {
        Assert.False(Phase12PreviewMode.TryParse([], out _));
        Assert.True(Phase12PreviewMode.TryParse(["--phase12-preview=offline"], out var mode));
        Assert.Equal("offline", mode.Scenario);
        Assert.Equal("light", mode.Theme);
        Assert.Equal("normal", mode.Size);
        Assert.True(mode.IsValid);
    }

    [Theory]
    [MemberData(nameof(AllPreviewCases))]
    public void Preview_parser_accepts_each_documented_case_in_memory(string pageCase)
    {
        Assert.True(Phase12PreviewMode.TryParse([$"--phase12-page-case={pageCase}", "--phase12-theme=dark", "--phase12-size=narrow"], out var mode));
        Assert.True(mode.IsValid);
        Assert.Equal(pageCase, $"{mode.Page}/{mode.Case}");
        Assert.Equal("dark", mode.Theme);
        Assert.Equal("narrow", mode.Size);
        Assert.True(mode.BlocksProductionActions);
    }

    public static IEnumerable<object[]> AllPreviewCases() => Phase12PreviewMode.Cases.Select(item => new object[] { $"{item.Page}/{item.Case}" });

    [Fact]
    public void Permissions_denied_preview_uses_the_actual_microphone_permission_step()
    {
        Assert.True(Phase12PreviewMode.TryParse(["--phase12-page-case=onboarding/permissions-denied"], out var mode));
        Assert.Equal(1, mode.OnboardingStep);
        Assert.Equal("failure", mode.StateCategory);
        Assert.Contains("FAILURE", mode.PageStateMessage);
    }

    [Fact]
    public void Preview_parser_keeps_invalid_preview_arguments_out_of_production_startup()
    {
        Assert.True(Phase12PreviewMode.TryParse(["--phase12-preview=not-a-scenario"], out var mode));
        Assert.False(mode.IsValid);
        Assert.Contains("Unsupported", mode.Diagnostic);
    }

    [Fact]
    public void Preview_matrix_has_the_complete_128_visual_cells()
    {
        Assert.Equal(22, Phase12PreviewMode.Cases.Count);
        Assert.Equal(2, Phase12PreviewMode.Themes.Count);
        Assert.Equal(2, Phase12PreviewMode.Sizes.Count);
        Assert.Equal(352, new Phase12PreviewMode("dashboard", "empty", "light", "normal").MatrixCellCount);
    }

    [Fact]
    public void Preview_isolation_and_lazy_logging_are_source_contracts()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs"));
        var runtime = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.xaml.cs"));
        var runtimeNavigation = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Navigation.cs"));
        var preview = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "Phase12PreviewMode.cs"));
        var shell = main + Environment.NewLine + runtime + Environment.NewLine + runtimeNavigation;
        Assert.Contains("private Services.AppLogService? _logService", app);
        Assert.Contains("if (Services.Phase12PreviewMode.TryParse(e.Args, out var preview))", app);
        Assert.DoesNotContain("private readonly Services.AppLogService _logService = new();", app);
        Assert.Contains("no store, filesystem, registry,", preview);
        Assert.Contains("IsPreview: true", preview);
        Assert.Contains("MainWindow.CreateVisualPreview", preview);
        Assert.Contains("no store, filesystem, registry, network", preview);
        Assert.Contains("_isVisualPreview", main);
        Assert.Contains("_isVisualPreview ? \"Preview-only: startup registration was not inspected.\"", runtime);
        var previewConstructor = runtime[runtime.IndexOf("private FeatureRuntime(IFeatureShellContext shell, AppServices appServices, Phase12PreviewMode mode)", StringComparison.Ordinal)..runtime.IndexOf("private void ShowPreviewPage", StringComparison.Ordinal)];
        foreach (var forbiddenConstruction in new[] { "new SettingsStore", "new AppDataStore", "new OnboardingProgressStore", "new AppLogService", "new DictationCoordinator", "new NativeTranscriptionClient", "new TrayIconService", "SystemEvents.", "StartupRegistrationService", "new HttpClient", "new ComputerUseTraceStore" })
            Assert.DoesNotContain(forbiddenConstruction, previewConstructor);
        Assert.Contains("_isVisualPreview", runtime);
        Assert.Contains("DisableVisualPreviewActions", runtime);
        Assert.Contains("Preview-only: startup repair is disabled", shell);
        var featureXaml = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(Path.Combine(root, "windows-native", "Muesli.Windows", "Features"), "*.xaml", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.Contains("PreviewPageStateMessage", featureXaml);
        Assert.Contains("VisualVerificationBanner", File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml")));
    }

    [Fact]
    public void Visual_capture_manifest_contract_records_actual_dpi_monitor_bounds_and_png()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-phase12-ui.ps1"));
        Assert.Contains("GetDpiForWindow", script);
        Assert.Contains("RequiredScale $RequiredScale% requires $requiredDpi DPI", script);
        Assert.Contains("actualDpi", script);
        Assert.Contains("windowBounds", script);
        Assert.Contains("isolationMarker", script);
        Assert.Contains("CopyFromScreen", script);
        Assert.Contains("Refusing to write outside OutputDirectory", script);
        Assert.Contains("foregroundDeadline", script);
        Assert.Contains("22 exact page/cases", script);
        Assert.Contains("pageCase", script);
        Assert.Contains("CaptureAllForCurrentScale", script);
    }

    [Fact]
    public void Visual_capture_case_table_matches_every_preview_case_and_category()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-phase12-ui.ps1"));
        var entries = System.Text.RegularExpressions.Regex.Matches(
                script,
                @"\[pscustomobject\]@\{\s*Page\s*=\s*'(?<page>[^']+)';\s*Case\s*=\s*'(?<case>[^']+)';\s*StateCategory\s*=\s*'(?<category>[^']+)'\s*\}",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(match => new
            {
                Key = $"{match.Groups["page"].Value}/{match.Groups["case"].Value}",
                Category = match.Groups["category"].Value
            })
            .ToList();
        var expected = Phase12PreviewMode.Cases.ToDictionary(item => $"{item.Page}/{item.Case}", item => item.StateCategory, StringComparer.Ordinal);

        Assert.Equal(22, entries.Count);
        Assert.Equal(entries.Count, entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Count, entries.Count);
        foreach (var entry in entries)
        {
            Assert.True(expected.TryGetValue(entry.Key, out var category), $"Script case '{entry.Key}' is not a Phase12PreviewMode case.");
            Assert.Equal(category, entry.Category);
        }
        Assert.Contains("$caseByPageCase[$PageCase].StateCategory", script);
        Assert.DoesNotContain("switch -Wildcard ($PageCase)", script);
    }

    [Fact]
    public void Windows_app_configures_per_monitor_v2_once_through_the_project_property()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Muesli.Windows.csproj"));
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project);
        Assert.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", project);
        Assert.DoesNotContain("SetHighDpiMode", File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "App.xaml.cs")));
        // Built RT_MANIFEST dpiAware/dpiAwareness assertions live in DpiManifestAndPlacementTests.
    }

    [Fact]
    public void Resume_progress_clamps_steps_and_never_carries_secret_fields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"muesli-phase12-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "onboarding-progress.json");
            var store = new OnboardingProgressStore(path);
            store.Save(new OnboardingProgress { Step = 999, Deferred = true, LastStatus = "model download paused" });
            var loaded = store.Load();
            Assert.Equal(OnboardingProgress.LastStep, loaded.Step);
            Assert.True(loaded.Deferred);
            Assert.Equal("model download paused", loaded.LastStatus);
            var persisted = File.ReadAllText(path);
            Assert.DoesNotContain("apiKey", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcript", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Window_placement_converts_pixel_corners_and_clamps_negative_monitor_bounds()
    {
        var at150 = WindowPlacementService.PixelRectToDipRect(new WindowPlacementService.PixelRect(-1920, 0, 0, 1080), new Matrix(1d / 1.5, 0, 0, 1d / 1.5, 0, 0), new WindowPlacementService.ScreenCoordinateAnchor(new(-1600, 0), new(-1200, 0)));
        Assert.Equal(-1413.333, at150.Left, 3); // This would be -1280 with the rejected absolute-coordinate division.
        Assert.Equal(720, at150.Height);
        var work = new Rect(-1280, 0, 1280, 720);
        var restored = WindowPlacementService.ClampToWorkArea(new Rect(-1500, -20, 1800, 900), work);
        Assert.Equal(work.Left, restored.Left);
        Assert.Equal(work.Top, restored.Top);
        Assert.Equal(work.Width, restored.Width);
        Assert.Equal(work.Height, restored.Height);
    }

    [Theory]
    [InlineData(1.25, 1536, 864, 1228.8, 691.2)]
    [InlineData(1.5, 1920, 1080, 1280, 720)]
    [InlineData(2, 3840, 2160, 1920, 1080)]
    public void Window_placement_supports_common_mixed_dpi_scales(double scale, int right, int bottom, double expectedWidth, double expectedHeight)
    {
        var converted = WindowPlacementService.PixelRectToDipRect(new WindowPlacementService.PixelRect(0, 0, right, bottom), new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0), new WindowPlacementService.ScreenCoordinateAnchor(new(0, 0), new(0, 0)));
        Assert.Equal(expectedWidth, converted.Width, 3);
        Assert.Equal(expectedHeight, converted.Height, 3);
    }

    [Fact]
    public void Toast_placement_requires_the_windows_own_connected_presentation_source_before_screen_conversion()
    {
        var root = FindRepositoryRoot();
        var placement = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "WindowPlacementService.cs"));
        // Git checks these files out with CRLF on Windows (core.autocrlf), so the ordering
        // assertions below must compare against normalised line endings rather than failing
        // on a clean clone.
        var toast = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "ToastNotificationService.cs"))
            .Replace("\r\n", "\n");
        Assert.Contains("GetConnectedPresentationSource(window)?.CompositionTarget", placement);
        Assert.Contains("PresentationSource.FromVisual(window) as HwndSource", placement);
        Assert.Contains("HwndSource.FromHwnd(handle)", placement);
        Assert.Contains("_window.Show();\n            PositionWindow(_window);", toast);
        Assert.DoesNotContain("PositionWindow(_window);\n        _window.Show();", toast);
    }

    [Fact]
    public void Compact_sidebar_updates_the_named_column_and_keeps_the_settings_tab_host_within_the_narrow_content_area()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs")) +
                   Environment.NewLine +
                   File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Navigation.cs"));
        var settings = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Settings", "SettingsView.xaml"));
        Assert.Contains("x:Name=\"SidebarColumn\" Width=\"260\" MinWidth=\"72\"", xaml);
        Assert.DoesNotContain("<ColumnDefinition.Style>", xaml);
        Assert.Contains("SidebarColumn.Width = new GridLength(value ? 72 : 260);", File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs")));
        Assert.Contains("Window_SizeChanged", code);
        Assert.Contains("<TabControl x:Name=\"SettingsTabs\"", settings);
        Assert.Contains("Style=\"{StaticResource MuesliTabControl}\"", settings);
    }

    [Fact]
    public void Compact_rail_keeps_search_functional_and_hides_only_informational_header_copy()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Search.cs"));
        var search = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Search", "SearchView.xaml"));
        Assert.Contains("x:Key=\"CompactHeaderText\"", xaml);
        Assert.Contains("x:Key=\"CompactHeaderSearchSurface\"", xaml);
        Assert.Contains("x:Key=\"CompactOnlySearchButton\"", xaml);
        Assert.Contains("RelativeSource={RelativeSource AncestorType=Window}", xaml);
        Assert.Contains("OpenSearchFromCompactRail_Click", xaml);
        Assert.Contains("AutomationProperties.Name=\"Open search and focus the search field\"", xaml);
        Assert.Contains("x:Name=\"MainSearchInput\"", search);
        Assert.Contains("AutomationProperties.Name=\"Search dictations and meetings\"", xaml);
        Assert.Contains("x:Key=\"CompactThemeSwitch\"", xaml);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Left\" />", xaml);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"24,12,0,0\" />", xaml);
        Assert.DoesNotContain("<Border Margin=\"24,12,0,0\" Style=\"{StaticResource CompactThemeSwitch}\"", xaml);
        Assert.Contains("x:Key=\"NormalOnlyRailAction\"", xaml);
        Assert.Contains("x:Key=\"CompactOnlyCreateFolder\"", xaml);
        Assert.True(CountOccurrences(xaml, "Click=\"AddMeetingFolder_Click\"") >= 2);
        Assert.DoesNotContain("<Setter Property=\"Padding\" Value=\"26,0\" />", xaml);
        Assert.True(CountOccurrences(xaml, "<Setter Property=\"HorizontalContentAlignment\" Value=\"Center\" />") >= 2);
        Assert.Contains("MainSearchInput.Focus();", code);
        Assert.Contains("Keyboard.Focus(MainSearchInput);", code);
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        for (var index = value.IndexOf(fragment, StringComparison.Ordinal); index >= 0; index = value.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void Cancellation_disclosure_startup_and_overlay_contracts_are_present()
    {
        var root = FindRepositoryRoot();
        var onboarding = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "OnboardingWindow.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "MainWindow.xaml.cs"));
        var runtimeShell = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.xaml.cs"));
        var workflow = main + Environment.NewLine + runtimeShell + Environment.NewLine +
                       File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Dictations.cs")) +
                       Environment.NewLine +
                       File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Features", "Runtime", "FeatureRuntime.Navigation.cs"));
        var prompt = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "MeetingPromptService.cs"));
        var tray = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows", "Services", "TrayIconService.cs"));
        Assert.Contains("Func<CancellationToken, Task<string>>", onboarding);
        Assert.Contains("Cancel dictation test", onboarding);
        Assert.Contains("value.Percent", onboarding);
        Assert.Contains("_context.OllamaEndpoint", onboarding);
        Assert.Contains("Configure summary provider in Settings", onboarding);
        Assert.Contains("OpenSummaryProviderSettings", onboarding);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)", workflow);
        Assert.Contains("StopForOnboardingTestAsync(cancellationToken)", workflow);
        Assert.DoesNotContain("LogTranscriptionResult(\"onboarding dictation test\"", main);
        var coordinator = File.ReadAllText(Path.Combine(root, "windows-native", "Muesli.Windows.Platform", "Services", "DictationCoordinator.cs"));
        Assert.Contains("StopForOnboardingTestAsync", coordinator);
        Assert.Contains("keepLatestDictationAlias: false", coordinator);
        Assert.Contains("StartupRegistrationService.IsEnabled()", workflow);
        Assert.Contains("Window_Activated", workflow);
        Assert.Contains("SystemParameters.ClientAreaAnimation", prompt);
        Assert.Contains("WindowPlacementService.GetWorkAreaForCursor", prompt);
        Assert.Contains("_window.Opacity = 0", prompt);
        Assert.Contains("Show();", prompt);
        Assert.Contains("Show first so placement has a real HWND", workflow);
        Assert.Contains("previous?.Dispose()", tray);
        Assert.Contains("snapshotFailure", tray);
        Assert.Contains("explicitResume", workflow);
        Assert.Contains("savedProgress.Deferred && !explicitResume", workflow);
    }
}
