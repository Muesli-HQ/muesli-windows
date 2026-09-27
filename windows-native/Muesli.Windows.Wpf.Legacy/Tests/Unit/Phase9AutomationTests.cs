using System.Diagnostics;
using System.Security.Cryptography;
using Muesli.Windows;

namespace Muesli.Windows.Tests;

public sealed class Phase9AutomationTests
{
    private static string HookExecutable => Path.Combine(
        AppContext.BaseDirectory,
        "AutomationTestHost",
        "Muesli.Automation.TestHost.exe");

    private static MeetingItem Meeting(
        string id = "phase9",
        string title = "Planning / launch & review",
        string transcript = "transcript exact value α",
        string summary = "generated exact value β",
        string manualNotes = "manual exact value γ",
        MeetingSessionState sessionState = MeetingSessionState.Completed,
        PostMeetingAutomationResult? automationResult = null) =>
        new(
            Id: id,
            Title: title,
            CreatedAt: new DateTime(2026, 8, 3, 10, 30, 0, DateTimeKind.Utc),
            Transcript: transcript,
            Summary: summary,
            SourcePath: @"C:\private\audio.wav",
            ModelProfile: "parakeet-v3",
            DurationMs: 65_000,
            FolderId: "folder-1",
            WordCount: 123,
            TemplateName: "Action items",
            HealthWarnings: ["low microphone level"],
            SessionState: sessionState,
            MicrophoneAudioPath: @"C:\private\microphone.wav",
            SystemAudioPath: @"C:\private\system.wav",
            RecoveredFromInterruption: true,
            ManualNotes: manualNotes,
            AutomationResult: automationResult);

    private static PostMeetingAutomationOptions HookOptions(string? path = null) => new()
    {
        HookEnabled = true,
        HookExecutablePath = path ?? HookExecutable,
        TranscriptPolicy = PostMeetingTranscriptPolicy.Inline,
        Timeout = TimeSpan.FromSeconds(5)
    };

