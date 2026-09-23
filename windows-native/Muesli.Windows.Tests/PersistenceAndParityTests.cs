using System.Text.Json;
using System.IO;
using Muesli.Windows.Services;
using NAudio.Wave;
using Xunit;

namespace Muesli.Windows.Tests;

public sealed class PersistenceAndParityTests
{
    [Fact]
    public void LoadsLegacyArrayAndWritesVersionedEnvelope()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.json");
        File.WriteAllText(path, "[\"one\",\"two\"]");
        var store = new AtomicJsonFile();

        Assert.Equal(["one", "two"], store.Load(path, new List<string>()).Value);
        store.Save(path, new[] { "three" });

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(AtomicJsonFile.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void RecoversFromBackupAndQuarantinesCorruption()
    {
        using var directory = new TestDirectory();
        var path = directory.File("history.json");
        var store = new AtomicJsonFile();
        store.Save(path, new[] { "first" });
        store.Save(path, new[] { "second" });
        File.WriteAllText(path, "{broken");

        var result = store.Load(path, Array.Empty<string>());

        Assert.True(result.RecoveredFromBackup);
        Assert.Equal(["first"], result.Value);
        Assert.Contains(Directory.EnumerateFiles(directory.Path), file => file.Contains(".corrupt-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Um, please, you know, send this.", "Please, send this.")]
    [InlineData("like, this is ready", "This is ready")]
    [InlineData("I like this result.", "I like this result.")]
    [InlineData("kind of sort of finished", "Finished")]
    public void FillerRemovalIsDeterministic(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Apply(input));

    [Fact]
    public void FillerRemovalPreservesTranscriptLineBreaks()
    {
        var input = "[10:00:00] You: Um, hello.\r\n\r\n[10:00:02] System audio: Ahh, welcome.";

        var filtered = FillerWordFilter.Apply(input);

        Assert.Contains("\r\n\r\n", filtered);
        Assert.Contains("[10:00:00] You: hello.", filtered);
        Assert.Contains("[10:00:02] System audio: welcome.", filtered);
    }

    [Fact]
    public void TranscriptFallbackKeepsBothTracksInChronologicalOrder()
    {
        var meetingStart = new DateTime(2026, 9, 20, 10, 0, 0);
        var mic = new List<TranscriptSegment>
        {
            new("mic-1", "", 1000, 1800, "My first response."),
            new("mic-2", "", 4000, 4700, "My second response.")
        };
        var system = new List<TranscriptSegment>
        {
            new("system-1", "", 0, 800, "Remote opening."),
            new("system-2", "", 2500, 3300, "Remote follow-up.")
        };

        var result = TranscriptFormatter.MergeWithSegments(mic, system, [], meetingStart);

        Assert.Equal(
            ["System audio", "You", "System audio", "You"],
            result.Segments.Select(segment => segment.Speaker).ToArray());
        Assert.True(result.Transcript.IndexOf("Remote opening.", StringComparison.Ordinal)
                    < result.Transcript.IndexOf("My first response.", StringComparison.Ordinal));
        Assert.True(result.Transcript.IndexOf("My first response.", StringComparison.Ordinal)
                    < result.Transcript.IndexOf("Remote follow-up.", StringComparison.Ordinal));
        Assert.DoesNotContain("[You]", result.Transcript);
    }

    [Fact]
    public void PortableDictionaryAcceptsMacAndCliSchema()
    {
        using var directory = new TestDirectory();
        var path = directory.File("dictionary.json");
        File.WriteAllText(path, "[{\"word\":\"museli\",\"replacement\":\"Muesli\",\"matching_threshold\":0.9}]");

        var entry = Assert.Single(DictionaryPortabilityService.Import(path));

        Assert.Equal("museli", entry.Phrase);
        Assert.Equal("Muesli", entry.Replacement);
        Assert.Equal(0.9, entry.MatchingThreshold);
    }

    [Fact]
    public void PlaintextKeysMigrateToSecretStoreAndAreRemovedFromJson()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"OpenAIApiKey\":\"secret-openai\",\"OpenRouterApiKey\":\"secret-router\"}");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(secrets, path);

        var settings = store.Load();

        Assert.Equal("secret-openai", settings.OpenAIApiKey);
        Assert.Equal("secret-router", settings.OpenRouterApiKey);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("secret-openai", persisted);
        Assert.DoesNotContain("secret-router", persisted);
    }

