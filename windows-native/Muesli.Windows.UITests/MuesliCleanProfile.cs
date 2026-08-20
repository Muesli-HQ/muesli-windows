namespace Muesli.Windows.UITests;

/// <summary>
/// Isolated writable profile for production-startup automation.
/// Production has no custom data-dir environment variable, and launching Muesli.exe
/// (WPF apphost) does not honor process-level APPDATA for
/// <c>Environment.GetFolderPath(ApplicationData)</c> — only
/// <c>GetEnvironmentVariable("APPDATA")</c> changes, which this app does not use.
/// The harness therefore parks any existing %APPDATA%\muesli directory, installs a
/// seed-only directory at that path, and restores the parked directory on dispose.
/// It never uses the developer's parked history/settings as the writable profile.
/// </summary>
internal sealed class MuesliCleanProfile : IDisposable
{
    public const string MarkerFileName = ".l09-ui-harness-profile";
    public const string ParkFolderName = "muesli.l09-harness-park";

    private bool _disposed;
    private bool _profileInstalled;
    private bool _parkedOriginalProfile;

    public MuesliCleanProfile(bool seedDeterministicMeeting = false)
    {
        SeedDeterministicMeeting = seedDeterministicMeeting;
        AppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        MuesliRoot = Path.Combine(AppDataRoot, "muesli");
        ParkedProfile = Path.Combine(AppDataRoot, ParkFolderName);
        try
        {
            Install();
        }
        catch (IOException exception)
        {
            RestoreAfterFailedInstall();
            throw new InvalidOperationException(
                $"L09 UI Automation could not acquire the APPDATA profile without modifying it: {exception.Message}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            RestoreAfterFailedInstall();
            throw new InvalidOperationException(
                $"L09 UI Automation could not acquire the APPDATA profile without modifying it: {exception.Message}",
                exception);
        }
    }

    public string AppDataRoot { get; }
    public string MuesliRoot { get; }
    public string ParkedProfile { get; }
    public bool SeedDeterministicMeeting { get; }

    public void AssertIsolatedFromDeveloperProfile()
    {
        Assert.True(
            File.Exists(Path.Combine(MuesliRoot, MarkerFileName)),
            "The live %APPDATA%\\muesli directory is not the L09 harness seed profile.");
        Assert.False(
            File.Exists(Path.Combine(MuesliRoot, "windows-dictations.json")),
            "Harness profile must not carry developer dictation history.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_profileInstalled || _parkedOriginalProfile)
            Restore();
    }

    private void Install()
    {
        RecoverInterruptedSwap();
        if (Directory.Exists(MuesliRoot))
        {
            if (Directory.Exists(ParkedProfile))
            {
                throw new InvalidOperationException(
                    $"Cannot park %APPDATA%\\muesli because '{ParkFolderName}' already exists. Restore it manually before re-running L09 tests.");
            }

            Directory.Move(MuesliRoot, ParkedProfile);
            _parkedOriginalProfile = true;
        }

        Directory.CreateDirectory(MuesliRoot);
        _profileInstalled = true;
        SeedCompletedFirstRun();
        if (SeedDeterministicMeeting)
            SeedMeetingDetailFixture();
        File.WriteAllText(
            Path.Combine(MuesliRoot, MarkerFileName),
            "L09 UI automation seed profile. Safe to delete if a test run was interrupted; restore muesli.l09-harness-park to muesli.");
    }

    private void RecoverInterruptedSwap()
    {
        if (!File.Exists(Path.Combine(MuesliRoot, MarkerFileName)))
            return;
        Directory.Delete(MuesliRoot, recursive: true);

        if (Directory.Exists(ParkedProfile) && !Directory.Exists(MuesliRoot))
            Directory.Move(ParkedProfile, MuesliRoot);
    }

    private void Restore()
    {
        if (_profileInstalled && Directory.Exists(MuesliRoot))
            Directory.Delete(MuesliRoot, recursive: true);

        if (Directory.Exists(ParkedProfile) && !Directory.Exists(MuesliRoot))
            Directory.Move(ParkedProfile, MuesliRoot);

        _profileInstalled = false;
        _parkedOriginalProfile = false;
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
            """
            {
              "schemaVersion": 8,
              "onboardingCompleted": true,
              "lastCompletedFeatureTourVersion": 1,
              "openDashboardOnLaunch": true,
              "startAtLogin": false,
              "showFloatingIndicator": false,
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