    [Fact]
    public async Task DefaultsAreDisabledAndHaveMetadataOnlyTranscriptPolicy()
    {
        var options = new PostMeetingAutomationOptions();
        Assert.False(options.HookEnabled);
        Assert.False(options.AutoExportEnabled);
        Assert.Equal(PostMeetingTranscriptPolicy.MetadataOnly, options.TranscriptPolicy);

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Disabled, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.False(result.Export.Requested);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"C:\missing\hook.exe")]
    [InlineData(@"C:\Windows\System32\notepad.com")]
    public async Task HookRequiresAUserSelectedRootedExistingExe(string path)
    {
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(), HookOptions(path), PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.InvalidConfiguration, result.Status);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public async Task ExistingMalformedExeFailsDuringNativeStartupWithoutEscapingTheRunner()
    {
        using var directory = new TestDirectory();
        var malformedExe = directory.File("malformed hook.exe");
        var malformedImage = await File.ReadAllBytesAsync(HookExecutable);
        var peHeaderOffset = BitConverter.ToInt32(malformedImage, 0x3c);
        malformedImage[peHeaderOffset + 4] = 0xff;
        malformedImage[peHeaderOffset + 5] = 0xff;
        await File.WriteAllBytesAsync(malformedExe, malformedImage);
        var options = HookOptions(malformedExe) with
        {
            RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 1 }
        };

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "malformed-executable", transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Null(result.ExitCode);
        Assert.Contains("Hook launch failed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectExeLaunchHandlesSpacesMetacharactersAndUnicodeWithoutArguments()
    {
        using var directory = new TestDirectory();
        var hostileDirectory = Path.Combine(directory.Path, "hook & (literal) Ω 漢字 folder");
        Directory.CreateDirectory(hostileDirectory);
        foreach (var source in Directory.GetFiles(Path.GetDirectoryName(HookExecutable)!))
            File.Copy(source, Path.Combine(hostileDirectory, Path.GetFileName(source)));
        var copiedHook = Path.Combine(hostileDirectory, Path.GetFileName(HookExecutable));
        var title = "研发 sync & whoami | $(unsafe) %PATH%";

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(title: title, transcript: "", summary: "", manualNotes: ""),
            HookOptions(copiedHook),
            PostMeetingCompletionEvent.RecoveryCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.Contains($"TITLE={title}", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadIsVersionedExplicitAndSensitiveOutputIsSuppressedFromDiagnostics()
    {
        var meeting = Meeting();
        var payload = PostMeetingAutomationService.BuildPayload(
            meeting,
            PostMeetingCompletionEvent.RecoveryCompleted,
            PostMeetingTranscriptPolicy.Inline,
            PostMeetingExportDiagnostic.NotRequested);
        var json = PostMeetingAutomationService.SerializePayload(payload);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"meeting.completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"recovery-completed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"recoveredFromInterruption\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"policy\":\"inline\"", json, StringComparison.Ordinal);
        Assert.Equal(meeting.Transcript, payload.Transcript.Data);
        Assert.Equal(meeting.Summary, payload.Notes.Generated);
        Assert.Equal(meeting.ManualNotes, payload.Notes.Manual);
        Assert.DoesNotContain(meeting.SourcePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(meeting.MicrophoneAudioPath!, json, StringComparison.OrdinalIgnoreCase);

        var result = await new PostMeetingAutomationService().RunAsync(
            meeting, HookOptions(), PostMeetingCompletionEvent.RecoveryCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.Equal("[REDACTED CONTENT OUTPUT]", result.StandardOutput);
        Assert.DoesNotContain(meeting.Transcript, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(meeting.Summary, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(meeting.ManualNotes, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("host-secret-123456", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncationInsideLongSensitiveValuesCannotLeakPrefixesOrFragments()
    {
        var recognizablePrefix = "TOP-SECRET-TRANSCRIPT-PREFIX-";
        var transcript = recognizablePrefix + new string('T', 20_000);
        var manualPrefix = "PRIVATE-MANUAL-NOTES-PREFIX-";
        var manual = manualPrefix + new string('N', 20_000);
        var options = HookOptions() with { MaxCapturedOutputCharacters = 257 };

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "sensitive-first", transcript: transcript, summary: "", manualNotes: manual),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.Equal("[REDACTED CONTENT OUTPUT]", result.StandardOutput);
        Assert.Equal("[REDACTED CONTENT OUTPUT]", result.StandardError);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.DoesNotContain(recognizablePrefix, result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(manualPrefix, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('T', 32), result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('N', 32), result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualTestRunsWhileDisabledWithSyntheticMetadataOnlyPayload()
    {
        var result = await new PostMeetingAutomationService().TestHookAsync(new PostMeetingAutomationOptions
        {
            HookEnabled = false,
            HookExecutablePath = HookExecutable,
            TranscriptPolicy = PostMeetingTranscriptPolicy.Inline
        });

        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.Contains("\"name\":\"meeting.hook.test\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"trigger\":\"manual-test\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"policy\":\"metadata-only\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"data\":null", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"generated\":\"\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"manual\":\"\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimeoutTerminatesTheHook()
    {
        var options = HookOptions() with { Timeout = TimeSpan.FromMilliseconds(200) };
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "timeout"), options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.TimedOut, result.Status);
        Assert.Contains("job was terminated", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationTerminatesTheHookForAppShutdown()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "timeout-cancel"), HookOptions(), PostMeetingCompletionEvent.RecordingCompleted, cancellation.Token);

        Assert.Equal(PostMeetingAutomationStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task JobObjectTerminatesSpawnedDescendants()
    {
        // The helper is a framework-dependent apphost; leave enough startup headroom when the full
        // suite is concurrently exercising models and persistence before timing out its spawned child.
        var options = HookOptions() with { Timeout = TimeSpan.FromSeconds(5) };
        var meetingId = $"spawn-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(Path.GetTempPath(), $"muesli-child-{meetingId}.txt");
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: meetingId, transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.TimedOut, result.Status);
        Assert.True(File.Exists(markerPath));
        var childId = int.Parse(File.ReadAllText(markerPath));
        await Task.Delay(150);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childId));
        File.Delete(markerPath);
    }

    [Fact]
    public async Task ParentExitCannotLeaveInheritedPipesOrDescendantAlivePastAttemptTimeout()
    {
        var options = HookOptions() with { Timeout = TimeSpan.FromSeconds(3) };
        var stopwatch = Stopwatch.StartNew();
        var meetingId = $"parent-exit-child-{Guid.NewGuid():N}";
        var markerPath = Path.Combine(Path.GetTempPath(), $"muesli-child-{meetingId}.txt");

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: meetingId, transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        stopwatch.Stop();
        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Run took {stopwatch.Elapsed}.");
        Assert.True(File.Exists(markerPath));
        var childId = int.Parse(File.ReadAllText(markerPath));
        await Task.Delay(150);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childId));
        File.Delete(markerPath);
    }

    [Fact]
    public async Task CodeRunningBeforeStdinIsAlreadyInsideTheJobObject()
    {
        using var directory = new TestDirectory();
        var hookDirectory = Path.Combine(directory.Path, "immediate hook");
        Directory.CreateDirectory(hookDirectory);
        foreach (var source in Directory.GetFiles(Path.GetDirectoryName(HookExecutable)!))
            File.Copy(source, Path.Combine(hookDirectory, Path.GetFileName(source)));
        var copiedHook = Path.Combine(hookDirectory, Path.GetFileName(HookExecutable));
        var markerPath = directory.File("immediate-child.pid");
        File.WriteAllText(copiedHook + ".immediate-spawn", markerPath);
        var options = HookOptions(copiedHook) with { Timeout = TimeSpan.FromSeconds(2) };

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.TimedOut, result.Status);
        Assert.True(File.Exists(markerPath), "Immediate-start descendant never reached its marker.");
        var childId = int.Parse(File.ReadAllText(markerPath));
        await Task.Delay(150);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childId));
    }

    [Fact]
    public async Task NonzeroExitIsCapturedAndRetryCountIsBounded()
    {
        var options = HookOptions() with
        {
            RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 99 }
        };
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "crash", transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(3, result.Attempts);
        Assert.DoesNotContain("host-crash-password", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("[REDACTED SECRET]", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdoutAndStderrRemainBoundedWhileReadersDrainTheProcess()
    {
        var options = HookOptions() with { MaxCapturedOutputCharacters = 4096 };
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(id: "oversized", transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Succeeded, result.Status);
        Assert.Equal(4096, result.StandardOutput.Length);
        Assert.Equal(4096, result.StandardError.Length);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task AutoExportCreatesMissingDirectoryAtomicallyAndReusesManifest()
    {
        using var root = new TestDirectory();
        var destination = Path.Combine(root.Path, "selected destination");
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = destination,
            AutoExportMode = MeetingExportMode.FullMeeting
        };
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);
        var second = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(first.Export.Completed);
        Assert.True(second.Export.Completed);
        Assert.Equal(AutomationDestinationOwnership.UserSelectedDestination, first.Export.DestinationOwnership);
        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(MeetingExporter.BuildMarkdown(Meeting(), MeetingExportMode.FullMeeting, null),
            File.ReadAllText(first.Export.DestinationPath!));
        Assert.Single(Directory.GetFiles(destination, "*.md"));
        Assert.Empty(Directory.GetFiles(destination, "*.tmp"));
    }

    [Fact]
    public async Task ModifiedManifestDestinationIsNeverOverwrittenAndPublishesFreshCandidate()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(
            meeting,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(first.Export.Completed, first.Export.Error);

        var userContent = "user-modified export; preserve this file";
        File.WriteAllText(first.Export.DestinationPath!, userContent);

        var second = await service.RunAsync(
            meeting,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);
        var expectedMarkdown = MeetingExporter.BuildMarkdown(meeting, MeetingExportMode.FullMeeting, null);

        Assert.True(second.Export.Completed, second.Export.Error);
        Assert.NotEqual(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(userContent, File.ReadAllText(first.Export.DestinationPath!));
        Assert.Equal(expectedMarkdown, File.ReadAllText(second.Export.DestinationPath!));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);

        var third = await service.RunAsync(
            meeting,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);
        Assert.Equal(second.Export.DestinationPath, third.Export.DestinationPath);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task ChangedRenderedContentInvalidatesManifestAndPublishesCollisionFreeCandidate()
    {
        using var directory = new TestDirectory();
        var original = Meeting();
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(
            original,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(first.Export.Completed, first.Export.Error);
        var originalMarkdown = File.ReadAllText(first.Export.DestinationPath!);

        var changed = Meeting(transcript: "updated transcript exact value δ");
        var second = await service.RunAsync(
            changed,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);
        var changedMarkdown = MeetingExporter.BuildMarkdown(changed, MeetingExportMode.FullMeeting, null);

        Assert.True(second.Export.Completed, second.Export.Error);
        Assert.NotEqual(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(originalMarkdown, File.ReadAllText(first.Export.DestinationPath!));
        Assert.Equal(changedMarkdown, File.ReadAllText(second.Export.DestinationPath!));
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task ConcurrentAutoExportsPublishExactlyOneMarkdownFile()
    {
        const int rounds = 8;
        const int concurrentRuns = 12;
        for (var round = 0; round < rounds; round++)
        {
            using var directory = new TestDirectory();
            var options = new PostMeetingAutomationOptions
            {
                AutoExportEnabled = true,
                AutoExportDirectory = directory.Path,
                RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 3 }
            };
            var service = new PostMeetingAutomationService();

            var runs = await Task.WhenAll(Enumerable.Range(0, concurrentRuns).Select(_ => Task.Run(() =>
                service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted))));

            Assert.All(runs, result => Assert.True(result.Export.Completed, result.Export.Error));
            Assert.Single(runs.Select(result => result.Export.DestinationPath).Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
    }

    [Fact]
    public async Task PreexistingUnownedControlDirectoryIsLeftCompletelyUntouched()
    {
        using var directory = new TestDirectory();
        var controlDirectory = Path.Combine(directory.Path, ".muesli-automation");
        Directory.CreateDirectory(controlDirectory);
        var sentinel = Path.Combine(controlDirectory, "user-file.txt");
        File.WriteAllText(sentinel, "do not modify");
        var beforeAttributes = File.GetAttributes(controlDirectory);
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
        Assert.False(result.Export.Completed);
        Assert.Equal("do not modify", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(controlDirectory, ".owner.json")));
        Assert.Equal(beforeAttributes, File.GetAttributes(controlDirectory));
        Assert.Single(Directory.GetFiles(controlDirectory));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.md"));
    }

    [Fact]
    public async Task PreexistingMatchingServiceTempCollisionIsNeverDeleted()
    {
        using var directory = new TestDirectory();
        var service = new PostMeetingAutomationService();
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path,
            RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 1 }
        };
        // Establish a legitimately owned control directory with a different deterministic key.
        var setup = await service.RunAsync(
            Meeting(id: "control-owner-setup"), options, PostMeetingCompletionEvent.RecordingCompleted);
        Assert.True(setup.Export.Completed, setup.Export.Error);

        var target = Meeting(id: "temp-collision-target");
        var paths = PostMeetingMarkdownAutoExporter.GetControlPaths(
            directory.Path,
            target.Id,
            PostMeetingCompletionEvent.RecordingCompleted,
            MeetingExportMode.FullMeeting);
        var collisionPath = Path.Combine(directory.Path, $".muesli-{paths.Key}.content-collision.tmp");
        File.WriteAllText(collisionPath, "preexisting user temp");
        var tokens = new Queue<string>(["claim-owned", "content-collision"]);
        PostMeetingMarkdownAutoExporter.TemporaryTokenFactoryForTests = () => tokens.Dequeue();
        try
        {
            var result = await service.RunAsync(
                target, options, PostMeetingCompletionEvent.RecordingCompleted);

            Assert.False(result.Export.Completed);
            Assert.Equal("preexisting user temp", File.ReadAllText(collisionPath));
        }
        finally
        {
            PostMeetingMarkdownAutoExporter.TemporaryTokenFactoryForTests = null;
        }
    }

    [Fact]
    public async Task UserCollisionIsNeverOverwrittenAndManifestReusesTheNewCandidate()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var suggested = Path.ChangeExtension(
            MeetingExporter.SuggestFilename(meeting, MeetingExportMode.FullMeeting), ".md");
        var userFile = directory.File(suggested);
        File.WriteAllText(userFile, "user-owned content");
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };
        var service = new PostMeetingAutomationService();

        var first = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);
        var second = await service.RunAsync(meeting, options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal("user-owned content", File.ReadAllText(userFile));
        Assert.NotEqual(userFile, first.Export.DestinationPath);
        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task StaleCrashClaimRecoversAlreadyPublishedHashMatchingFile()
    {
        using var directory = new TestDirectory();
        var meeting = Meeting();
        var markdown = MeetingExporter.BuildMarkdown(meeting, MeetingExportMode.FullMeeting, null);
        var recoveredPath = directory.File("recovered-after-crash.md");
        File.WriteAllText(recoveredPath, markdown, new UTF8Encoding(false));
        var paths = PostMeetingMarkdownAutoExporter.GetControlPaths(
            directory.Path,
            meeting.Id,
            PostMeetingCompletionEvent.RecordingCompleted,
            MeetingExportMode.FullMeeting);
        Directory.CreateDirectory(paths.ControlDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(markdown))).ToLowerInvariant();
        var staleClaim = new PostMeetingMarkdownAutoExporter.ExportClaim(
            1,
            "crashed-owner",
            int.MaxValue,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            Path.GetFileName(recoveredPath),
            hash,
            "Muesli.PostMeetingAutomation");
        File.WriteAllText(
            Path.Combine(paths.ControlDirectory, ".owner.json"),
            "{\"schemaVersion\":1,\"owner\":\"Muesli.PostMeetingAutomation\",\"instanceToken\":\"phase9-test\"}");
        File.WriteAllText(paths.ClaimPath, JsonSerializer.Serialize(staleClaim, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };

        var result = await new PostMeetingAutomationService().RunAsync(
            meeting,
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(result.Export.Completed, result.Export.Error);
        Assert.Equal(recoveredPath, result.Export.DestinationPath);
        Assert.Equal(0, result.Export.Attempts);
        Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
        Assert.True(File.Exists(paths.ManifestPath));
        Assert.False(File.Exists(paths.ClaimPath));
    }

    [Fact]
    public async Task PersistedSuccessfulExportIsReusedWithoutCreatingACollisionDuplicate()
    {
        using var directory = new TestDirectory();
        var options = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path
        };
        var service = new PostMeetingAutomationService();
        var first = await service.RunAsync(Meeting(), options, PostMeetingCompletionEvent.RecordingCompleted);
        var priorRun = first;
        var reloaded = Meeting(automationResult: priorRun);

        var second = await service.RunAsync(reloaded, options, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(first.Export.DestinationPath, second.Export.DestinationPath);
        Assert.Single(Directory.GetFiles(directory.Path, "*.md"));
    }

    [Fact]
    public async Task PreviousExportIsReusedOnlyWhenRenderedContentHashMatchesNewMode()
    {
        using var directory = new TestDirectory();
        var service = new PostMeetingAutomationService();
        var notesOptions = new PostMeetingAutomationOptions
        {
            AutoExportEnabled = true,
            AutoExportDirectory = directory.Path,
            AutoExportMode = MeetingExportMode.Notes
        };
        var meeting = Meeting();
        var notes = await service.RunAsync(meeting, notesOptions, PostMeetingCompletionEvent.RecordingCompleted);
        var reloaded = Meeting(automationResult: notes);
        var fullOptions = notesOptions with { AutoExportMode = MeetingExportMode.FullMeeting };

        var full = await service.RunAsync(reloaded, fullOptions, PostMeetingCompletionEvent.RecordingCompleted);

        Assert.True(full.Export.Completed, full.Export.Error);
        Assert.NotEqual(notes.Export.DestinationPath, full.Export.DestinationPath);
        Assert.DoesNotContain("## Raw Transcript", File.ReadAllText(notes.Export.DestinationPath!), StringComparison.Ordinal);
        Assert.Contains("## Raw Transcript", File.ReadAllText(full.Export.DestinationPath!), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(directory.Path, "*.md").Length);
    }

    [Fact]
    public async Task ExportFailureDoesNotSuppressAnIndependentHookAttempt()
    {
        using var directory = new TestDirectory();
        var selectedFile = directory.File("not-a-directory");
        File.WriteAllText(selectedFile, "occupied");
        var options = HookOptions() with
        {
            AutoExportEnabled = true,
            AutoExportDirectory = selectedFile,
            RetryPolicy = new PostMeetingRetryPolicy { MaxAttempts = 2 }
        };

        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(transcript: "", summary: "", manualNotes: ""),
            options,
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.Failed, result.Status);
        Assert.False(result.Export.Completed);
        Assert.Equal(0, result.Export.Attempts);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("TITLE=", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MeetingSessionState.Failed)]
    [InlineData(MeetingSessionState.RecoverableInterruption)]
    [InlineData(MeetingSessionState.Cancelled)]
    public async Task ProductionAutomationRejectsAnyMeetingThatIsNotCompleted(MeetingSessionState state)
    {
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(sessionState: state),
            HookOptions(),
            PostMeetingCompletionEvent.RecordingCompleted);

        Assert.Equal(PostMeetingAutomationStatus.InvalidConfiguration, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.False(result.Export.Requested);
    }

    [Fact]
    public async Task ManualTestEventCannotBeUsedWithARealMeetingThroughRunAsync()
    {
        var result = await new PostMeetingAutomationService().RunAsync(
            Meeting(),
            HookOptions(),
            PostMeetingCompletionEvent.ManualTest);

        Assert.Equal(PostMeetingAutomationStatus.InvalidConfiguration, result.Status);
        Assert.Equal(0, result.Attempts);
    }

    [Fact]
    public void ImmutableResultRoundTripsThroughJsonForMeetingPersistence()
    {
        var result = new PostMeetingAutomationResult(
            Guid.NewGuid(),
            PostMeetingAutomationStatus.Failed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            2,
            17,
            "bounded output",
            "bounded error",
            false,
            false,
            "failed",
            new PostMeetingExportDiagnostic(true, true, @"C:\exports\meeting.md",
                AutomationDestinationOwnership.UserSelectedDestination, null, 1));

        var roundTrip = JsonSerializer.Deserialize<PostMeetingAutomationResult>(JsonSerializer.Serialize(result));

        Assert.Equal(result, roundTrip);
    }
}