    [Fact]
    public void SourceBuildFindsRepositoryRootPastCopiedWorkerDirectory()
    {
        var root = WorkerRuntimeLocator.FindRepoRoot(AppContext.BaseDirectory);

        Assert.NotNull(root);
        Assert.True(File.Exists(Path.Combine(root!, "worker", "transcribe_worker.py")));
        Assert.True(File.Exists(Path.Combine(
            root!,
            "windows-native",
            "Muesli.Windows",
            "Muesli.Windows.csproj")));
        Assert.NotEqual(
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(root!),
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Zoom Workplace", "Zoom")]
    [InlineData("Zoom", "Zoom")]
    [InlineData("Join from Zoom Workplace app - Zoom - Google Chrome", "chrome")]
    [InlineData("Microsoft Teams", "ms-teams")]
    [InlineData("Webex", "Webex")]
    public void PlatformHomeWindowsAreNotMeetings(string title, string processName) =>
        Assert.Null(MeetingDetectionService.DetectPlatform(title, processName, ""));

    [Fact]
    public void ZoomAppHandoffPageIsNotAnActiveBrowserMeeting() =>
        Assert.Null(MeetingDetectionService.DetectPlatform(
            "Join from Zoom Workplace app - Zoom - Google Chrome",
            "chrome",
            "https://zoom.us/j/123456789"));

    [Theory]
    [InlineData("Product review - Zoom Meeting", "Zoom", "Zoom")]
    [InlineData("Weekly sync | Microsoft Teams", "ms-teams", "Microsoft Teams")]
    [InlineData("Meet - abc-defg-hij", "chrome", "Google Meet")]
    public void ActiveMeetingWindowsAreDetected(string title, string processName, string expected) =>
        Assert.Equal(expected, MeetingDetectionService.DetectPlatform(title, processName, ""));

    [Fact]
    public void BareZoomWindowNeedsLiveMeetingEvidence()
    {
        var meeting = new DetectedMeeting("Zoom", "Zoom meeting", "Zoom Meeting", "Zoom", "", "zoom", 123);

        Assert.True(MeetingDetectionService.RequiresLiveZoomEvidence(meeting));
        Assert.False(MeetingDetectionService.BareZoomWindowHasLiveEvidence(false, false, false));
        Assert.True(MeetingDetectionService.BareZoomWindowHasLiveEvidence(true, false, false));
        Assert.True(MeetingDetectionService.BareZoomWindowHasLiveEvidence(false, false, true));
    }

    [Fact]
    public void MeetingCandidateMustRemainStableAcrossScans()
    {
        var gate = new MeetingDetectionStabilityGate();

        Assert.False(gate.Observe("zoom", 3));
        Assert.False(gate.Observe("zoom", 3));
        Assert.True(gate.Observe("zoom", 3));
        gate.Reset();
        Assert.False(gate.Observe("zoom", 3));
    }

    [Fact]
    public void ZoomWindowProcessChangesStillBelongToTheActiveZoomMeeting()
    {
        var detected = new DetectedMeeting(
            "Zoom",
            "Zoom meeting",
            "Zoom Meeting",
            "Zoom",
            "",
            "zoom|zoom|",
            456);

        Assert.True(MeetingRecordingSafetyPolicy.IsSameMeeting("zoom|cpthost|", 123, detected));
        Assert.False(MeetingRecordingSafetyPolicy.IsSameMeeting("microsoft teams|ms-teams|", 123, detected));
    }

    [Fact]
    public void ProcessAudioQualificationRejectsDigitalSilence()
    {
        var silence = new byte[4096];
        var speechLike = new byte[4096];
        for (var offset = 0; offset < 64; offset += 2)
        {
            BitConverter.GetBytes((short)2048).CopyTo(speechLike, offset);
        }

        Assert.False(SystemAudioCaptureService.ContainsAudiblePcm16(silence, silence.Length));
        Assert.True(SystemAudioCaptureService.ContainsAudiblePcm16(speechLike, speechLike.Length));
    }

    [Fact]
    public void RouteRecoveryAndSafetyFallbackWarningsRemainVisible()
    {
        var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings([
            "Microphone device changed during the meeting (0x88890004).",
            "Microphone capture resumed on the current communications device.",
            "Meeting-process audio remained silent during qualification; Muesli switched to all-system audio capture so remote speech would not be lost."
        ]);

        Assert.Contains(warnings, warning => warning.StartsWith("Microphone device changed", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.StartsWith("Microphone capture resumed", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("switched to all-system", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureWriterRotatesBeforeWaveFileLimit()
    {
        using var directory = new TestDirectory();
        var format = new WaveFormat(8000, 16, 1);
        using var writer = new SegmentedWaveCaptureWriter(directory.Path, "capture", format, maxSegmentBytes: 1024);
        writer.Write(new byte[4096], 0, 4096);

        var paths = writer.Complete();

        Assert.Equal(4, paths.Count);
        Assert.All(paths, path =>
        {
            Assert.True(File.Exists(path));
            using var reader = new WaveFileReader(path);
            Assert.Equal(1024, reader.Length);
        });
    }

    [Fact]
    public void TimedWaveNormalizationPreservesRecoveryGap()
    {
        using var directory = new TestDirectory();
        var first = directory.File("first.wav");
        var recovered = directory.File("recovered.wav");
        var normalized = directory.File("normalized.wav");
        WriteSilence(first, milliseconds: 100);
        WriteSilence(recovered, milliseconds: 100);

        WaveFileUtilities.NormalizeToWhisperWav(
            [
                new TimedWaveSource([first], 0),
                new TimedWaveSource([recovered], 500)
            ],
            normalized);

        using var reader = new WaveFileReader(normalized);
        Assert.InRange(reader.TotalTime.TotalMilliseconds, 590, 630);
    }

    [Fact]
    public void LongWaveFilesAreMarkedForChunking()
    {
        using var directory = new TestDirectory();
        var path = directory.File("long.wav");
        var format = new WaveFormat(1000, 16, 1);
        using (var writer = new WaveFileWriter(path, format))
        {
            writer.Write(new byte[1000 * 2 * 9 * 60]);
        }

        Assert.True(TranscriptionWorkerClient.ShouldChunkWaveFile(path));
    }

    [Fact]
    public void ProcessingMeetingStatusSurvivesPersistence()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            Id = "meet_processing",
            Title = "Recovery test",
            CreatedAt = DateTime.Now,
            SourcePath = "mic.wav; system.wav",
            Status = "processing"
        }]);

        var meeting = Assert.Single(store.LoadMeetings());

        Assert.Equal("processing", meeting.Status);
        Assert.Equal("mic.wav; system.wav", meeting.SourcePath);
    }

    [Fact]
    public void TimedTranscriptSegmentsAndDiarizationStatusSurvivePersistence()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            Id = "meet_timed",
            Title = "Timed transcript",
            TranscriptSegments =
            [
                new MeetingTranscriptSegment("microphone", "You", 1250, 2200, "Hello")
            ],
            DiarizationStatus = "fallback"
        }]);

