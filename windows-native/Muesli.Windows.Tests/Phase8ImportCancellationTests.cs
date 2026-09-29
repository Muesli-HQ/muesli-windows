using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// A long media import must be legible while it runs and interruptible while it runs. These cover
/// the progress contract, the cancellation contract, and the promise that cancelling an import
/// never leaves a half-written meeting behind.
/// </summary>
public sealed class Phase8ImportCancellationTests
{
    // ---- Progress reporting ------------------------------------------------

    [Fact]
    public void OverallProgressIsMonotonicAcrossTheWholePipeline()
    {
        // The bar must never travel backwards as the import advances, or it reads as a bug.
        MeetingImportStage[] order =
        [
            MeetingImportStage.Preparing,
            MeetingImportStage.Decoding,
            MeetingImportStage.Transcribing,
            MeetingImportStage.CleaningUp,
            MeetingImportStage.GeneratingNotes
        ];

        var last = -1.0;
        foreach (var stage in order)
        {
            foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
            {
                var percent = MeetingImportProgressMapper.Map(stage, fraction).Percent;
                Assert.True(
                    percent >= last,
                    $"{stage} at {fraction} reported {percent}, which is behind the previous {last}.");
                last = percent;
            }
        }

        Assert.Equal(100, last);
    }

    [Fact]
    public void StagesWithNoMeasurableFractionAreIndeterminateRatherThanFabricated()
    {
        // Parakeet decodes a whole file in one native call. Showing a fake percentage that never
        // moves is worse than admitting the stage cannot be measured.
        var progress = MeetingImportProgressMapper.Map(MeetingImportStage.Transcribing, fraction: null);

        Assert.True(progress.IsIndeterminate);
        Assert.DoesNotContain("%", progress.Label, StringComparison.Ordinal);
        // It still parks at the floor of its band so the bar does not jump back to zero.
        Assert.Equal(20, progress.Percent);
    }

