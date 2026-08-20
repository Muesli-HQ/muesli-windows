using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// L23 / MTG-03 service slice: transcript save/cancel with optimistic backup, and candidate-based
/// retranscription that must not destroy the prior transcript on cancel, timeout, missing audio,
/// corrupt audio, ASR failure, or crash recovery.
/// </summary>
public sealed class TranscriptEditServiceTests
{
    private const string OriginalTranscript = "[09:00:00] You: we agreed to ship on Friday and Priya will own the release notes.";
    private const string EditedTranscript = "[09:00:00] You: we agreed to ship on Monday and Priya will own the release notes.";
    private const string CandidateTranscript = "[09:00:00] You: the candidate transcript from retranscription.";
    private const string ManualNotes = "Remember: Priya is on leave next week.";
    private const string GeneratedNotes = "## Summary\n- Ship on Friday";
    private const string ManualTitle = "Board sync";

    [Fact]
    public void SaveTranscriptEditPersistsAndCancelRestoresPrevious()
    {
        using var harness = Harness();
        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        Assert.True(begin.Succeeded);
        Assert.NotNull(begin.Session);

        var saved = harness.Service.SaveTranscriptEdit(begin.Session!, EditedTranscript);
        Assert.True(saved.Succeeded);
        Assert.True(saved.AppliedToMeeting);
        Assert.Equal(TranscriptEditOutcome.Saved, saved.Outcome);
        Assert.Equal(EditedTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Equal(ManualNotes, harness.Store.Find("meet_1")!.ManualNotes);

        var beginAgain = harness.Service.BeginTranscriptEdit("meet_1");
        var cancelled = harness.Service.CancelTranscriptEdit(beginAgain.Session!);
        Assert.False(cancelled.Succeeded);
        Assert.False(cancelled.AppliedToMeeting);
        Assert.Equal(TranscriptEditOutcome.Cancelled, cancelled.Outcome);
        Assert.DoesNotContain("success", cancelled.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EditedTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Contains("restored", cancelled.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelDuringDraftDoesNotPersistTheDraft()
    {
        using var harness = Harness();
        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        var cancelled = harness.Service.CancelTranscriptEdit(begin.Session!);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.False(cancelled.Succeeded);
        Assert.Equal(TranscriptEditOutcome.Cancelled, cancelled.Outcome);
    }

    [Fact]
    public void OptimisticBackupIsRestoredIfSaveFails()
    {
        using var directory = new TestDirectory();
        var inner = new MemoryTranscriptMeetingStore();
        inner.Save(Meeting());
        var store = new FailingTranscriptStore(inner);
        store.FailWhenTranscriptEquals = EditedTranscript;
        using var service = CreateService(store, directory.Path);

        var begin = service.BeginTranscriptEdit("meet_1");
        var result = service.SaveTranscriptEdit(begin.Session!, EditedTranscript);

        Assert.False(result.Succeeded);
        Assert.False(result.AppliedToMeeting);
        Assert.Equal(TranscriptEditOutcome.SaveFailed, result.Outcome);
        Assert.True(result.RestoredFromBackup);
        Assert.Contains("restored", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);
        Assert.Equal(ManualNotes, store.Find("meet_1")!.ManualNotes);
        Assert.Equal(GeneratedNotes, store.Find("meet_1")!.Summary);
    }

    [Fact]
    public void EditThatChangesTranscriptMarksGeneratedNotesStaleAndLeavesManualNotes()
    {
        using var harness = Harness();
        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        var result = harness.Service.SaveTranscriptEdit(begin.Session!, EditedTranscript);

        Assert.True(result.RequestsResummary);
        Assert.True(result.GeneratedNotesStale);
        Assert.True(harness.Service.IsGeneratedNotesStale("meet_1"));
        Assert.Contains("Re-summarize", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("written notes were not changed", result.Status, StringComparison.OrdinalIgnoreCase);
        var meeting = harness.Store.Find("meet_1")!;
        Assert.Equal(ManualNotes, meeting.ManualNotes);
        Assert.Equal(GeneratedNotes, meeting.Summary);
        Assert.Equal(ManualTitle, meeting.Title);
        Assert.True(meeting.TitleIsManual);
    }

    [Fact]
    public void UnchangedEditDoesNotRequestResummary()
    {
        using var harness = Harness();
        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        var result = harness.Service.SaveTranscriptEdit(begin.Session!, OriginalTranscript);
        Assert.Equal(TranscriptEditOutcome.Unchanged, result.Outcome);
        Assert.False(result.RequestsResummary);
        Assert.False(harness.Service.IsGeneratedNotesStale("meet_1"));
    }

    [Fact]
    public async Task RetranscribeWithMissingAudioFailsClosedAndLeavesOriginalIntact()
    {
        using var harness = Harness(audioPath: null);
        var result = await harness.Service.RetranscribeAsync("meet_1");

        Assert.False(result.Succeeded);
        Assert.False(result.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.MissingAudio, result.Outcome);
        Assert.DoesNotContain("success", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not changed", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Equal(0, harness.Asr.Calls);
        Assert.Null(harness.Service.GetPendingCandidate("meet_1"));
    }

    [Fact]
    public async Task RetranscribeCancellationLeavesOriginalIntactAndDoesNotAcceptACandidate()
    {
        using var harness = Harness();
        harness.Asr.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new TranscriptionResult(CandidateTranscript);
        };
        using var cts = new CancellationTokenSource();
        var task = harness.Service.RetranscribeAsync("meet_1", cancellationToken: cts.Token);
        await WaitUntil(() => harness.Service.GetPendingCandidate("meet_1") is not null);
        await cts.CancelAsync();
        var result = await task;

        Assert.False(result.Succeeded);
        Assert.False(result.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.Cancelled, result.Outcome);
        Assert.DoesNotContain("success", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Null(harness.Service.GetPendingCandidate("meet_1"));
    }

    [Fact]
    public async Task ConcurrentSameMeetingRetranscriptionsKeepTheAdmittedCandidateAndCleanUp()
    {
        using var directory = new TestDirectory();
        var audio = directory.File("microphone.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var store = new MemoryTranscriptMeetingStore();
        store.Save(Meeting(audio));

        var secondAsrStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAsr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new FirstCallWaitsForSecondAsrTimeProvider(secondAsrStarted);
        var asr = new FakeRetranscriptionAsr
        {
            Handler = async (_, token) =>
            {
                secondAsrStarted.TrySetResult(true);
                await releaseAsr.Task.WaitAsync(token);
                return new TranscriptionResult(CandidateTranscript);
            }
        };
        var scratchDirectory = Path.Combine(directory.Path, "scratch");
        using var service = CreateService(store, scratchDirectory, asr, time: time);

        // Hold the first caller after all preflight checks, then let the second caller acquire
        // the flight and begin ASR. This deterministically exercises the old save-before-admit
        // interleaving: the first caller must not overwrite or delete the admitted scratch.
        var firstTask = Task.Run(() => service.RetranscribeAsync("meet_1"));
        await time.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondTask = Task.Run(() => service.RetranscribeAsync("meet_1"));
        await secondAsrStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = await firstTask;
        Assert.Equal(RetranscriptionOutcome.Busy, first.Outcome);
        Assert.False(first.AppliedToMeeting);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);

        releaseAsr.TrySetResult(true);
        var second = await secondTask;
        Assert.Equal(RetranscriptionOutcome.CandidateReady, second.Outcome);
        Assert.True(second.Succeeded);
        Assert.False(second.AppliedToMeeting);
        Assert.NotNull(second.Candidate);
        Assert.Equal(CandidateTranscript, second.Candidate!.Transcript);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);

        // Exercise a small same-meeting request burst while the ready candidate is pending. The
        // burst must remain fail-closed and must not disturb the winner before explicit acceptance.
        var contenders = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => service.RetranscribeAsync("meet_1"))));
        Assert.All(contenders, result => Assert.Equal(RetranscriptionOutcome.Busy, result.Outcome));
        var pending = service.GetPendingCandidate("meet_1");
        Assert.NotNull(pending);
        Assert.Equal(second.Candidate.CandidateId, pending!.CandidateId);
        Assert.Equal(CandidateTranscript, pending.Transcript);

        var accepted = service.AcceptCandidate("meet_1", pending.CandidateId);
        Assert.True(accepted.AppliedToMeeting);
        Assert.Equal(CandidateTranscript, store.Find("meet_1")!.Transcript);
        Assert.Empty(Directory.GetFiles(scratchDirectory, "*-candidate.json*"));
        service.DeleteMeetingScratch("meet_1");
        Assert.Empty(Directory.GetFiles(scratchDirectory));
    }

    [Fact]
    public async Task RetranscribeSuccessProducesCandidateAcceptReplacesRejectKeepsOriginal()
    {
        using var harness = Harness();
        var ready = await harness.Service.RetranscribeAsync("meet_1");

        Assert.True(ready.Succeeded);
        Assert.False(ready.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.CandidateReady, ready.Outcome);
        Assert.Contains("Accept", ready.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Equal(CandidateTranscript, ready.Candidate!.Transcript);
        Assert.Equal(ManualNotes, harness.Store.Find("meet_1")!.ManualNotes);

        var rejected = harness.Service.RejectCandidate("meet_1", ready.Candidate!.CandidateId);
        Assert.True(rejected.Succeeded);
        Assert.False(rejected.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.Rejected, rejected.Outcome);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Null(harness.Service.GetPendingCandidate("meet_1"));

        var readyAgain = await harness.Service.RetranscribeAsync("meet_1");
        var accepted = harness.Service.AcceptCandidate("meet_1", readyAgain.Candidate!.CandidateId);
        Assert.True(accepted.Succeeded);
        Assert.True(accepted.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.Accepted, accepted.Outcome);
        Assert.Equal(CandidateTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Equal(ManualNotes, harness.Store.Find("meet_1")!.ManualNotes);
        Assert.Equal(GeneratedNotes, harness.Store.Find("meet_1")!.Summary);
        Assert.True(accepted.RequestsResummary);
        Assert.True(harness.Service.IsGeneratedNotesStale("meet_1"));
    }

    [Fact]
    public async Task AsrExceptionAndTimeoutDoNotDeleteOriginal()
    {
        using var failHarness = Harness();
        failHarness.Asr.Handler = (_, _) => throw new InvalidOperationException("native recognizer crashed");
        var failed = await failHarness.Service.RetranscribeAsync("meet_1");
        Assert.False(failed.Succeeded);
        Assert.False(failed.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.AsrFailed, failed.Outcome);
        Assert.Equal(OriginalTranscript, failHarness.Store.Find("meet_1")!.Transcript);
        Assert.Null(failHarness.Service.GetPendingCandidate("meet_1"));

        using var timeoutHarness = Harness(timeout: TimeSpan.FromMilliseconds(40));
        timeoutHarness.Asr.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new TranscriptionResult(CandidateTranscript);
        };
        var timedOut = await timeoutHarness.Service.RetranscribeAsync("meet_1");
        Assert.False(timedOut.Succeeded);
        Assert.False(timedOut.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.TimedOut, timedOut.Outcome);
        Assert.Contains("timed out", timedOut.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, timeoutHarness.Store.Find("meet_1")!.Transcript);
        Assert.Null(timeoutHarness.Service.GetPendingCandidate("meet_1"));
    }

    [Fact]
    public async Task CorruptAudioFailsClosedWithoutFabricatingATranscript()
    {
        using var emptyFile = Harness(audioBytes: []);
        var empty = await emptyFile.Service.RetranscribeAsync("meet_1");
        Assert.Equal(RetranscriptionOutcome.CorruptAudio, empty.Outcome);
        Assert.False(empty.Succeeded);
        Assert.Equal(OriginalTranscript, emptyFile.Store.Find("meet_1")!.Transcript);
        Assert.Equal(0, emptyFile.Asr.Calls);

        using var decodeFail = Harness();
        decodeFail.Asr.Handler = (_, _) => throw new InvalidDataException("not a wave file");
        var corrupt = await decodeFail.Service.RetranscribeAsync("meet_1");
        Assert.Equal(RetranscriptionOutcome.CorruptAudio, corrupt.Outcome);
        Assert.Equal(OriginalTranscript, decodeFail.Store.Find("meet_1")!.Transcript);
        Assert.Null(decodeFail.Service.GetPendingCandidate("meet_1"));
    }

    [Fact]
    public async Task EmptyAsrResultIsNotACompletedRetranscribe()
    {
        using var harness = Harness();
        harness.Asr.Handler = (_, _) => Task.FromResult(new TranscriptionResult("   "));
        var result = await harness.Service.RetranscribeAsync("meet_1");
        Assert.False(result.Succeeded);
        Assert.Equal(RetranscriptionOutcome.EmptyTranscript, result.Outcome);
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Null(harness.Service.GetPendingCandidate("meet_1"));
    }

    [Fact]
    public async Task TitleOwnershipRemainsUserOwnedAndAliasesArePreserved()
    {
        using var harness = Harness();
        var ready = await harness.Service.RetranscribeAsync("meet_1");
        var accepted = harness.Service.AcceptCandidate("meet_1", ready.Candidate!.CandidateId);
        var meeting = harness.Store.Find("meet_1")!;

        Assert.True(meeting.TitleIsManual);
        Assert.Equal(ManualTitle, meeting.Title);
        Assert.True(accepted.TitleRemainsUserOwned);
        Assert.Equal("Priya", meeting.SpeakerAliases["Speaker 1"]);
        Assert.Equal(ManualNotes, meeting.ManualNotes);

        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        var saved = harness.Service.SaveTranscriptEdit(begin.Session!, EditedTranscript);
        var afterEdit = harness.Store.Find("meet_1")!;
        Assert.True(afterEdit.TitleIsManual);
        Assert.Equal(ManualTitle, afterEdit.Title);
        Assert.Equal("Priya", afterEdit.SpeakerAliases["Speaker 1"]);
        Assert.Equal(ManualNotes, afterEdit.ManualNotes);
        Assert.True(saved.RequestsResummary);
    }

    [Fact]
    public async Task DeletionOfMeetingCleansCandidateScratch()
    {
        using var harness = Harness();
        var ready = await harness.Service.RetranscribeAsync("meet_1");
        Assert.NotNull(harness.Service.GetPendingCandidate("meet_1"));
        Assert.True(ready.Succeeded);

        harness.Service.DeleteMeetingScratch("meet_1");
        Assert.Null(harness.Service.GetPendingCandidate("meet_1"));
        Assert.False(harness.Service.IsGeneratedNotesStale("meet_1"));
        Assert.Equal(OriginalTranscript, harness.Store.Find("meet_1")!.Transcript);
        Assert.Empty(Directory.GetFiles(harness.ScratchDirectory));
    }

    [Fact]
    public async Task CrashRecoveryAbandonsInFlightCandidateAndDoesNotFabricateCompletion()
    {
        using var directory = new TestDirectory();
        var audio = directory.File("microphone.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var store = new MemoryTranscriptMeetingStore();
        store.Save(Meeting(audio));
        var gate = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asr = new FakeRetranscriptionAsr
        {
            Handler = async (_, token) =>
            {
                using (token.Register(() => gate.TrySetCanceled(token)))
                {
                    return await gate.Task;
                }
            }
        };
        var scratch = new RetranscriptionCandidateStore(Path.Combine(directory.Path, "scratch"));
        using var live = CreateService(store, scratch.RootDirectory, asr);
        var liveTask = live.RetranscribeAsync("meet_1");
        await WaitUntil(() => scratch.TryLoad("meet_1")?.Status == nameof(RetranscriptionCandidateStatus.InFlight));

        using var recovered = CreateService(store, scratch.RootDirectory, new FakeRetranscriptionAsr());
        var recovery = recovered.RecoverInFlight("meet_1");
        Assert.False(recovery.Succeeded);
        Assert.False(recovery.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.RecoveredInFlight, recovery.Outcome);
        Assert.Contains("not completed", recovery.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);

        gate.TrySetResult(new TranscriptionResult(CandidateTranscript));
        var liveResult = await liveTask;
        Assert.False(liveResult.AppliedToMeeting);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);
        Assert.Null(recovered.GetPendingCandidate("meet_1"));
        Assert.NotEqual(RetranscriptionOutcome.CandidateReady, liveResult.Outcome);
        Assert.NotEqual(RetranscriptionOutcome.Accepted, liveResult.Outcome);
    }

    [Fact]
    public async Task ReadyCandidateSurvivesRestartUntilAcceptedOrRejected()
    {
        using var directory = new TestDirectory();
        var audio = directory.File("microphone.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var store = new MemoryTranscriptMeetingStore();
        store.Save(Meeting(audio));
        var scratchDir = Path.Combine(directory.Path, "scratch");
        using (var first = CreateService(store, scratchDir, new FakeRetranscriptionAsr
        {
            Handler = (_, _) => Task.FromResult(new TranscriptionResult(CandidateTranscript))
        }))
        {
            var ready = await first.RetranscribeAsync("meet_1");
            Assert.Equal(RetranscriptionOutcome.CandidateReady, ready.Outcome);
        }

        using var restarted = CreateService(store, scratchDir, new FakeRetranscriptionAsr());
        var recovered = restarted.RecoverInFlight("meet_1");
        Assert.True(recovered.Succeeded);
        Assert.False(recovered.AppliedToMeeting);
        Assert.Equal(RetranscriptionOutcome.CandidateReady, recovered.Outcome);
        Assert.Equal(CandidateTranscript, recovered.Candidate!.Transcript);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);

        var accepted = restarted.AcceptCandidate("meet_1", recovered.Candidate!.CandidateId);
        Assert.True(accepted.AppliedToMeeting);
        Assert.Equal(CandidateTranscript, store.Find("meet_1")!.Transcript);
    }

    [Fact]
    public async Task MissingEngineDoesNotFabricateACompletedRetranscribe()
    {
        using var directory = new TestDirectory();
        var audio = directory.File("microphone.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var store = new MemoryTranscriptMeetingStore();
        store.Save(Meeting(audio));
        using var service = CreateService(store, Path.Combine(directory.Path, "scratch"), asr: null);
        var result = await service.RetranscribeAsync("meet_1");
        Assert.False(result.Succeeded);
        Assert.Equal(RetranscriptionOutcome.EngineMissing, result.Outcome);
        Assert.Equal(OriginalTranscript, store.Find("meet_1")!.Transcript);
    }

    [Fact]
    public void DiagnosticsNeverContainTranscriptTitleOrRawAudioPath()
    {
        using var harness = Harness();
        var begin = harness.Service.BeginTranscriptEdit("meet_1");
        harness.Service.SaveTranscriptEdit(begin.Session!, EditedTranscript);
        AssertNoLeak(harness.Log, harness.AudioPath);
    }

    [Fact]
    public async Task RetranscribeDiagnosticsNeverContainTranscriptTitleOrRawAudioPath()
    {
        using var harness = Harness();
        var ready = await harness.Service.RetranscribeAsync("meet_1");
        harness.Service.AcceptCandidate("meet_1", ready.Candidate!.CandidateId);
        AssertNoLeak(harness.Log, harness.AudioPath);
    }

    [Fact]
    public async Task AppDataStoreRoundTripKeepsOriginalUntilCandidateIsAccepted()
    {
        using var directory = new TestDirectory();
        var audio = directory.File("microphone.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        var data = new AppDataStore(Path.Combine(directory.Path, "data"));
        data.SaveMeetings([Meeting(audio)]);
        var store = new AppDataBackedTranscriptStore(data);
        using var service = CreateService(store, Path.Combine(directory.Path, "scratch"), new FakeRetranscriptionAsr());
        var ready = await service.RetranscribeAsync("meet_1");
        Assert.Equal(OriginalTranscript, data.LoadMeetings().Single().Transcript);
        service.AcceptCandidate("meet_1", ready.Candidate!.CandidateId);
        var loaded = data.LoadMeetings().Single();
        Assert.Equal(CandidateTranscript, loaded.Transcript);
        Assert.Equal(ManualNotes, loaded.ManualNotes);
        Assert.True(loaded.TitleIsManual);
        Assert.Equal("Priya", loaded.SpeakerAliases["Speaker 1"]);
    }

    private static void AssertNoLeak(CollectingDiagnostics log, string? audioPath)
    {
        Assert.NotEmpty(log.Messages);
        foreach (var message in log.Messages)
        {
            Assert.DoesNotContain(OriginalTranscript, message, StringComparison.Ordinal);
            Assert.DoesNotContain(EditedTranscript, message, StringComparison.Ordinal);
            Assert.DoesNotContain(CandidateTranscript, message, StringComparison.Ordinal);
            Assert.DoesNotContain(ManualTitle, message, StringComparison.Ordinal);
            Assert.DoesNotContain(ManualNotes, message, StringComparison.Ordinal);
            Assert.DoesNotContain(GeneratedNotes, message, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                Assert.DoesNotContain(audioPath, message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static HarnessScope Harness(
        string? audioPath = "provided",
        byte[]? audioBytes = null,
        TimeSpan? timeout = null)
    {
        var directory = new TestDirectory();
        string? path = null;
        if (audioPath is not null)
        {
            path = directory.File("microphone.wav");
            File.WriteAllBytes(path, audioBytes ?? [1, 2, 3, 4, 5]);
        }

        var store = new MemoryTranscriptMeetingStore();
        store.Save(Meeting(path));
        var asr = new FakeRetranscriptionAsr
        {
            Handler = (_, _) => Task.FromResult(new TranscriptionResult(CandidateTranscript))
        };
        var log = new CollectingDiagnostics();
        var service = CreateService(store, Path.Combine(directory.Path, "scratch"), asr, log, timeout);
        return new HarnessScope(directory, store, service, asr, log, path, Path.Combine(directory.Path, "scratch"));
    }

    private static TranscriptEditService CreateService(
        ITranscriptMeetingStore store,
        string scratchDirectory,
        IMeetingRetranscriptionAsr? asr = null,
        ITranscriptEditDiagnostics? log = null,
        TimeSpan? timeout = null,
        TimeProvider? time = null) =>
        new(
            store,
            new RetranscriptionCandidateStore(scratchDirectory),
            asr,
            log,
            time ?? TimeProvider.System,
            timeout);

    private static PersistedMeeting Meeting(string? audioPath = null) => new()
    {
        SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
        Id = "meet_1",
        Title = ManualTitle,
        TitleIsManual = true,
        CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0),
        Transcript = OriginalTranscript,
        Summary = GeneratedNotes,
        ManualNotes = ManualNotes,
        MicrophoneAudioPath = audioPath,
        SourcePath = audioPath ?? "",
        SpeakerAliases = new Dictionary<string, string> { ["Speaker 1"] = "Priya" },
        TemplateName = "Standard Meeting Notes"
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class HarnessScope : IDisposable
    {
        private readonly TestDirectory _directory;

        public HarnessScope(
            TestDirectory directory,
            MemoryTranscriptMeetingStore store,
            TranscriptEditService service,
            FakeRetranscriptionAsr asr,
            CollectingDiagnostics log,
            string? audioPath,
            string scratchDirectory)
        {
            _directory = directory;
            Store = store;
            Service = service;
            Asr = asr;
            Log = log;
            AudioPath = audioPath;
            ScratchDirectory = scratchDirectory;
        }

        public MemoryTranscriptMeetingStore Store { get; }
        public TranscriptEditService Service { get; }
        public FakeRetranscriptionAsr Asr { get; }
        public CollectingDiagnostics Log { get; }
        public string? AudioPath { get; }
        public string ScratchDirectory { get; }

        public void Dispose()
        {
            Service.Dispose();
            _directory.Dispose();
        }
    }

    private sealed class MemoryTranscriptMeetingStore : ITranscriptMeetingStore
    {
        private readonly Dictionary<string, PersistedMeeting> _meetings = new(StringComparer.Ordinal);

        public PersistedMeeting? Find(string meetingId) =>
            _meetings.TryGetValue(meetingId, out var meeting)
                ? meeting with { SpeakerAliases = new Dictionary<string, string>(meeting.SpeakerAliases) }
                : null;

        public void Save(PersistedMeeting meeting) =>
            _meetings[meeting.Id] = meeting with
            {
                SpeakerAliases = new Dictionary<string, string>(meeting.SpeakerAliases)
            };
    }

    private sealed class FailingTranscriptStore : ITranscriptMeetingStore
    {
        private readonly MemoryTranscriptMeetingStore _inner;

        public FailingTranscriptStore(MemoryTranscriptMeetingStore inner) => _inner = inner;

        public string? FailWhenTranscriptEquals { get; set; }

        public PersistedMeeting? Find(string meetingId) => _inner.Find(meetingId);

        public void Save(PersistedMeeting meeting)
        {
            if (FailWhenTranscriptEquals is not null &&
                string.Equals(meeting.Transcript, FailWhenTranscriptEquals, StringComparison.Ordinal))
            {
                _inner.Save(meeting with { Transcript = "PARTIAL-WRITE" });
                throw new IOException("disk full");
            }

            _inner.Save(meeting);
        }
    }

    private sealed class AppDataBackedTranscriptStore : ITranscriptMeetingStore
    {
        private readonly AppDataStore _store;

        public AppDataBackedTranscriptStore(AppDataStore store) => _store = store;

        public PersistedMeeting? Find(string meetingId) =>
            _store.LoadMeetings().FirstOrDefault(meeting => meeting.Id == meetingId);

        public void Save(PersistedMeeting meeting)
        {
            var meetings = _store.LoadMeetings().ToList();
            var index = meetings.FindIndex(item => item.Id == meeting.Id);
            if (index < 0)
            {
                meetings.Add(meeting);
            }
            else
            {
                meetings[index] = meeting;
            }

            _store.SaveMeetings(meetings);
        }
    }

    private sealed class FakeRetranscriptionAsr : IMeetingRetranscriptionAsr
    {
        public Func<string, CancellationToken, Task<TranscriptionResult>>? Handler { get; set; }
        public int Calls { get; private set; }

        public Task<TranscriptionResult> TranscribeOwnedAudioAsync(
            string filePath,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Handler is null
                ? Task.FromResult(new TranscriptionResult(CandidateTranscript))
                : Handler(filePath, cancellationToken);
        }
    }

    private sealed class FirstCallWaitsForSecondAsrTimeProvider(
        TaskCompletionSource<bool> secondAsrStarted) : TimeProvider
    {
        private int _calls;

        public TaskCompletionSource<bool> FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult(true);
                if (!secondAsrStarted.Task.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The second retranscription did not reach ASR.");
                }
            }

            return new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        }
    }

    private sealed class CollectingDiagnostics : ITranscriptEditDiagnostics
    {
        public List<string> Messages { get; } = [];

        public void Info(string message) => Messages.Add(message);

        public void Error(string message, Exception? exception = null) =>
            Messages.Add(exception is null ? message : $"{message} {exception.GetType().Name}");
    }
}