        var meeting = Assert.Single(store.LoadMeetings());

        var segment = Assert.Single(meeting.TranscriptSegments);
        Assert.Equal(1250, segment.StartMs);
        Assert.Equal("microphone", segment.Track);
        Assert.Equal("fallback", meeting.DiarizationStatus);
    }

    [Fact]
    public void MissingDiarizationDependencyProducesExplicitFallbackStatus()
    {
        var warning = MeetingRecordingCoordinator.DescribeDiarizationFailure(
            new InvalidOperationException("No module named 'soundfile'"));

        Assert.Contains("not installed", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chronological", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialTranscriptionWarningsRemainVisible()
    {
        var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings([
            "System transcription failed: timed out",
            "Meeting saved with a partial transcript because one or more audio tracks failed."
        ]);

        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void AllSystemAudioFallbackWarningRemainsVisible()
    {
        var warnings = MeetingRecordingCoordinator.CleanupHealthWarnings([
            "No live target process was available. All system audio is being captured as a fallback."
        ]);

        Assert.Contains(warnings, warning => warning.Contains("unrelated computer sounds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessLoopbackRequiresALiveTargetProcess()
    {
        var capability = WindowsProcessLoopbackSupport.Inspect(int.MaxValue, new Version(10, 0, 22631));

        Assert.True(capability.OperatingSystemSupported);
        Assert.False(capability.TargetProcessAvailable);
        Assert.False(capability.CanAttempt);
    }

    [Fact]
    public void DiskSafetyPolicyUsesConfiguredThreshold()
    {
        using var directory = new TestDirectory();

        Assert.False(MeetingRecordingSafetyPolicy.IsDiskCriticallyLow(directory.Path, 0));
        Assert.True(MeetingRecordingSafetyPolicy.IsDiskCriticallyLow(directory.Path, long.MaxValue));
    }

    [Fact]
    public void InterruptedMeetingJournalFindsAndRepairsOnlyItsSessionAudio()
    {
        using var directory = new TestDirectory();
        var captures = Path.Combine(directory.Path, "captures");
        Directory.CreateDirectory(captures);
        var sessionId = Guid.NewGuid().ToString("N");
        var ownedPath = Path.Combine(captures, $"meeting-{sessionId}-microphone.wav");
        var unrelatedPath = Path.Combine(captures, "meeting-someone-else.wav");
        WriteTestWave(ownedPath);
        WriteTestWave(unrelatedPath);
        CorruptWaveLengthFields(ownedPath);
        var store = new MeetingSessionJournalStore(directory.Path, captures);
        var session = new ActiveMeetingSession(sessionId, "Recovery", DateTime.Now, 123, "key", "meeting-process");
        store.Save(session);

        var loaded = Assert.IsType<ActiveMeetingSession>(store.Load());
        var found = store.DiscoverAudio(loaded);

        Assert.Equal(ownedPath, Assert.Single(found));
        using var reader = new WaveFileReader(ownedPath);
        Assert.True(reader.Length > 0);
    }

    [Fact]
    public void CompletedMeetingCleanupDeletesRawAndNormalizedAudio()
    {
        using var directory = new TestDirectory();
        var raw = directory.File("raw.wav");
        var normalized = directory.File("normalized.wav");
        File.WriteAllBytes(raw, [1]);
        File.WriteAllBytes(normalized, [2]);

        var failures = MeetingAudioCleanup.DeleteTemporaryFiles([raw, normalized], "");

        Assert.Empty(failures);
        Assert.False(File.Exists(raw));
        Assert.False(File.Exists(normalized));
    }

    [Fact]
    public void PartialMeetingCleanupKeepsOnlyNamedRecoveryAudio()
    {
        using var directory = new TestDirectory();
        var raw = directory.File("raw.wav");
        var normalized = directory.File("normalized.wav");
        File.WriteAllBytes(raw, [1]);
        File.WriteAllBytes(normalized, [2]);

        var failures = MeetingAudioCleanup.DeleteTemporaryFiles([raw, normalized], normalized);

        Assert.Empty(failures);
        Assert.False(File.Exists(raw));
        Assert.True(File.Exists(normalized));
    }

    private static void WriteTestWave(string path)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(8000, 16, 1));
        writer.Write(new byte[1600]);
    }

    private static void WriteSilence(string path, int milliseconds)
    {
        var format = new WaveFormat(16000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        writer.Write(new byte[format.AverageBytesPerSecond * milliseconds / 1000]);
    }

    private static void CorruptWaveLengthFields(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Array.Clear(bytes, 4, 4);
        var dataMarker = System.Text.Encoding.ASCII.GetBytes("data");
        var dataIndex = -1;
        for (var index = 12; index <= bytes.Length - 8; index++)
        {
            if (bytes.AsSpan(index, 4).SequenceEqual(dataMarker))
            {
                dataIndex = index;
                break;
            }
        }
        Assert.True(dataIndex >= 0);
        Array.Clear(bytes, dataIndex + 4, 4);
        File.WriteAllBytes(path, bytes);
    }
}

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"muesli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }
    public string Path { get; }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
