namespace Muesli.Windows.Tests;

public sealed class CapturePrivacyAndRuntimeTests
{
    [Fact]
    public void MeetingRetentionUsesUniqueStablePathsAndProtectsImports()
    {
        using var directory = new TestDirectory();
        var storage = new CaptureStorageService(directory.Path);
        var firstSource = directory.File("native-first.wav");
        var secondSource = directory.File("native-second.wav");
        File.WriteAllBytes(firstSource, [1, 2, 3]);
        File.WriteAllBytes(secondSource, [4, 5, 6]);
        using var first = Audio(firstSource);
        using var second = Audio(secondSource);

        var firstPaths = storage.PersistMeetingAudio("meet_1", first, null);
        var secondPaths = storage.PersistMeetingAudio("meet_2", second, null);

        Assert.NotEqual(firstPaths.MicrophonePath, secondPaths.MicrophonePath);
        Assert.True(File.Exists(firstPaths.MicrophonePath));
        Assert.True(storage.IsOwnedMeetingAudioPath("meet_1", firstPaths.MicrophonePath));
        var import = directory.File("imported.mp3");
        File.WriteAllText(import, "user owned");
        Assert.False(storage.IsOwnedMeetingAudioPath("meet_1", import));
        storage.DeleteOwnedMeetingAudio("meet_1", new[] { import, firstPaths.MicrophonePath! });
        Assert.True(File.Exists(import));
        Assert.False(File.Exists(firstPaths.MicrophonePath));
    }

    [Fact]
    public void TransientAudioCleanupIsIdempotentAndLegacyInventoryExcludesRetainedMeetingFiles()
    {
        using var directory = new TestDirectory();
        var transient = directory.File("native-old.wav");
        File.WriteAllBytes(transient, new byte[10]);
        var latest = directory.File("last-dictation.wav");
        File.WriteAllBytes(latest, new byte[20]);
        var retainedDir = System.IO.Path.Combine(directory.Path, "recordings", "meet_1");
        Directory.CreateDirectory(retainedDir);
        File.WriteAllBytes(System.IO.Path.Combine(retainedDir, "microphone.wav"), new byte[30]);
        var storage = new CaptureStorageService(directory.Path);

        var inventory = storage.InspectLegacyAndTransientCaptures();
        Assert.Single(inventory.Paths);
        Assert.Equal(10, inventory.TotalBytes);
        storage.DeleteLegacyAndTransientCaptures(inventory);
        Assert.True(File.Exists(latest));
        Assert.True(File.Exists(System.IO.Path.Combine(retainedDir, "microphone.wav")));

        var owned = directory.File("meeting-system-temp.wav");
        File.WriteAllText(owned, "temp");
        var audio = Audio(owned);
        audio.Dispose();
        audio.Dispose();
        Assert.False(File.Exists(owned));
    }

    [Fact]
    public void LogRedactionAndRetentionArePrivacySafe()
    {
        var redacted = AppLogService.Redact("url=https://meet.google.com/abc-defg-hij; targetWindowTitle=Budget.xlsx - Excel; done");
        Assert.DoesNotContain("meet.google.com", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Budget.xlsx", redacted, StringComparison.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var selected = AppLogService.SelectLogsForDeletion(
            [
                new LogRetentionEntry("new", 8, now),
                new LogRetentionEntry("old", 1, now.AddDays(-20)),
                new LogRetentionEntry("middle", 8, now.AddDays(-1))
            ],
            now,
            TimeSpan.FromDays(14),
            10);
        Assert.Contains("old", selected);
        Assert.Contains("middle", selected);
        Assert.DoesNotContain("new", selected);
    }

    [Fact]
    public void RuntimeFailuresNeverMapToReady()
    {
        var failed = RuntimeStatusMapper.Map(null, new InvalidOperationException());
        Assert.Equal("Diagnostics failed", failed.RuntimeStatus);
        Assert.Equal("Unknown", failed.ProviderStatus);

        var cpu = RuntimeStatusMapper.Map(new RuntimeDiagnostics(
            "Native", true, true, "CPU", "Ready", "Ready", "Disabled", "cache", 0, "detail"));
        Assert.Equal("Runtime available", cpu.RuntimeStatus);
        Assert.Equal("CPU", cpu.ProviderStatus);
    }

    [Fact]
    public void CaptureFinalizationFailureDeletesDetachedSourceAndDerivedFiles()
    {
        using var directory = new TestDirectory();
        var source = directory.File("native-detached.wav");
        var derived = directory.File("normalized-partial.wav");
        File.WriteAllText(source, "source audio");
        File.WriteAllText(derived, "partial normalized audio");

        Action failFinalization = () =>
        {
            using var cleanup = new CaptureFinalizationCleanup(source);
            cleanup.Track(derived);
            throw new InvalidOperationException("simulated finalization failure");
        };

        Assert.Throws<InvalidOperationException>(failFinalization);

        Assert.False(File.Exists(source));
        Assert.False(File.Exists(derived));
    }

    [Fact]
    public void CompletedCaptureFinalizationTransfersFilesToCapturedAudioOwner()
    {
        using var directory = new TestDirectory();
        var source = directory.File("native-complete.wav");
        File.WriteAllText(source, "audio");

        using (var cleanup = new CaptureFinalizationCleanup(source))
        {
            cleanup.Complete();
        }

        Assert.True(File.Exists(source));
        using (Audio(source))
        {
        }
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task MeetingCleanupWaitsForUnobservedDiarizationAndContainsItsFailure()
    {
        var diarization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reported = null;

        var cleanupWait = MeetingRecordingCoordinator.AwaitDiarizationBeforeCleanupAsync(
            diarization.Task,
            alreadyObserved: false,
            exception => reported = exception);

        Assert.False(cleanupWait.IsCompleted);
        diarization.SetException(new InvalidOperationException("simulated native failure"));
        await cleanupWait;

        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void SuccessfulDiarizationEvidenceRemovesStaleFailureWarnings()
    {
        var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings(
            [
                "Speaker diarization failed; transcript used fallback speaker labels.",
                "Speaker diarization returned no speaker segments; transcript used [System audio] fallback."
            ],
            transcript: "[00:00:02] Speaker 1: Hello from the meeting.");

        Assert.Empty(warnings);
    }

    private static CapturedAudio Audio(string path) => new(
        path, path, [path], new FileInfo(path).Length, 1, 0, 0, "test", "test");
}
