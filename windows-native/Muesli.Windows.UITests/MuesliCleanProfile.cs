using System.Text.Json;

namespace Muesli.Windows.UITests;

/// <summary>
/// Isolated writable profile for production-startup automation.
/// Production data is never used as the writable automation profile. The harness installs a
/// seed-only directory beneath the OS temporary directory and passes its absolute path through
/// <c>MUESLI_PROFILE_ROOT</c>. This keeps the production profile untouched and avoids the packaged
/// desktop runner's AppData virtualization while still exercising the real production startup.
/// <c>ParkedProfile</c> remains exposed as the legacy park location for isolation diagnostics and
/// compatibility with the phase-preview guard; this harness no longer moves that directory.
/// </summary>
internal sealed class MuesliCleanProfile : IDisposable
{
    public const string MarkerFileName = ".l09-ui-harness-profile";
    public const string ParkFolderName = "muesli.l09-harness-park";

    private bool _disposed;
    private bool _profileInstalled;
    private readonly string _temporaryProfileParent;

    public MuesliCleanProfile(
        bool seedDeterministicMeeting = false,
        bool seedPopulatedData = false,
        bool completeFirstRun = true,
        bool showFloatingIndicator = false)
    {
        SeedDeterministicMeeting = seedDeterministicMeeting;
        SeedPopulatedData = seedPopulatedData;
        CompleteFirstRun = completeFirstRun;
        ShowFloatingIndicator = showFloatingIndicator;
        AppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _temporaryProfileParent = Path.GetFullPath(Path.GetTempPath());
        MuesliRoot = Path.Combine(
            _temporaryProfileParent,
            $"muesli-l09-ui-{Guid.NewGuid():N}");
        ParkedProfile = Path.Combine(AppDataRoot, ParkFolderName);
        try
        {
            Install();
        }
        catch (IOException exception)
        {
            RestoreAfterFailedInstall();
            throw new InvalidOperationException(
                $"L09 UI Automation could not create its isolated temporary profile: {exception.Message}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            RestoreAfterFailedInstall();
            throw new InvalidOperationException(
                $"L09 UI Automation could not create its isolated temporary profile: {exception.Message}",
                exception);
        }
    }

    public string AppDataRoot { get; }
    public string MuesliRoot { get; }
    public string ParkedProfile { get; }
    public bool SeedDeterministicMeeting { get; }
    public bool SeedPopulatedData { get; }
    /// <summary>False leaves first-run gates untouched so onboarding opens on launch.</summary>
    public bool CompleteFirstRun { get; }

    /// <summary>
    /// Whether the seeded profile shows the floating dictation indicator. Defaults to
    /// <c>false</c> so shell tests are not disturbed by an always-on-top pill; indicator tests
    /// opt in.
    /// </summary>
    public bool ShowFloatingIndicator { get; }

    public void AssertIsolatedFromDeveloperProfile()
    {
        Assert.True(
            File.Exists(Path.Combine(MuesliRoot, MarkerFileName)),
            "The isolated L09 harness profile marker is missing.");
        Assert.False(
            string.Equals(
                Path.GetFullPath(MuesliRoot),
                Path.Combine(AppDataRoot, "muesli"),
                StringComparison.OrdinalIgnoreCase),
            "The UI harness must never use the developer's production profile as its writable root.");
        Assert.True(
            SeedPopulatedData || !File.Exists(Path.Combine(MuesliRoot, "windows-dictations.json")),
            "Only the explicitly populated harness profile may contain seeded dictation history.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_profileInstalled)
            Restore();
    }

    private void Install()
    {
        RecoverInterruptedSwap();
        if (Directory.Exists(MuesliRoot))
        {
            throw new InvalidOperationException(
                $"The generated L09 profile path already exists: '{MuesliRoot}'.");
        }

        Directory.CreateDirectory(MuesliRoot);
        _profileInstalled = true;
        // Write the marker before any seeding so a partial setup can still be safely reclaimed.
        File.WriteAllText(
            Path.Combine(MuesliRoot, MarkerFileName),
            "L09 UI automation seed profile. Safe to delete if a test run was interrupted.");
        if (CompleteFirstRun)
            SeedCompletedFirstRun();
        if (SeedPopulatedData)
            SeedPopulatedFixture();
        else if (SeedDeterministicMeeting)
            SeedMeetingDetailFixture();
    }

    private void RecoverInterruptedSwap()
    {
        var fullRoot = Path.GetFullPath(MuesliRoot).TrimEnd(Path.DirectorySeparatorChar);
        var fullTemp = Path.GetFullPath(_temporaryProfileParent).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullRoot.StartsWith(fullTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(fullRoot, MarkerFileName)))
            return;
        Directory.Delete(fullRoot, recursive: true);
    }

    private void Restore()
    {
        // Delete only a generated profile beneath the OS temp directory and only after checking
        // the marker. This makes cleanup safe after a failed test or an interrupted process.
        var fullRoot = Path.GetFullPath(MuesliRoot).TrimEnd(Path.DirectorySeparatorChar);
        var fullTemp = Path.GetFullPath(_temporaryProfileParent).TrimEnd(Path.DirectorySeparatorChar);
        var marker = Path.Combine(fullRoot, MarkerFileName);
        if (_profileInstalled && Directory.Exists(fullRoot) &&
            fullRoot.StartsWith(fullTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(marker))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Delete(MuesliRoot, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(100 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        _profileInstalled = false;
    }

    private void RestoreAfterFailedInstall()
    {
        try
        {
            Restore();
        }
        catch
        {
            // Preserve the original failure. Install only mutates the profile after
            // the park succeeds, and a later successful test run can recover a marker.
        }
    }

    private void SeedCompletedFirstRun()
    {
        // Deterministic MainWindow reachability: a truly empty profile opens onboarding.
        // L34 owns onboarding qualification; this skeleton seeds completed first-run gates
        // with non-secret defaults so production startup lands on the dashboard.
        File.WriteAllText(
            Path.Combine(MuesliRoot, "windows-settings.json"),
            $$"""
            {
              "schemaVersion": 8,
              "onboardingCompleted": true,
              "lastCompletedFeatureTourVersion": 2,
              "openDashboardOnLaunch": true,
              "startAtLogin": false,
              "showFloatingIndicator": {{(ShowFloatingIndicator ? "true" : "false")}},
              "autoMeetingDetectionEnabled": false,
              "theme": "dark"
            }
            """);
        File.WriteAllText(
            Path.Combine(MuesliRoot, "onboarding-progress.json"),
            """
            {
              "schemaVersion": 1,
              "completed": true
            }
            """);
    }

    /// <summary>
    /// Seeds the complete, read-only visual qualification fixture. Dates are relative to the
    /// machine's local today so grouping and streak calculations remain truthful on every run.
    /// These are production JSON value files (version-0 shape), read by the real adapters.
    /// </summary>
    private void SeedPopulatedFixture()
    {
        var dataDirectory = Path.Combine(MuesliRoot, "data");
        Directory.CreateDirectory(dataDirectory);
        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Local);

        var dictations = new[]
        {
            new { id = "ui-dictation-today-01", timestamp = today.AddHours(9).AddMinutes(10), text = "Review the launch checklist and send the final notes to the design team.", durationMs = 8400, modelProfile = "parakeet-v3" },
            new { id = "ui-dictation-today-02", timestamp = today.AddHours(13).AddMinutes(25), text = "Remember to schedule the customer research session for next Tuesday.", durationMs = 7200, modelProfile = "parakeet-v3" },
            new { id = "ui-dictation-yesterday", timestamp = today.AddDays(-1).AddHours(16).AddMinutes(5), text = "Capture the key risks before we publish the weekly product update.", durationMs = 11200, modelProfile = "parakeet-v3" },
            new { id = "ui-dictation-week-old", timestamp = today.AddDays(-7).AddHours(11).AddMinutes(40), text = "Draft a concise project brief for the onboarding improvements.", durationMs = 9600, modelProfile = "parakeet-v3" },
            new { id = "ui-dictation-older", timestamp = today.AddDays(-32).AddHours(10).AddMinutes(15), text = "Follow up with the research panel about the accessibility review.", durationMs = 6800, modelProfile = "parakeet-v3" },
            new { id = "ui-dictation-oldest", timestamp = today.AddDays(-95).AddHours(14).AddMinutes(30), text = "Archive the completed sprint notes and update the team roadmap.", durationMs = 13200, modelProfile = "parakeet-v3" }
        };
        WriteJson(Path.Combine(dataDirectory, "windows-dictations.json"), dictations);

        var meetings = new[]
        {
            Meeting("ui-meeting-today", "Product planning · Today", today.AddHours(10), 42_000, "ui-folder-product", "Today's planning session covered launch readiness and owners for the final checklist.", "Product planning summary"),
            Meeting("ui-meeting-yesterday", "Customer research review · Yesterday", today.AddDays(-1).AddHours(15), 1_860_000, "ui-folder-research", "The team reviewed five interviews and agreed on two follow-up experiments for onboarding.", "Customer research summary"),
            Meeting("ui-meeting-older", "Accessibility retro · Earlier", today.AddDays(-14).AddHours(11), 2_430_000, null, "A retro on keyboard navigation, screen-reader labels, and the next parity pass.", "Accessibility retro summary")
        };
        WriteJson(Path.Combine(dataDirectory, "windows-meetings.json"), meetings);
        WriteJson(Path.Combine(dataDirectory, "windows-meeting-folders.json"), new[]
        {
            new { id = "ui-folder-product", name = "Product", parentId = (string?)null },
            new { id = "ui-folder-research", name = "Research", parentId = (string?)null },
            new { id = "ui-folder-archive", name = "Archive", parentId = (string?)null }
        });
        WriteJson(Path.Combine(dataDirectory, "windows-dictionary.json"), new[]
        {
            new { id = "ui-dictionary-muesli", phrase = "muesli", replacement = "Muesli", matchingThreshold = 0.90 },
            new { id = "ui-dictionary-winui", phrase = "win you I", replacement = "WinUI", matchingThreshold = 0.88 },
            new { id = "ui-dictionary-parakeet", phrase = "parakeet", replacement = "Parakeet", matchingThreshold = 0.92 },
            new { id = "ui-dictionary-accessibility", phrase = "access ability", replacement = "accessibility", matchingThreshold = 0.86 }
        });
        WriteJson(Path.Combine(dataDirectory, "windows-meeting-templates.json"), new[]
        {
            new { id = "ui-template-product", name = "Product planning summary", prompt = "Summarize decisions, owners, risks, and next steps.", icon = "checkmark.circle" },
            new { id = "ui-template-research", name = "Customer research summary", prompt = "List themes, evidence, open questions, and follow-up experiments.", icon = "person.2" },
            new { id = "ui-template-retro", name = "Accessibility retro summary", prompt = "Capture wins, friction, and actionable accessibility follow-ups.", icon = "accessibility" }
        });

        var settings = new
        {
            schemaVersion = 8,
            userName = "Alex",
            hotkey = "F8",
            pasteBehavior = "active-app",
            dictationModelId = "parakeet-v3",
            finalMeetingModelId = "parakeet-v3",
            liveMeetingModelId = (string?)null,
            liveTranscriptOwnership = "preview-only",
            showLiveWaveformOnHover = true,
            onboardingCompleted = true,
            lastCompletedFeatureTourVersion = 2,
            enableDoubleTapDictation = false,
            hotkeyTriggerThresholdMs = 250,
            recordingColorHex = "1e1e2e",
            removeFillerWords = true,
            enableLocalCleanup = false,
            startAtLogin = false,
            autoMeetingDetectionEnabled = false,
            meetingSummaryProvider = "local",
            meetingSummaryTemplate = "standard",
            meetingSummaryPromptOverride = "",
            openDashboardOnLaunch = true,
            saveMeetingRecordings = true,
            showFloatingIndicator = false,
            soundEnabled = true,
            indicatorAnchor = "Middle Right",
            dictionarySuggestions = new[]
            {
                new { observed = "muesli app", replacement = "Muesli app" },
                new { observed = "win you I", replacement = "WinUI" },
                new { observed = "access ability", replacement = "accessibility" }
            },
            openAIModel = "gpt-5.4-mini",
            openRouterModel = "stepfun/step-3.5-flash:free",
            ollamaEndpoint = "http://localhost:11434",
            ollamaModel = "llama3.1:8b",
            theme = "dark",
            crashReportingEnabled = false,
            crashReportingPromptShown = true
        };
        WriteJson(Path.Combine(MuesliRoot, "windows-settings.json"), settings);
    }

    private static object Meeting(string id, string title, DateTime createdAt, int durationMs, string? folderId, string summary, string templateName) => new
    {
        schemaVersion = 5,
        id,
        title,
        createdAt,
        durationMs,
        transcript = $"{summary} The transcript includes clear decisions for {title}.",
        summary,
        sourcePath = "",
        modelProfile = "parakeet-v3",
        folderId,
        wordCount = 22,
        templateName,
        speakerAliases = new Dictionary<string, string> { ["SPEAKER_00"] = "Alex", ["SPEAKER_01"] = "Jordan" },
        healthWarnings = Array.Empty<string>(),
        sessionState = 6,
        microphoneAudioPath = (string?)null,
        systemAudioPath = (string?)null,
        systemCaptureMode = "legacy-unknown",
        recoveredFromInterruption = false,
        livePreviewModelId = (string?)null,
        liveTranscriptOwnership = "off",
        finalTranscriptOwnerModelId = "parakeet-v3",
        gapRecoveryModelId = (string?)null,
        manualNotes = "Fixture note: verify summary, transcript, and speaker labels.",
        titleIsManual = true,
        automationResult = (object?)null
    };

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    private void SeedMeetingDetailFixture()
    {
        var dataDirectory = Path.Combine(MuesliRoot, "data");
        var retranscriptionDirectory = Path.Combine(dataDirectory, "retranscription");
        Directory.CreateDirectory(retranscriptionDirectory);
        File.WriteAllText(
            Path.Combine(dataDirectory, "windows-meetings.json"),
            """
            [
              {
                "schemaVersion": 5,
                "id": "l09-seeded-meeting",
                "title": "Seeded launch meeting",
                "createdAt": "2026-08-19T10:00:00Z",
                "durationMs": 42000,
                "transcript": "Original transcript survives the UI flow.",
                "summary": "",
                "sourcePath": "",
                "modelProfile": "parakeet-tdt-0.6b-v3",
                "folderId": null,
                "wordCount": 6,
                "templateName": "Default",
                "speakerAliases": { "SPEAKER_00": "Alex" },
                "healthWarnings": [],
                "sessionState": 6,
                "microphoneAudioPath": null,
                "systemAudioPath": null,
                "systemCaptureMode": "legacy-unknown",
                "recoveredFromInterruption": false,
                "livePreviewModelId": null,
                "liveTranscriptOwnership": "off",
                "finalTranscriptOwnerModelId": "parakeet-tdt-0.6b-v3",
                "gapRecoveryModelId": null,
                "manualNotes": "Manual note survives transcript editing.",
                "titleIsManual": true,
                "automationResult": null
              }
            ]
            """);
        File.WriteAllText(
            Path.Combine(retranscriptionDirectory, "l09-seeded-meeting-candidate.json"),
            """
            {
              "schemaVersion": 1,
              "meetingId": "l09-seeded-meeting",
              "candidateId": "l09-seeded-candidate",
              "status": "Ready",
              "createdAtUtc": "2026-08-19T10:01:00Z",
              "updatedAtUtc": "2026-08-19T10:01:00Z",
              "transcript": "Ready candidate must be explicitly accepted or rejected.",
              "transcriptFingerprint": "fixture",
              "error": null,
              "audioFileName": null,
              "audioByteLength": 0,
              "durationMs": 42000
            }
            """);
    }
}
