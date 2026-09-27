using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingFinalizationPipelineTests
{
    [Fact]
    public async Task EmptyTracksFailBeforeAsrAndDoNotDeleteAudio()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var persistence = new MeetingPersistenceBoundary(store);
        var journal = persistence.Create("meet_empty_tracks", "Empty", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var pipeline = new MeetingFinalizationPipeline(CreateTranscriptionClient(), persistence);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.FinalizeAsync(
            new MeetingFinalizationRequest(
                journal,
                journal.Title,
                Recovered: false,
                DateTime.UnixEpoch,
                [],
                null,
                CancellationToken.None)));

        Assert.Contains("No valid meeting track", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(persistence.GetSessionDirectory(journal.SessionId)));
    }

    [Fact]
    public void CoordinatorWarningCleanupDelegatesToThePipeline()
    {
        var transcript = "[00:00:01] Alice: hello";
        var warnings = new[]
        {
            "Speaker diarization failed; transcript used fallback speaker labels.",
            "System transcript was empty even though system audio was captured.",
            "ASR engine: leftover"
        };

        var fromCoordinator = MeetingRecordingCoordinator.CleanupHealthWarnings(warnings, transcript, true);
        var fromPipeline = MeetingFinalizationPipeline.CleanupHealthWarnings(warnings, transcript, true);

        Assert.Equal(fromPipeline, fromCoordinator);
        Assert.DoesNotContain(fromCoordinator, warning => warning.Contains("diarization failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fromCoordinator, warning => warning.Contains("System transcript was empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnobservedDiarizationIsAwaitedBeforeCleanupReturns()
    {
        var observed = 0;
        var diarization = Task.Run(async () =>
        {
            await Task.Delay(25);
            Interlocked.Increment(ref observed);
        });

        await MeetingFinalizationPipeline.AwaitDiarizationBeforeCleanupAsync(diarization, alreadyObserved: false);
        Assert.Equal(1, Volatile.Read(ref observed));
        Assert.True(diarization.IsCompleted);
    }

    [Fact]
    public async Task EmptyTranscriptMarksTheJournalFailedAndKeepsWorkingAudio()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var persistence = new MeetingPersistenceBoundary(store);
        var journal = persistence.Create("meet_silent", "Silent", DateTimeOffset.UnixEpoch, "mic", "parakeet-v3", true, null);
        var source = directory.File("mic.wav");
        WriteSilence(source);
        journal = persistence.AppendPart(journal, MeetingAudioChannel.Microphone, source);
        var pipeline = new MeetingFinalizationPipeline(CreateTranscriptionClient(), persistence);

        var outcome = await pipeline.FinalizeAsync(new MeetingFinalizationRequest(
            journal,
            journal.Title,
            Recovered: false,
            DateTime.UnixEpoch,
            [],
            null,
            CancellationToken.None));

        Assert.Equal(MeetingSessionState.Failed, outcome.TerminalState);
        Assert.Equal(MeetingSessionState.Failed, outcome.Journal.State);
        Assert.Contains(MeetingFinalizationPipeline.TranscriptMissingWarning, outcome.Result.HealthWarnings ?? []);
        Assert.True(Directory.Exists(persistence.GetSessionDirectory(journal.SessionId)));
        Assert.NotNull(outcome.Result.MicAudioPath);
        Assert.True(File.Exists(outcome.Result.MicAudioPath));
    }

    private static NativeTranscriptionClient CreateTranscriptionClient() =>
        new(
            TranscriptionModelCatalog.GetRequired(TranscriptionModelCatalog.DefaultModelId),
            new StubTranscriptionSessionFactory(),
            isReady: _ => true);

    private static void WriteSilence(string path)
    {
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(16000, 16, 1));
        var bytes = new byte[6400];
        writer.Write(bytes, 0, bytes.Length);
    }

    private sealed class StubTranscriptionSessionFactory : ITranscriptionModelSessionFactory
    {
        public ITranscriptionModelSession Create(TranscriptionModelDefinition model) => new StubTranscriptionSession();
    }

    private sealed class StubTranscriptionSession : ITranscriptionModelSession
    {
        public Task<ModelOperationResult> InitializeAsync() =>
            Task.FromResult(new ModelOperationResult("stub"));

        public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
            Task.FromResult(new TranscriptionResult(""));

        public Task<TranscriptionResult> TranscribeFileAsync(
            string title,
            string filePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TranscriptionResult(""));

        public void Dispose()
        {
        }
    }
}
