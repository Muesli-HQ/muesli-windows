using System.Text;
using System.Security.Cryptography;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class Phase4LiveTranscriptionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void StreamingCatalogPinsArchiveVadAndEveryRunnableFile()
    {
        var model = Assert.Single(StreamingModelCatalog.Models);
        Assert.Equal(StreamingModelCatalog.Nemotron35Id, model.Id);
        Assert.Matches("^[A-F0-9]{64}$", model.ArchiveSha256);
        Assert.Matches("^[A-F0-9]{64}$", model.VadSha256);
        Assert.Equal(4, model.RequiredFileSha256.Count);
        Assert.All(model.RequiredFileSha256, file =>
        {
            Assert.False(Path.IsPathRooted(file.Key));
            Assert.DoesNotContain("..", file.Key, StringComparison.Ordinal);
            Assert.Matches("^[A-F0-9]{64}$", file.Value);
        });
    }

    [Fact]
    public void SettingsKeepLiveOffByDefaultAndRoundTripExplicitOwnership()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());
        Assert.Null(store.Load().LiveMeetingModelId);

        store.Save(new MuesliSettings
        {
            LiveMeetingModelId = StreamingModelCatalog.Nemotron35Id,
            LiveTranscriptOwnership = "unified-live-final",
            ShowLiveWaveformOnHover = true
        });
        var loaded = store.Load();
        Assert.Equal(StreamingModelCatalog.Nemotron35Id, loaded.LiveMeetingModelId);
        Assert.Equal("unified-live-final", loaded.LiveTranscriptOwnership);
        Assert.True(loaded.ShowLiveWaveformOnHover);
    }

    [Fact]
    public void SettingsMigrationRejectsNonRunningStreamingArtifacts()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":3,\"LiveMeetingModelId\":\"parakeet-realtime-eou-coreml\",\"LiveTranscriptOwnership\":\"unified-live-final\"}");
        var loaded = new SettingsStore(path, new InMemorySecretStore()).Load();
        Assert.Null(loaded.LiveMeetingModelId);
        Assert.Equal("unified-live-final", loaded.LiveTranscriptOwnership);
    }

    [Fact]
    public async Task StreamingVerificationPinsEveryFileDetectsCorruptionAndHonorsCancellation()
    {
        using var directory = new TestDirectory();
        var required = new Dictionary<string, string>();
        foreach (var name in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            File.WriteAllBytes(directory.File(name), bytes);
            required[name] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        var vadBytes = Encoding.UTF8.GetBytes("silero");
        File.WriteAllBytes(directory.File("silero_vad.onnx"), vadBytes);
        var model = StreamingModelCatalog.Models[0] with
        {
            DirectoryName = directory.Path,
            RequiredFileSha256 = required,
            VadSha256 = Convert.ToHexString(SHA256.HashData(vadBytes))
        };
        var installer = new StreamingModelInstaller(model);
        await installer.VerifyAndStampAsync(null, CancellationToken.None);
        Assert.True(installer.IsVerified);

        File.AppendAllText(directory.File("tokens.txt"), "corrupt");
        Assert.False(installer.IsVerified);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.VerifyAndStampAsync(null, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.VerifyAndStampAsync(null, cancelled.Token));
    }

    [Fact]
    public void PcmNormalizerDownmixesAndResamplesWithoutResettingTimeline()
    {
        var normalizer = new StreamingPcmNormalizer();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var bytes = Enumerable.Range(0, 4800).SelectMany(index =>
            BitConverter.GetBytes(index % 2 == 0 ? 0.5f : -0.5f)).ToArray();
        var first = normalizer.Convert(bytes, bytes.Length, format);
        var second = normalizer.Convert(bytes, bytes.Length, format);
        Assert.InRange(first.Samples.Length, 795, 805);
        Assert.Equal(first.Samples.Length, second.StartSample);
        Assert.All(first.Samples, sample => Assert.InRange(sample, -0.001f, 0.001f));
    }

    [Fact]
    public async Task VadBoundaryMovesPartialTextIntoCommittedState()
    {
        var recognizer = new ScriptedRecognizer("hello", "hello world");
        var vad = new ScriptedVad(boundaryOnCall: 2);
        await using var session = new MeetingLiveTranscriptionSession(recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.PreviewOnly);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        await EventuallyAsync(() => session.Snapshot().PartialMicrophone == "hello");
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.2f], 1));
        await EventuallyAsync(() => session.Snapshot().Committed.Count == 1);
        var snapshot = session.Snapshot();
        Assert.Equal("hello world", snapshot.Committed[0].Text);
        Assert.Empty(snapshot.PartialMicrophone);
    }

    [Fact]
    public async Task DuplicateBoundaryResultsAreCommittedOnlyOnce()
    {
        var recognizer = new ScriptedRecognizer("same", "same");
        var vad = new ScriptedVad(boundaryEveryCall: true);
        await using var session = new MeetingLiveTranscriptionSession(recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        session.TryEnqueue(new(LiveTranscriptChannel.System, [0.1f], 0));
        session.TryEnqueue(new(LiveTranscriptChannel.System, [0.1f], 1));
        await EventuallyAsync(() => session.Snapshot().QueuedPackets == 0);
        var result = await session.FinishAsync();
        Assert.Single(result.Committed);
    }

    [Fact]
    public async Task QueuePressureIsBoundedAndRecordedAsRecoverableGap()
    {
        using var release = new ManualResetEventSlim();
        var recognizer = new BlockingRecognizer(release);
        await using var session = new MeetingLiveTranscriptionSession(recognizer, new ScriptedVad(), "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, capacity: 1);
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0)));
        Assert.True(recognizer.Entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 1)));
        Assert.False(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 2)));
        release.Set();
        var result = await session.FinishAsync();
        Assert.Equal(1, result.DroppedPackets);
        Assert.Contains(result.Gaps, gap => gap.Reason == "bounded-queue-pressure" && gap.StartSample == 2);
    }

    [Fact]
    public async Task CancellationStopsWorkerAndDisposesNativeOwners()
    {
        var recognizer = new ScriptedRecognizer("partial");
        var vad = new ScriptedVad();
        var session = new MeetingLiveTranscriptionSession(recognizer, vad, "model-a", LiveTranscriptOwnershipMode.PreviewOnly);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        session.Cancel();
        await session.DisposeAsync();
        Assert.True(recognizer.Disposed);
        Assert.True(vad.Disposed);
        Assert.False(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 1)));
    }

    [Theory]
    [InlineData(LiveTranscriptOwnershipMode.PreviewOnly, "offline-final", null)]
    [InlineData(LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, "live-model", "offline-final")]
    public void JournalRecordsExplicitFinalAndGapOwners(LiveTranscriptOwnershipMode mode, string finalOwner, string? gapOwner)
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_ownership", "Meeting", DateTimeOffset.UtcNow, "Default", "offline-final", true, null, "live-model", mode);
        Assert.Equal(finalOwner, journal.FinalTranscriptOwnerModelId);
        Assert.Equal(gapOwner, journal.GapRecoveryModelId);
    }

    [Fact]
    public void CompletedMeetingOwnershipSurvivesJournalDeletionAndReload()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = "meeting-live-owner",
            ModelProfile = "offline-final",
            LivePreviewModelId = "live-model",
            LiveTranscriptOwnership = "unified-live-final",
            FinalTranscriptOwnerModelId = "live-model",
            GapRecoveryModelId = "offline-final"
        }]);
        var loaded = Assert.Single(store.LoadMeetings());
        Assert.Equal("live-model", loaded.LivePreviewModelId);
        Assert.Equal("unified-live-final", loaded.LiveTranscriptOwnership);
        Assert.Equal("live-model", loaded.FinalTranscriptOwnerModelId);
        Assert.Equal("offline-final", loaded.GapRecoveryModelId);
    }

    [Fact]
    public async Task QueuePressureNeverPublishesFromTheCaptureThread()
    {
        using var release = new ManualResetEventSlim();
        var recognizer = new BlockingRecognizer(release);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, new ScriptedVad(), "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, capacity: 1);
        var publishThreads = new List<int>();
        session.SnapshotChanged += (_, _) => { lock (publishThreads) publishThreads.Add(Environment.CurrentManagedThreadId); };

        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0)));
        Assert.True(recognizer.Entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 1)));
        for (var packet = 0; packet < 40; packet++)
        {
            Assert.False(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 2 + packet)));
        }

        // Dropping a packet must stay off the audio callback: no snapshot copy, no dispatcher hop.
        lock (publishThreads) Assert.Empty(publishThreads);
        Assert.Equal(40, session.Snapshot().DroppedPackets);

        release.Set();
        var result = await session.FinishAsync();
        lock (publishThreads) Assert.DoesNotContain(Environment.CurrentManagedThreadId, publishThreads);
        Assert.Equal(40, result.DroppedPackets);
        Assert.Contains(result.Gaps, gap => gap.Reason == MeetingLiveTranscriptionSession.DroppedPacketReason);
    }

    [Fact]
    public async Task ContiguousDropsCoalesceIntoOneMeasuredGapRange()
    {
        using var release = new ManualResetEventSlim();
        var recognizer = new BlockingRecognizer(release);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, new ScriptedVad(), "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, capacity: 1);
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0)));
        Assert.True(recognizer.Entered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 1)));

        for (var index = 0; index < MeetingLiveTranscriptionSession.MaxPendingDropRanges + 200; index++)
        {
            // Every range is far enough apart that naive recording would keep one gap each.
            session.TryEnqueue(new(LiveTranscriptChannel.Microphone, new float[160], 10_000L * (index + 1)));
        }
        release.Set();
        var result = await session.FinishAsync();

        Assert.Equal(MeetingLiveTranscriptionSession.MaxPendingDropRanges + 200, result.DroppedPackets);
        var dropGaps = result.Gaps.Where(gap => gap.Reason == MeetingLiveTranscriptionSession.DroppedPacketReason).ToList();
        Assert.NotEmpty(dropGaps);
        Assert.True(dropGaps.Count <= MeetingLiveTranscriptionSession.MaxPendingDropRanges,
            $"Pending drop ranges must stay bounded; found {dropGaps.Count}.");
        Assert.Equal(10_000L, dropGaps.Min(gap => gap.StartSample));
    }

    [Fact]
    public async Task ProvisionalUpdatesAreCoalescedWhileCommitsPublishImmediately()
    {
        var recognizer = new ScriptedRecognizer("one", "two", "three", "four", "five", "six");
        var vad = new ScriptedVad(boundaryOnCall: 6);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.PreviewOnly,
            capacity: 8, publishInterval: TimeSpan.FromMinutes(5));
        var publications = 0;
        session.SnapshotChanged += (_, _) => Interlocked.Increment(ref publications);

        for (var packet = 0; packet < 5; packet++)
        {
            session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], packet));
        }
        await EventuallyAsync(() => session.Snapshot().QueuedPackets == 0);
        // A five-minute interval means none of the provisional-only packets may publish.
        Assert.Equal(0, Volatile.Read(ref publications));

        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 5));
        // Publication happens after the state lock is released, so wait on the counter itself.
        await EventuallyAsync(() => Volatile.Read(ref publications) >= 1);
        Assert.Single(session.Snapshot().Committed);

        var beforeFinish = Volatile.Read(ref publications);
        await session.FinishAsync();
        Assert.True(Volatile.Read(ref publications) > beforeFinish, "Finalization must publish the final state.");
    }

    [Fact]
    public async Task ShortPublishIntervalStillDeliversProvisionalTails()
    {
        var recognizer = new ScriptedRecognizer("provisional");
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, new ScriptedVad(), "qualified-model", LiveTranscriptOwnershipMode.PreviewOnly,
            capacity: 8, publishInterval: TimeSpan.Zero);
        LiveTranscriptSnapshot? latest = null;
        session.SnapshotChanged += (_, snapshot) => latest = snapshot;
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.4f, -0.9f], 0));
        await EventuallyAsync(() => latest?.PartialMicrophone == "provisional");
        Assert.Equal(0.9f, latest!.MicrophoneLevel, 3);
    }

    [Fact]
    public async Task SeveralVadBoundariesInOnePacketCommitOneSpanWithoutSpuriousGaps()
    {
        var recognizer = new ScriptedRecognizer("alpha beta");
        var vad = new StaticVad([(0, 100), (400, 900)]);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        var result = await session.FinishAsync();

        var segment = Assert.Single(result.Committed);
        Assert.Equal("alpha beta", segment.Text);
        Assert.Equal(0, segment.StartSample);
        Assert.Equal(900, segment.EndSample);
        // Committing per boundary would have produced empty text and a fabricated gap.
        Assert.DoesNotContain(result.Gaps, gap => gap.Reason == "speech-without-committed-text");
    }

    [Fact]
    public async Task FinalTailCommitSpansEveryFlushedBoundary()
    {
        var recognizer = new ScriptedRecognizer("closing remarks");
        var vad = new StaticVad(feedBoundaries: [], finishBoundaries: [(1000, 2000), (5000, 8000)]);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        var result = await session.FinishAsync();

        var segment = Assert.Single(result.Committed, candidate => candidate.Channel == LiveTranscriptChannel.Microphone);
        Assert.Equal("closing remarks", segment.Text);
        Assert.Equal(1000, segment.StartSample);
        Assert.Equal(8000, segment.EndSample);
    }

    [Fact]
    public async Task ShutdownWhileTheWorkerIsBlockedDisposesNativeOwnersAndStopsIntake()
    {
        using var release = new ManualResetEventSlim();
        var recognizer = new BlockingRecognizer(release);
        var vad = new ScriptedVad();
        var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "qualified-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, capacity: 1);
        Assert.True(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0)));
        Assert.True(recognizer.Entered.Wait(TimeSpan.FromSeconds(2)));
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 1));
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 2));

        var shutdown = session.DisposeAsync();
        release.Set();
        await shutdown;

        Assert.True(recognizer.Disposed);
        Assert.True(vad.Disposed);
        Assert.False(session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 3)));
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(null, LiveTranscriptOwnershipMode.PreviewOnly, "Off", "Final offline model", LiveTranscriptOwnershipDescriptor.GapRecoveryUnusedLabel, false)]
    [InlineData(null, LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, "Off", "Final offline model", LiveTranscriptOwnershipDescriptor.GapRecoveryUnusedLabel, false)]
    [InlineData("live-model", LiveTranscriptOwnershipMode.PreviewOnly, "Live model", "Final offline model", LiveTranscriptOwnershipDescriptor.GapRecoveryUnusedLabel, true)]
    [InlineData("live-model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, "Live model", "Live model", "Final offline model", true)]
    public void OwnershipDescriptorNamesEveryOwnerForEachMode(
        string? liveModelId,
        LiveTranscriptOwnershipMode mode,
        string preview,
        string final,
        string gapRecovery,
        bool enabled)
    {
        var descriptor = LiveTranscriptOwnershipDescriptor.Create(liveModelId, "Live model", mode, "Final offline model");
        Assert.Equal(preview, descriptor.LivePreviewOwner);
        Assert.Equal(final, descriptor.FinalTranscriptOwner);
        Assert.Equal(gapRecovery, descriptor.GapRecoveryOwner);
        Assert.Equal(enabled, descriptor.IsLiveEnabled);
    }

    [Theory]
    [InlineData(LiveTranscriptOwnershipMode.PreviewOnly)]
    [InlineData(LiveTranscriptOwnershipMode.UnifiedLiveAndFinal)]
    public void OwnershipModeRoundTripsThroughDisplayAndPersistedValues(LiveTranscriptOwnershipMode mode)
    {
        var display = LiveTranscriptOwnershipDescriptor.DisplayNameFor(mode);
        var persisted = LiveTranscriptOwnershipDescriptor.SettingValueFor(mode);
        Assert.Contains(display, LiveTranscriptOwnershipDescriptor.DisplayNames);
        Assert.Equal(mode, LiveTranscriptOwnershipDescriptor.ModeFromDisplayName(display));
        Assert.Equal(mode, LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(persisted));
    }

    [Fact]
    public void UnknownOwnershipValuesFallBackToPreviewOnlyRatherThanUnifiedOwnership()
    {
        Assert.Equal(LiveTranscriptOwnershipMode.PreviewOnly, LiveTranscriptOwnershipDescriptor.ModeFromSettingValue("off"));
        Assert.Equal(LiveTranscriptOwnershipMode.PreviewOnly, LiveTranscriptOwnershipDescriptor.ModeFromSettingValue(null));
        Assert.Equal(LiveTranscriptOwnershipMode.PreviewOnly, LiveTranscriptOwnershipDescriptor.ModeFromDisplayName("Something else"));
    }

    [Fact]
    public void ChangingTheSelectedLiveModelMovesOwnershipWithoutTouchingTheFinalModel()
    {
        var before = LiveTranscriptOwnershipDescriptor.Create(
            "nemotron", "Nemotron 3.5 Streaming", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, "Parakeet v3");
        var afterModelChange = LiveTranscriptOwnershipDescriptor.Create(
            null, LiveTranscriptOwnershipDescriptor.OffOwnerLabel, LiveTranscriptOwnershipMode.UnifiedLiveAndFinal, "Parakeet v3");

        Assert.Equal("Nemotron 3.5 Streaming", before.FinalTranscriptOwner);
        Assert.Equal("Parakeet v3", before.GapRecoveryOwner);
        // Turning the live model off must hand the whole transcript back to the final model.
        Assert.Equal("Parakeet v3", afterModelChange.FinalTranscriptOwner);
        Assert.False(afterModelChange.IsLiveEnabled);
    }

    [Fact]
    public void LiveModelPickerEntriesRenderTheirLabelInTheClosedComboBox()
    {
        // The shared ComboBox template renders SelectionBoxItem without SelectionBoxItemTemplate,
        // so a picker record without a ToString override leaks its compiler-generated form.
        Assert.Equal(LiveTranscriptOwnershipDescriptor.OffOwnerLabel, LiveModelChoice.Off.ToString());
        Assert.Null(LiveModelChoice.Off.Id);
        var nemotron = StreamingModelCatalog.Models[0];
        var choice = new LiveModelChoice(nemotron.Id, nemotron.PickerLabel);
        Assert.Equal(nemotron.PickerLabel, choice.ToString());
        Assert.DoesNotContain("LiveModelChoice", choice.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreparingALiveModelReportsReadyAndNeverSelectsIt()
    {
        string? selected = null;
        using var lifecycle = new StreamingModelLifecycleService(() => selected, _ => Task.CompletedTask);
        var installed = StreamingModelInstaller.HasValidVerificationStamp(StreamingModelCatalog.Models[0]);

        var unselected = lifecycle.Snapshot(StreamingModelCatalog.Nemotron35Id);
        Assert.NotEqual(TranscriptionModelStatus.Selected, unselected.Status);
        Assert.Contains("Selected for live meetings: False", unselected.Diagnostics);

        selected = StreamingModelCatalog.Nemotron35Id;
        var chosen = lifecycle.Snapshot(StreamingModelCatalog.Nemotron35Id);
        Assert.Contains("Selected for live meetings: True", chosen.Diagnostics);
        if (installed)
        {
            Assert.Equal(TranscriptionModelStatus.Ready, unselected.Status);
            Assert.Equal(TranscriptionModelStatus.Selected, chosen.Status);
        }
    }

    [Fact]
    public async Task UnifiedOwnershipKeepsLiveTextAndRecoversOnlyMeasuredGaps()
    {
        using var directory = new TestDirectory();
        var microphone = directory.File("mic.wav");
        WriteSilentWav(microphone, sampleRate: 48000, seconds: 12);
        var live = new MeetingLiveTranscriptionResult(
            [new("live_1", LiveTranscriptChannel.Microphone, 0, 16000, "committed live text")],
            [new(LiveTranscriptChannel.Microphone, 160000, 176000, MeetingLiveTranscriptionSession.DroppedPacketReason)],
            1,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        var requested = new List<string>();

        var (mic, system) = await MeetingGapRecoveryService.BuildUnifiedOwnerResultsAsync(
            live,
            new MeetingAudioPaths(microphone, null),
            "offline-final",
            (title, path) =>
            {
                requested.Add(title);
                return Task.FromResult(new TranscriptionResult(
                    "recovered words", null, 900,
                    [new TranscriptSegment("seg1", "You", 0, 900, "recovered words")]));
            },
            CancellationToken.None);

        Assert.Single(requested);
        Assert.Contains("Owner: live-model", mic.Diagnostic);
        Assert.Contains("recovery model: offline-final", mic.Diagnostic);
        Assert.Contains("measured gaps: 1", mic.Diagnostic);
        Assert.Contains("committed live text", mic.Text);
        Assert.Contains("recovered words", mic.Text);
        Assert.Equal(2, mic.Segments!.Count);
        // Recovered text is placed at the measured gap, not at the head of the meeting.
        Assert.True(mic.Segments[1].StartMs > mic.Segments[0].EndMs);
        Assert.Empty(system.Segments!);
    }

    [Fact]
    public async Task GapRecoveryDropsTextThatDuplicatesAnAlreadyCommittedLiveSegment()
    {
        using var directory = new TestDirectory();
        var microphone = directory.File("mic.wav");
        WriteSilentWav(microphone, sampleRate: 16000, seconds: 4);
        var live = new MeetingLiveTranscriptionResult(
            [new("live_1", LiveTranscriptChannel.Microphone, 0, 32000, "Shared sentence")],
            [new(LiveTranscriptChannel.Microphone, 0, 32000, "speech-without-committed-text")],
            0,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);

        var (mic, _) = await MeetingGapRecoveryService.BuildUnifiedOwnerResultsAsync(
            live,
            new MeetingAudioPaths(microphone, null),
            "offline-final",
            (_, _) => Task.FromResult(new TranscriptionResult(
                "shared   sentence", null, 2000,
                [new TranscriptSegment("seg1", "You", 0, 2000, "shared   sentence")])),
            CancellationToken.None);

        Assert.Single(mic.Segments!);
        Assert.Equal("Shared sentence", mic.Segments![0].Text);
    }

    [Fact]
    public async Task GapRecoveryHonoursCancellationBeforeTouchingTheRecoveryModel()
    {
        using var directory = new TestDirectory();
        var microphone = directory.File("mic.wav");
        WriteSilentWav(microphone, sampleRate: 16000, seconds: 2);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var live = new MeetingLiveTranscriptionResult(
            [],
            [new(LiveTranscriptChannel.Microphone, 0, 16000, MeetingLiveTranscriptionSession.DroppedPacketReason)],
            1,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MeetingGapRecoveryService.BuildUnifiedOwnerResultsAsync(
                live,
                new MeetingAudioPaths(microphone, null),
                "offline-final",
                (_, _) => Task.FromException<TranscriptionResult>(new InvalidOperationException("must not run")),
                cancelled.Token));
    }

    [Theory]
    [InlineData(16000, 32000, 32000)]
    [InlineData(48000, 32000, 96000)]
    [InlineData(44100, 16000, 44100)]
    public void MeasuredGapRangesRescaleFromLiveSamplesToNativeCaptureFrames(int sourceRate, long liveSample, long expectedFrame) =>
        Assert.Equal(expectedFrame, MeetingGapRecoveryService.ToSourceFrame(liveSample, sourceRate, long.MaxValue));

    [Fact]
    public void MeasuredGapRangesAreClampedToTheRetainedTrackLength() =>
        Assert.Equal(500, MeetingGapRecoveryService.ToSourceFrame(long.MaxValue / 4, 48000, 500));

    private static void WriteSilentWav(string path, int sampleRate, int seconds)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        writer.Write(new byte[sampleRate * 2 * seconds], 0, sampleRate * 2 * seconds);
    }

    [QualificationFact("MUESLI_STREAMING_QUALIFICATION_MODEL")]
    public async Task QualifiedArtifactPerformsRealInferenceWhenQualificationPathIsProvided()
    {
        var root = Environment.GetEnvironmentVariable("MUESLI_STREAMING_QUALIFICATION_MODEL")!;
        var catalog = StreamingModelCatalog.Models[0];
        var model = catalog with { DirectoryName = Path.GetFullPath(root) };
        await new StreamingModelInstaller(model).VerifyAndStampAsync(null, CancellationToken.None);
        var qualificationWav = Directory.EnumerateFiles(Path.Combine(root, "test_wavs"), "*.wav").OrderBy(path => path).First();
        var samples = ReadMono16k(qualificationWav);
        output.WriteLine($"fixture={Path.GetFileName(qualificationWav)}; samples={samples.Length}; seconds={samples.Length / 16000.0:F2}");

        using (var recognizer = new NativeLiveRecognizer(model))
        {
            string partial = "";
            const int chunk = 8960;
            for (var offset = 0; offset < samples.Length; offset += chunk)
                partial = recognizer.Feed(LiveTranscriptChannel.Microphone, samples.Skip(offset).Take(Math.Min(chunk, samples.Length - offset)).ToArray());
            var text = recognizer.Finish(LiveTranscriptChannel.Microphone);
            var decoded = text.Length >= partial.Length ? text : partial;
            output.WriteLine($"recognizer-only decoded: '{decoded}'");
            Assert.False(string.IsNullOrWhiteSpace(decoded));
        }

        using (var vad = new NativeSileroVad(model))
        {
            var boundaries = new List<(long StartSample, long EndSample)>();
            for (var offset = 0; offset < samples.Length; offset += 512)
                boundaries.AddRange(vad.Feed(LiveTranscriptChannel.Microphone, samples.Skip(offset).Take(Math.Min(512, samples.Length - offset)).ToArray()));
            boundaries.AddRange(vad.Finish(LiveTranscriptChannel.Microphone));
            output.WriteLine($"silero boundaries: {string.Join(", ", boundaries.Select(b => $"[{b.StartSample}-{b.EndSample}]"))}");
            Assert.NotEmpty(boundaries);
        }

        // Drive the real pipeline the product uses: shared PCM into the bounded session,
        // native transducer plus native Silero, boundary-only commits, no second capture owner.
        await using var session = new MeetingLiveTranscriptionSession(model, LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        var sawProvisionalBeforeCommit = false;
        session.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Committed.Count == 0 && !string.IsNullOrWhiteSpace(snapshot.PartialMicrophone))
                sawProvisionalBeforeCommit = true;
        };
        const int packet = 1600; // 100 ms of 16 kHz audio, the realistic capture cadence
        var enqueued = 0;
        // Deliberately faster than real time so the bounded queue is exercised against the
        // real model; drops here are the harness outrunning inference, not a capture fault.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var offset = 0; offset < samples.Length; offset += packet)
        {
            var slice = samples.Skip(offset).Take(Math.Min(packet, samples.Length - offset)).ToArray();
            if (session.TryEnqueue(new(LiveTranscriptChannel.Microphone, slice, offset))) enqueued++;
            await Task.Delay(5);
        }
        var result = await session.FinishAsync();
        clock.Stop();

        var processedSeconds = enqueued * packet / 16000.0;
        var realTimeFactor = clock.Elapsed.TotalSeconds / Math.Max(0.001, processedSeconds);
        output.WriteLine($"session model={result.ModelId}; ownership={result.OwnershipMode}; dropped={result.DroppedPackets}");
        output.WriteLine($"processed={processedSeconds:F2}s audio in {clock.Elapsed.TotalSeconds:F2}s wall; realTimeFactor={realTimeFactor:F2} (cpu provider)");
        foreach (var segment in result.Committed)
            output.WriteLine($"  committed [{segment.StartSample}-{segment.EndSample}] {segment.Channel}: '{segment.Text}'");
        foreach (var gap in result.Gaps)
            output.WriteLine($"  gap [{gap.StartSample}-{gap.EndSample}] {gap.Channel}: {gap.Reason}");

        Assert.NotEmpty(result.Committed);
        Assert.All(result.Committed, segment => Assert.False(string.IsNullOrWhiteSpace(segment.Text)));
        Assert.All(result.Committed, segment => Assert.True(segment.EndSample > segment.StartSample));
        Assert.True(sawProvisionalBeforeCommit, "A provisional tail must be visible before the first VAD commit.");
        Assert.Equal(StreamingModelCatalog.Nemotron35Id, result.ModelId);
        Assert.DoesNotContain(result.Gaps, gap => gap.Reason == "streaming-engine-failure");
    }

    private static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
        var samples = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.Take(read));
        return samples.ToArray();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class ScriptedRecognizer(params string[] results) : ILiveRecognizer
    {
        private int _feed;
        private readonly Dictionary<LiveTranscriptChannel, string> _current = [];
        public bool Disposed { get; private set; }
        public string Feed(LiveTranscriptChannel channel, float[] samples)
        {
            var text = results.Length == 0 ? "" : results[Math.Min(_feed++, results.Length - 1)];
            _current[channel] = text;
            return text;
        }
        public string CommitBoundary(LiveTranscriptChannel channel) => _current.GetValueOrDefault(channel, "");
        public string Finish(LiveTranscriptChannel channel) => _current.GetValueOrDefault(channel, "");
        public void Dispose() => Disposed = true;
    }

    private sealed class ScriptedVad(int boundaryOnCall = -1, bool boundaryEveryCall = false) : ILiveVad
    {
        private int _calls;
        public bool Disposed { get; private set; }
        public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
        {
            _calls++;
            return boundaryEveryCall || _calls == boundaryOnCall ? [(Math.Max(0, _calls - 1), _calls)] : [];
        }
        public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel) => [];
        public void Dispose() => Disposed = true;
    }

    /// <summary>Emits a fixed boundary set so grouped-boundary handling can be asserted exactly.</summary>
    private sealed class StaticVad(
        IReadOnlyList<(long StartSample, long EndSample)> feedBoundaries,
        IReadOnlyList<(long StartSample, long EndSample)>? finishBoundaries = null) : ILiveVad
    {
        private bool _fed;
        public bool Disposed { get; private set; }
        public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
        {
            if (channel != LiveTranscriptChannel.Microphone || _fed) return [];
            _fed = true;
            return feedBoundaries;
        }
        public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel) =>
            channel == LiveTranscriptChannel.Microphone ? finishBoundaries ?? [] : [];
        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingRecognizer(ManualResetEventSlim release) : ILiveRecognizer
    {
        public ManualResetEventSlim Entered { get; } = new();
        public bool Disposed { get; private set; }
        public string Feed(LiveTranscriptChannel channel, float[] samples) { Entered.Set(); release.Wait(); return ""; }
        public string CommitBoundary(LiveTranscriptChannel channel) => "";
        public string Finish(LiveTranscriptChannel channel) => "";
        public void Dispose() => Disposed = true;
    }
}
