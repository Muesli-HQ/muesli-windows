using System.Drawing;
using System.Text.Json;

namespace Muesli.Windows.UITests;

/// <summary>
/// WinUI 3 shell qualification: every navigation destination renders, the shared search box
/// routes to results, single-instance activation holds, and package-identity-only features
/// report themselves as unavailable rather than pretending to work.
/// </summary>
[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class WinUiShellQualificationTests
{
    /// <summary>
    /// page key, navigation automation id, an automation id only that page publishes.
    /// </summary>
    private static readonly (string Page, string Navigation, string Evidence)[] Destinations =
    [
        ("timeline", "NavTimeline", "TimelineRefreshButton"),
        ("dictations", "NavDictations", "DictationRecordButton"),
        ("meetings", "NavMeetings", "QuickNoteButton"),
        // P7-06 moved the three unlabelled share-image glyph buttons onto the Share split
        // button's menu, so InsightsPreviewShareButton now lives in a flyout popup and is only
        // realized while that menu is open. The split button itself is the always-present,
        // Insights-only control, so it is the page sentinel.
        ("insights", "NavInsights", "InsightsShareButton"),
        ("dictionary", "NavDictionary", "DictionaryAddButton"),
        ("models", "NavModels", "ModelsRefreshButton"),
        ("shortcuts", "NavShortcuts", "ChangePushToTalkButton"),
        ("settings", "NavSettings", "SettingsSaveButton"),
        // Prompt 7 moved the raw diagnostics dump into a collapsed Expander ("keep technical
        // diagnostics accessible but secondary"), and a collapsed Expander keeps its content out
        // of the UIA tree, so RuntimeDiagnosticsText is not realized on arrival. The diagnostics
        // card's Refresh button is the About-only control that is always published; the dump
        // itself is asserted, expanded, by About_diagnostics_detail_is_reachable_behind_its_disclosure.
        ("about", "NavAbout", "RefreshDiagnosticsButton")
    ];

    [WinUiAutomationFact]
    public void Shell_navigates_every_destination_and_publishes_its_controls() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            session.RequireAutomationId("MainNavigation", mustBeOnscreen: true);
            session.RequireAutomationId("HistorySearchBox", mustBeOnscreen: true);
            session.RequireAutomationId("SidebarGreeting");

            foreach (var (page, navigation, evidence) in Destinations)
            {
                session.NavigateTo(page, navigation, evidence);
                session.CaptureReference($"winui-{page}", page, evidence);
            }

            Assert.Equal(
                Destinations.Select(destination => destination.Page).ToArray(),
                session.VisitedPages.ToArray());
        });
    });

    /// <summary>
    /// Captures the eleven macOS reference states against one populated, deterministic profile.
    /// The three settings and two insights states are intentionally captured separately because
    /// they are distinct visual destinations in the reference set, even though each shares a page.
    /// </summary>
    [WinUiAutomationFact]
    public void Populated_profile_captures_all_reference_states_and_secondary_surfaces() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchPopulated();
        session.Run(() =>
        {
            foreach (var (width, height) in new[] { (1280, 820), (1440, 900), (1600, 900) })
            {
                session.ResizeDips(width, height);
                var actualSize = session.ReadEffectiveDipSize();
                // UIA reports the client/content rectangle while MoveWindow sizes the outer
                // frame, so the title bar contributes a small platform-owned delta.
                // A maximized/edge-constrained desktop can reserve a small non-client margin at
                // the largest requested width; keep the assertion focused on a correctly scaled
                // resize rather than failing on work-area clamping.
                Assert.InRange(actualSize.Width, width - 64, width + 8);
                Assert.InRange(actualSize.Height, height - 32, height + 8);
                session.NavigateTo("timeline", "NavTimeline", "TimelineList");
                session.CaptureReference($"winui-populated-timeline-{width}", "timeline", "TimelineList");
            }
            session.AssertKeyboardFocusTraversal("NavTimeline", "NavDictations", "HistorySearchBox");
            File.WriteAllText(
                Path.Combine(UiScreenshot.ArtifactDirectory, "winui-qualification-capabilities.json"),
                JsonSerializer.Serialize(new
                {
                    launchHost = "packaged (winapp run --no-restore --detach --json --property Platform=x64; isolated profile via --profile-root)",
                    requestedEffectiveDips = new[] { new[] { 1280, 820 }, new[] { 1440, 900 }, new[] { 1600, 900 } },
                    observedDpiScale = session.CurrentDpiScale,
                    highContrast = session.HighContrastAutomationAvailable,
                    highContrastNote = "Not exercised: this harness does not change the interactive desktop contrast theme.",
                    referenceDirectory = "docs/ui-reference/macos-current-2026-08-27",
                    artifactPolicy = "artifacts/ui-automation"
                }, new JsonSerializerOptions { WriteIndented = true }));

            session.NavigateTo("dictations", "NavDictations", "DictationHistoryList");
            session.CaptureReference("winui-populated-dictations", "dictations", "DictationHistoryList");
            session.NavigateTo("meetings", "NavMeetings", "MeetingHistoryList");
            session.CaptureReference("winui-populated-meetings", "meetings", "MeetingHistoryList");
            session.NavigateTo("models", "NavModels", "ModelsRefreshButton");
            session.CaptureReference("winui-populated-models", "models", "ModelsRefreshButton");
            session.NavigateTo("shortcuts", "NavShortcuts", "ChangePushToTalkButton");
            session.CaptureReference("winui-populated-shortcuts", "shortcuts", "ChangePushToTalkButton");
            session.NavigateTo("dictionary", "NavDictionary", "DictionaryEntryList");
            session.RequireAutomationId("DictionarySuggestionList", mustBeOnscreen: true);
            session.CaptureReference("winui-populated-dictionary", "dictionary", "DictionaryEntryList");

            session.NavigateTo("insights", "NavInsights", "InsightsHeroCard");
            session.RequirePageSentinel("insights", "InsightsOverviewCard");
            session.CaptureReference("winui-populated-insights-overview", "insights", "InsightsHeroCard");
            // SelectorBar exposes its selectable child item through UIA, while the container is
            // not itself Invoke/SelectionItem-capable. Use the stable accessible name published
            // by the meetings item rather than attempting to select the container.
            session.RequireAutomationId("InsightsActivityModePicker", mustBeOnscreen: true);
            session.SelectAccessibleName("Show meetings activity");
            session.RequirePageSentinel("insights", "InsightsHeatmap");
            session.RequirePageSentinel("insights", "InsightsOverviewCard");
            session.CaptureReference("winui-populated-insights-detail", "insights", "InsightsHeatmap");

            session.NavigateTo("settings", "NavSettings", "SettingsUserName");
            session.CaptureReference("winui-populated-settings-general", "settings", "SettingsUserName");
            session.SelectAutomationId("SettingsDictationTab");
            session.RequireAutomationId("DictationModelPicker", mustBeOnscreen: true);
            session.CaptureReference("winui-populated-settings-dictation", "settings-dictation", "DictationModelPicker");
            session.SelectAutomationId("SettingsMeetingsTab");
            session.RequireAutomationId("MeetingModelPicker", mustBeOnscreen: true);
            session.CaptureReference("winui-populated-settings-meetings", "settings-meetings", "MeetingModelPicker");

            // Secondary surfaces reachable without external apps or credentials.
            session.NavigateTo("meetings", "NavMeetings", "MeetingHistoryList");
            session.SelectAutomationId("ManageMeetingTemplatesButton");
            session.RequireAutomationId("MeetingTemplateList", mustBeOnscreen: true);
            session.CaptureReference("winui-secondary-meeting-templates", "meeting-templates", "MeetingTemplateList");
            session.SelectAccessibleName("Back to meetings");
            session.SetValueAutomationId("HistorySearchBox", "Product planning");
            session.RequireAutomationId("SearchResultList", mustBeOnscreen: true);
            session.CaptureReference("winui-secondary-search", "search", "SearchResultList");
            session.SelectAccessibleName("Product planning · Today");
            // TEST-01. Meeting detail opens on the Notes tab, so the transcript editor is not in
            // the tree yet; the title editor is what proves the detail page actually opened. The
            // transcript editor is still required, just after the step that reveals it.
            session.RequireAutomationId("MeetingTitleEditor", mustBeOnscreen: true);
            session.SelectAutomationId("MeetingTranscriptTab");
            session.RequireAutomationId("MeetingTranscriptEditor", mustBeOnscreen: true);
            session.CaptureReference("winui-secondary-meeting-detail", "meeting-detail", "MeetingTranscriptEditor");
        });
    });

    [WinUiAutomationFact]
    public void Populated_profile_captures_all_reference_states_in_dark_and_light() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchPopulated();
        session.Run(() =>
        {
            CapturePopulatedReferenceSet(session, "Dark");
            CapturePopulatedReferenceSet(session, "Light");
        });
    });

    private static void CapturePopulatedReferenceSet(MuesliWinUiSession session, string theme)
    {
        session.NavigateTo("settings", "NavSettings", "SettingsUserName");
        session.SelectAutomationId("SettingsAppearanceTab");
        session.SelectComboBoxItem("ThemePicker", theme);
        session.SelectAutomationId("SettingsSaveButton");

        session.NavigateTo("timeline", "NavTimeline", "TimelineList");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-timeline", "timeline", "TimelineList");
        session.NavigateTo("dictations", "NavDictations", "DictationHistoryList");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-dictations", "dictations", "DictationHistoryList");
        session.NavigateTo("meetings", "NavMeetings", "MeetingHistoryList");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-meetings", "meetings", "MeetingHistoryList");
        session.NavigateTo("models", "NavModels", "ModelsRefreshButton");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-models", "models", "ModelsRefreshButton");
        session.NavigateTo("shortcuts", "NavShortcuts", "ChangePushToTalkButton");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-shortcuts", "shortcuts", "ChangePushToTalkButton");
        session.NavigateTo("dictionary", "NavDictionary", "DictionaryEntryList");
        session.RequireAutomationId("DictionarySuggestionList", mustBeOnscreen: true);
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-dictionary", "dictionary", "DictionaryEntryList");

        session.NavigateTo("insights", "NavInsights", "InsightsHeroCard");
        session.RequirePageSentinel("insights", "InsightsOverviewCard");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-insights-overview", "insights", "InsightsHeroCard");
        session.RequirePageSentinel("insights", "InsightsHeatmap");
        session.RequirePageSentinel("insights", "InsightsOverviewCard");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-insights-detail", "insights", "InsightsHeatmap");

        session.NavigateTo("settings", "NavSettings", "SettingsUserName");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-settings-general", "settings", "SettingsUserName");
        session.SelectAutomationId("SettingsDictationTab");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-settings-dictation", "settings-dictation", "DictationModelPicker");
        session.SelectAutomationId("SettingsMeetingsTab");
        session.CaptureReference($"winui-populated-{theme.ToLowerInvariant()}-settings-meetings", "settings-meetings", "MeetingModelPicker");
    }

    [WinUiAutomationFact]
    public void Meetings_page_hides_capture_and_recovery_panels_while_idle() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            session.NavigateTo("meetings", "NavMeetings", "QuickNoteButton");

            foreach (var automationId in new[]
                     {
                         "ImportAudioButton",
                         "ManageMeetingTemplatesButton",
                         "MeetingsRefreshButton",
                         "MeetingsSearchBox",
                         "MeetingsSort",
                         "MeetingFoldersExpander"
                     })
            {
                session.RequireAutomationId(automationId);
            }

            // P3-02: the folder editor ships collapsed so the meeting list stays above the fold at
            // 720x720, and a collapsed Expander keeps its content out of the UIA tree. Qualify that
            // it starts closed, then open it and hold the editor to the same requirement as before
            // — the controls must still be there, one click away, not quietly dropped.
            session.RequireAbsentAutomationId(
                "MeetingFolderList",
                "the folder editor is collapsed until the user opens it.");
            session.ExpandAutomationId("MeetingFoldersExpander");
            session.RequireAutomationId("MeetingFolderList");
            session.RequireAutomationId("MeetingFolderName");

            // An empty profile has no active capture and nothing recoverable. Both panels must
            // stay collapsed rather than showing controls that would act on a session that does
            // not exist, or listing recoverable audio that was never recorded.
            session.RequireAbsentAutomationId(
                "ActiveMeetingPanel",
                "no capture or import is running on an empty profile.");
            session.RequireAbsentAutomationId(
                "MeetingRecoveryPanel",
                "an empty profile has no interrupted capture to recover.");
            session.RequireAbsentAutomationId(
                "MeetingHistoryList",
                "an empty profile must show the empty state instead of a history list.");
        });
    });

    [WinUiAutomationFact]
    public void Search_box_routes_the_shell_to_matching_local_history() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch(seedDeterministicMeeting: true);
        session.Run(() =>
        {
            // A term that matches nothing must show the empty state, not an empty result list.
            session.SetValueAutomationId("HistorySearchBox", "zzzz-no-such-history");
            session.RequireAbsentAutomationId(
                "SearchResultList",
                "no local history matches the query.");

            session.SetValueAutomationId("HistorySearchBox", "Seeded launch meeting");
            session.RequireAutomationId("SearchResultList", mustBeOnscreen: true);
        });
    });

    [WinUiAutomationFact]
    public void Insights_share_preview_renders_the_generated_card() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            session.NavigateTo("insights", "NavInsights", "InsightsShareButton");

            // The preview card only exists once a render succeeds; nothing is shown before then.
            session.RequireAbsentAutomationId(
                "InsightsSharePreviewImage",
                "no share card has been generated yet.");

            // P7-06: preview / copy / save are now items on the Share split button's menu. The
            // menu is a separate popup window, so it has to be opened and then searched there -
            // invoking the split button itself would fire its primary action (Windows sharing).
            session.ExpandAutomationId("InsightsShareButton");
            var menu = session.RequireSecondaryWindow("InsightsPreviewShareButton");
            session.SelectAutomationIdIn(menu, "InsightsPreviewShareButton");

            session.RequireAutomationId("InsightsSharePreviewImage", mustBeOnscreen: true);
            session.CaptureReference("winui-insights-share-preview", "insights-share-preview", "InsightsSharePreviewImage");
        });
    });

    /// <summary>
    /// The raw runtime diagnostics are secondary but must stay reachable: the About page opens
    /// with the dump collapsed, and opening the disclosure must realize real text rather than an
    /// empty placeholder.
    /// </summary>
    [WinUiAutomationFact]
    public void About_diagnostics_detail_is_reachable_behind_its_disclosure() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            session.NavigateTo("about", "NavAbout", "RefreshDiagnosticsButton");

            // Secondary: a collapsed Expander keeps its content out of the UIA tree entirely.
            session.RequireAbsentAutomationId(
                "RuntimeDiagnosticsText",
                "the diagnostics dump starts collapsed.");

            // Accessible: one disclosure, and the real summary is there.
            session.ExpandAutomationId("RuntimeDiagnosticsExpander");
            var summary = session.RequireAutomationId("RuntimeDiagnosticsText", mustBeOnscreen: true);
            Assert.False(
                string.IsNullOrWhiteSpace(summary.Current.Name),
                "The expanded runtime diagnostics must publish the collected summary.");
            Assert.DoesNotContain(
                "Loading diagnostics",
                summary.Current.Name,
                StringComparison.Ordinal);
        });
    });

    /// <summary>
    /// Explicit unpackaged coverage: the loose executable has no package identity, so the
    /// MSIX startup task must report itself unavailable instead of pretending to work.
    /// </summary>
    [WinUiAutomationFact(RequireUnpackagedBuild = true)]
    public void Startup_registration_is_reported_unavailable_without_package_identity() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchUnpackaged();
        session.Run(() =>
        {
            session.NavigateTo("settings", "NavSettings", "SettingsSaveButton");

            // The MSIX startup task needs package identity, which the unpackaged shell does not
            // have. The toggle must go disabled and say why instead of silently doing nothing.
            var toggle = session.RequireAutomationId("StartAtLoginToggle", mustBeOnscreen: true);
            Assert.False(
                toggle.Current.IsEnabled,
                "The start-at-login toggle must be disabled when the shell has no package identity.");

            var reason = session.RequireAutomationId("StartupAvailabilityNotice", mustBeOnscreen: true);
            Assert.False(
                string.IsNullOrWhiteSpace(reason.Current.Name),
                "The disabled start-at-login toggle must publish the reason it is unavailable.");
        });
    });

    [WinUiAutomationFact]
    public void Theme_setting_switches_the_shell_between_light_and_dark() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        var light = 0d;
        var dark = 0d;
        session.Run(() =>
        {
            light = ApplyThemeAndMeasure(session, "Light", "winui-theme-light");
            dark = ApplyThemeAndMeasure(session, "Dark", "winui-theme-dark");
        });

        // A theme that only changed a setting without repainting would leave these equal.
        Assert.True(
            light > dark + 40,
            $"The light theme must render brighter than the dark theme. " +
            $"Measured mean luminance light={light:F1}, dark={dark:F1} (0-255).");
    });

    private static double ApplyThemeAndMeasure(MuesliWinUiSession session, string theme, string slug)
    {
        session.NavigateTo("settings", "NavSettings", "SettingsSaveButton");
        session.SelectAutomationId("SettingsAppearanceTab");
        session.SelectComboBoxItem("ThemePicker", theme);
        session.SelectAutomationId("SettingsSaveButton");

        session.NavigateTo("timeline", "NavTimeline", "TimelineRefreshButton");
        return MeanLuminance(session.CaptureReference(slug, "timeline", "TimelineRefreshButton"));
    }

    /// <summary>
    /// Mean perceived brightness of a captured window, 0-255. Sampling every eighth pixel keeps
    /// the measurement cheap while still covering the whole frame.
    /// </summary>
    private static double MeanLuminance(string screenshotPath)
    {
        using var bitmap = new Bitmap(screenshotPath);
        var total = 0d;
        var samples = 0;
        for (var y = 0; y < bitmap.Height; y += 8)
        {
            for (var x = 0; x < bitmap.Width; x += 8)
            {
                var pixel = bitmap.GetPixel(x, y);
                total += (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
                samples++;
            }
        }

        return samples == 0 ? 0 : total / samples;
    }

    /// <summary>
    /// Packaged coverage: the same settings surface must offer the startup task when package
    /// identity exists, and the availability notice must not claim it is missing.
    /// </summary>
    [WinUiAutomationFact]
    public void Startup_registration_is_available_with_package_identity() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.Launch();
        session.Run(() =>
        {
            session.NavigateTo("settings", "NavSettings", "SettingsSaveButton");

            var toggle = session.RequireAutomationId("StartAtLoginToggle", mustBeOnscreen: true);
            Assert.True(
                toggle.Current.IsEnabled,
                "The start-at-login toggle must be enabled when the packaged shell has package identity.");
        });
    });

    /// <summary>
    /// A profile without completed first-run gates opens the onboarding window over the
    /// dashboard. Captures the first setup step as baseline onboarding evidence.
    /// </summary>
    [WinUiAutomationFact]
    public void First_run_profile_opens_the_onboarding_window() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchOnboarding();
        session.Run(() =>
        {
            var onboarding = session.RequireSecondaryWindow("OnboardingProgressBar");
            session.CaptureSecondaryWindow(onboarding, "winui-onboarding-first-run");
        });
    });

    /// <summary>
    /// Explicit unpackaged coverage: executable-level single-instance redirection. Packaged
    /// AUMID re-activation is platform-mediated and qualified separately.
    /// </summary>
    [WinUiAutomationFact(RequireUnpackagedBuild = true)]
    public void Second_process_redirects_and_the_primary_shell_stays_attached() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchUnpackaged();
        session.Run(() =>
        {
            var start = new ProcessStartInfo
            {
                FileName = session.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(session.ExecutablePath)!,
                UseShellExecute = false
            };
            start.Environment["MUESLI_PROFILE_ROOT"] = session.ProfileRoot;

            using var second = Process.Start(start)
                               ?? throw new InvalidOperationException("The second shell process did not start.");
            Assert.True(
                second.WaitForExit(30_000),
                "A second WinUI shell process must redirect to the primary instance and exit.");

            session.RequireAutomationId("MainNavigation", mustBeOnscreen: true);
        });
    });

    /// <summary>
    /// Explicit unpackaged coverage: a second activation must return the primary shell to the
    /// dashboard (not leave the user on whatever page they had open). This is the
    /// <c>SingleInstanceCoordinator.StartListening</c> contract; the packaged AUMID path routes
    /// through the same <c>MainWindow.ShowDashboard</c>.
    /// </summary>
    [WinUiAutomationFact(RequireUnpackagedBuild = true)]
    public void Second_process_activation_returns_the_primary_shell_to_the_dashboard() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchUnpackaged();
        session.Run(() =>
        {
            session.NavigateTo("settings", "NavSettings", "SettingsSaveButton");

            var start = new ProcessStartInfo
            {
                FileName = session.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(session.ExecutablePath)!,
                UseShellExecute = false
            };
            start.Environment["MUESLI_PROFILE_ROOT"] = session.ProfileRoot;

            using var second = Process.Start(start)
                               ?? throw new InvalidOperationException("The second shell process did not start.");
            Assert.True(
                second.WaitForExit(30_000),
                "A second WinUI shell process must redirect to the primary instance and exit.");

            // Without a dashboard return the primary would still be on Settings and the Timeline
            // sentinel would never appear.
            session.RequirePageSentinel("timeline", "TimelineList");
        });
    });

}