    [Fact]
    public void MeasuredStagesReportBothAPercentAndAReadableLabel()
    {
        var progress = MeetingImportProgressMapper.Map(MeetingImportStage.Decoding, 0.5);

        Assert.False(progress.IsIndeterminate);
        Assert.Equal(10, progress.Percent);
        Assert.Contains("Decoding audio", progress.Label, StringComparison.Ordinal);
        Assert.Contains("50%", progress.Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void OutOfRangeFractionsCannotPushTheBarOutsideItsBand(double fraction)
    {
        var progress = MeetingImportProgressMapper.Map(MeetingImportStage.Transcribing, fraction);

        Assert.InRange(progress.Percent, 20, 80);
        // A NaN reaching ProgressBar.Value corrupts the control, so it must never survive the map.
        Assert.False(double.IsNaN(progress.Percent));
    }

    [Fact]
    public void ABrokenMeasurementDegradesToIndeterminateRatherThanPoisoningTheBar()
    {
        // A zero-length source or a bad duration estimate can divide to NaN. Math.Clamp passes NaN
        // through, so the guard has to be explicit at both layers.
        Assert.Null(TranscriptionProgress.Decoding(double.NaN).Fraction);
        Assert.Null(TranscriptionProgress.Transcribing(0.0 / 0.0).Fraction);

        var mapped = MeetingImportProgressMapper.Map(MeetingImportStage.Decoding, double.NaN);
        Assert.True(mapped.IsIndeterminate);
        Assert.Equal(0, mapped.Percent);
    }

    [Fact]
    public void CancellingKeepsTheBarWhereItWasInsteadOfResetting()
    {
        var progress = MeetingImportProgressMapper.Cancelling(42);

        Assert.Equal(42, progress.Percent);
        Assert.True(progress.IsIndeterminate);
        Assert.Contains("Cancel", progress.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranscriptionStagesMapOntoImportStages()
    {
        Assert.Equal(MeetingImportStage.Decoding, MeetingImportProgressMapper.From(TranscriptionStage.Decoding));
        Assert.Equal(MeetingImportStage.Transcribing, MeetingImportProgressMapper.From(TranscriptionStage.Transcribing));
    }

    [Fact]
    public void ProgressFactoriesClampAndPreserveTheUnmeasuredCase()
    {
        Assert.Equal(1, TranscriptionProgress.Decoding(4.2).Fraction);
        Assert.Equal(0, TranscriptionProgress.Transcribing(-1).Fraction);
        Assert.Null(TranscriptionProgress.TranscribingUnmeasured().Fraction);
        Assert.Equal(TranscriptionStage.Transcribing, TranscriptionProgress.TranscribingUnmeasured().Stage);
    }

    // ---- Progress throttling ----------------------------------------------
    //
    // Regression cover for a defect found by running the app, not by these tests: the decode loop
    // reported once per decoded buffer, and Progress<T> delivers each as a Normal-priority WPF
    // dispatcher callback. Normal outranks Input, so the flood starved mouse input and the Cancel
    // button was unclickable for the entire decode of an hour-long file.

    [Fact]
    public void DecodingAnHourOfAudioCannotFloodTheDispatcher()
    {
        var reports = new List<TranscriptionProgress>();
        var throttle = new ThrottledTranscriptionProgress(new SynchronousProgress<TranscriptionProgress>(reports.Add));

        // An hour at 32 kB/s decodes in roughly this many buffer reads.
        const int reads = 60_000;
        for (var i = 1; i <= reads; i++)
        {
            throttle.Report(TranscriptionStage.Decoding, i / (double)reads);
        }

        Assert.True(reports.Count <= 101, $"{reports.Count} callbacks would starve WPF input priority.");
        Assert.Equal(1, reports[^1].Fraction);
    }

    [Fact]
    public void ThrottlingStillDeliversEveryVisibleStep()
    {
        var reports = new List<TranscriptionProgress>();
        var throttle = new ThrottledTranscriptionProgress(new SynchronousProgress<TranscriptionProgress>(reports.Add));

        for (var i = 0; i <= 1000; i++)
        {
            throttle.Report(TranscriptionStage.Decoding, i / 1000.0);
        }

        // Every whole percent survives, so the bar animates rather than jumping.
        Assert.Equal(101, reports.Count);
        Assert.Equal(0, reports[0].Fraction);
        Assert.Equal(1, reports[^1].Fraction);
    }

    [Fact]
    public void AStageChangeIsAlwaysForwardedEvenAtTheSamePercent()
    {
        var reports = new List<TranscriptionProgress>();
        var throttle = new ThrottledTranscriptionProgress(new SynchronousProgress<TranscriptionProgress>(reports.Add));

        throttle.Report(TranscriptionStage.Decoding, 0.5);
        throttle.Report(TranscriptionStage.Decoding, 0.5);
        throttle.Report(TranscriptionStage.Transcribing, 0.5);

        Assert.Equal(2, reports.Count);
        Assert.Equal(TranscriptionStage.Decoding, reports[0].Stage);
        Assert.Equal(TranscriptionStage.Transcribing, reports[1].Stage);
    }

    [Fact]
    public void ThrottleDropsBrokenMeasurementsAndToleratesNoListener()
    {
        var reports = new List<TranscriptionProgress>();
        var throttle = new ThrottledTranscriptionProgress(new SynchronousProgress<TranscriptionProgress>(reports.Add));
        throttle.Report(TranscriptionStage.Decoding, double.NaN);
        Assert.Empty(reports);

        var silent = new ThrottledTranscriptionProgress(null);
        Assert.True(silent.IsNoOp);
        silent.Report(TranscriptionStage.Decoding, 0.5);      // must not throw
        silent.ReportUnmeasured(TranscriptionStage.Transcribing);
    }

    // ---- Cancellation ------------------------------------------------------

    [Fact]
    public async Task TranscriptionClientPassesProgressAndCancellationToTheSession()
    {
        var session = new RecordingSession();
        var client = new NativeTranscriptionClient(
            TranscriptionModelCatalog.Models[0],
            new StubSessionFactory(session),
            isReady: _ => true);
        var reports = new List<TranscriptionProgress>();
        using var cancellation = new CancellationTokenSource();

        await client.TranscribeFileAsync(
            "Imported",
            "meeting.mp4",
            new SynchronousProgress<TranscriptionProgress>(reports.Add),
            cancellation.Token);

        Assert.True(session.ReceivedProgress);
        Assert.True(session.ReceivedToken.CanBeCanceled);
        Assert.Equal(cancellation.Token, session.ReceivedToken);
    }

    [Fact]
    public async Task AnAlreadyCancelledImportNeverReachesTheRecognizer()
    {
        var session = new RecordingSession();
        var client = new NativeTranscriptionClient(
            TranscriptionModelCatalog.Models[0],
            new StubSessionFactory(session),
            isReady: _ => true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TranscribeFileAsync("Imported", "meeting.mp4", progress: null, cancellation.Token));

        Assert.False(session.Transcribed);
    }

    [Fact]
    public async Task WaitingBehindAnotherTranscriptionIsItselfCancellable()
    {
        // The gate wait is part of the delay the user sees, so Cancel has to work there too.
        var blocked = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RecordingSession { Pending = blocked };
        var client = new NativeTranscriptionClient(
            TranscriptionModelCatalog.Models[0],
            new StubSessionFactory(session),
            isReady: _ => true);

        var first = client.TranscribeFileAsync("First", "a.mp4");
        await session.Entered.Task;

        using var cancellation = new CancellationTokenSource();
        var queued = client.TranscribeFileAsync("Second", "b.mp4", progress: null, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        blocked.SetResult(new TranscriptionResult("first"));
        await first;
    }

    [Fact]
    public async Task CleanupStageHonoursCancellationBeforeItRewritesTheTranscript()
    {
        var pipeline = new TranscriptionPipelineService(new NativeTextCleanupService());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.PrepareImportedTranscriptAsync(
                "hello world",
                enableCleanup: false,
                [],
                cancellation.Token));
    }

    [Fact]
    public async Task CleanupCompletesNormallyWhenNotCancelled()
    {
        var pipeline = new TranscriptionPipelineService(new NativeTextCleanupService());

        var transcript = await pipeline.PrepareImportedTranscriptAsync(
            "hello world",
            enableCleanup: false,
            [],
            CancellationToken.None);

        Assert.Equal("hello world", transcript);
    }

    // ---- Test doubles ------------------------------------------------------

    /// <summary>Reports inline so assertions do not depend on a synchronization context.</summary>
    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class StubSessionFactory(RecordingSession session) : ITranscriptionModelSessionFactory
    {
        public ITranscriptionModelSession Create(TranscriptionModelDefinition model) => session;
    }

    private sealed class RecordingSession : ITranscriptionModelSession
    {
        public bool Transcribed { get; private set; }
        public bool ReceivedProgress { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public TaskCompletionSource<TranscriptionResult>? Pending { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelOperationResult> InitializeAsync() =>
            Task.FromResult(new ModelOperationResult("ready", "stub"));

        public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
            Task.FromResult(new TranscriptionResult("stub"));

        public Task<TranscriptionResult> TranscribeFileAsync(
            string title,
            string filePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Transcribed = true;
            ReceivedProgress = progress is not null;
            ReceivedToken = cancellationToken;
            progress?.Report(TranscriptionProgress.Decoding(0.25));
            Entered.TrySetResult();
            return Pending?.Task ?? Task.FromResult(new TranscriptionResult("stub"));
        }

        public void Dispose() { }
    }
}
