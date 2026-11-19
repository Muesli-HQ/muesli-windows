using Muesli.Windows.Services;
using NAudio.Wave;

namespace Muesli.Windows.Tests;

public sealed class DictationRollingTranscriberTests
{
    [Theory]
    [InlineData(NativeAsrModelKind.CohereTranscribe, "en", 500, true, true)]
    [InlineData(NativeAsrModelKind.CohereTranscribe, "en", 4096, true, false)]
    [InlineData(NativeAsrModelKind.CohereTranscribe, "ja", 500, true, false)]
    [InlineData(NativeAsrModelKind.CohereTranscribe, "en", 500, false, false)]
    [InlineData(NativeAsrModelKind.Parakeet, "en", 500, true, false)]
    public void MemoryFallbackPreservesLanguageAndRequiresAnInstalledModel(
        NativeAsrModelKind kind, string language, int freeMiB, bool installed, bool expected)
    {
        Assert.Equal(expected, DictationCoordinator.ShouldUseParakeetFallback(
            kind, language, (ulong)freeMiB * 1048576, installed));
    }

    [Fact]
    public void PerformanceBoostRaisesPriorityAndRestoresItWhenTheLastScopeEnds()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var before = process.PriorityClass;
        var outer = TranscriptionPerformance.Boost();
        var inner = TranscriptionPerformance.Boost();
        inner.Dispose();
        process.Refresh();
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.AboveNormal, process.PriorityClass);
        outer.Dispose();
        process.Refresh();
        Assert.Equal(before, process.PriorityClass);
    }

    private const int Second = 16000;

    /// <summary>Reports a stretch of speech once the audio fed so far passes the given point.</summary>
    private sealed class ScriptedVad(params (long SpeechEnd, long ReportedAt)[] script) : ILiveVad
    {
        private long _fed;
        private int _next;
        public int Resets { get; private set; }

        public void Reset(LiveTranscriptChannel channel) => Resets++;

        public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
        {
            _fed += samples.Length;
            var ready = new List<(long, long)>();
            while (_next < script.Length && _fed >= script[_next].ReportedAt)
                ready.Add((0, script[_next++].SpeechEnd));
            return ready;
        }

        public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel) => [];
        public void Dispose() { }
    }

    private static void FeedSeconds(DictationRollingTranscriber rolling, double from, double seconds)
    {
        // Capture delivers 10 ms packets.
        var start = (long)(from * Second);
        var end = start + (long)(seconds * Second);
        for (var at = start; at < end; at += 160)
            rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, new float[160], at));
    }

    private static string SilentRecording(TestDirectory directory, int seconds)
    {
        var recording = directory.File("recording.wav");
        using var writer = new WaveFileWriter(recording, new WaveFormat(Second, 16, 1));
        writer.Write(new byte[seconds * Second * 2]);
        return recording;
    }

    private static int DurationMs(byte[] wav)
    {
        using var reader = new WaveFileReader(new MemoryStream(wav));
        return (int)reader.TotalTime.TotalMilliseconds;
    }

    [Fact]
    public async Task ChunkIsCutAtADetectedPauseAndDecodedBeforeStop()
    {
        using var directory = new TestDirectory();
        var recording = SilentRecording(directory, 8);
        var firstDecoded = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fallbackCalls = 0;
        // Speech ends at 6.0 s; the detector reports it at 6.5 s, after the silence that follows.
        var vad = new ScriptedVad((6 * Second, 6 * Second + Second / 2));
        var rolling = new DictationRollingTranscriber(
            wav =>
            {
                var number = Interlocked.Increment(ref calls);
                if (number == 1) firstDecoded.SetResult(DurationMs(wav));
                return Task.FromResult(new TranscriptionResult($"part {number}"));
            },
            _ =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return Task.FromResult(new TranscriptionResult("fallback"));
            },
            vad);

        FeedSeconds(rolling, 0, 7);
        var firstChunkMs = await firstDecoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); // Recognition ran while recording was still active.
        Assert.InRange(firstChunkMs, 6_000, 6_500); // Inside the pause, not at a fixed interval.
        FeedSeconds(rolling, 7, 1);

        var result = await rolling.FinishAsync(recording);

        Assert.Equal("part 1 part 2", result.Text);
        Assert.StartsWith("ASR during capture:", result.Diagnostic);
        Assert.Contains("pauseCuts=1; forcedCuts=0; cuts=pauses", result.Diagnostic);
        Assert.Equal(0, fallbackCalls);
        Assert.Equal(8_000, result.DurationMs);
        Assert.Equal(1, vad.Resets);
    }

    [Fact]
    public async Task APauseTooSoonAfterTheLastCutIsNotCut()
    {
        using var directory = new TestDirectory();
        var recording = SilentRecording(directory, 6);
        var calls = 0;
        // A pause at 2 s would make a chunk shorter than the 5 s minimum.
        var rolling = new DictationRollingTranscriber(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new TranscriptionResult("partial"));
            },
            _ => Task.FromResult(new TranscriptionResult("complete")),
            new ScriptedVad((2 * Second, 2 * Second + Second / 2)));

        FeedSeconds(rolling, 0, 6);
        var result = await rolling.FinishAsync(recording);

        Assert.Equal(0, calls);
        Assert.Equal("complete", result.Text); // Never cut, so the whole recording is decoded once.
        Assert.StartsWith("Rolling fallback: short-recording", result.Diagnostic);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SpeechWithoutAPauseIsCutAtThirtySeconds(bool detectorAvailable)
    {
        using var directory = new TestDirectory();
        var recording = SilentRecording(directory, 31);
        var chunkMs = new List<int>();
        var rolling = new DictationRollingTranscriber(
            wav =>
            {
                lock (chunkMs) chunkMs.Add(DurationMs(wav));
                return Task.FromResult(new TranscriptionResult("part"));
            },
            _ => Task.FromResult(new TranscriptionResult("fallback")),
            detectorAvailable ? new ScriptedVad() : null);

        FeedSeconds(rolling, 0, 31);
        var result = await rolling.FinishAsync(recording);

        Assert.Equal([30_000, 1_000], chunkMs);
        Assert.Equal("part part", result.Text);
        Assert.Contains("pauseCuts=0; forcedCuts=1", result.Diagnostic);
        Assert.Contains(detectorAvailable ? "cuts=pauses" : "cuts=limit-only", result.Diagnostic);
    }

    [Theory]
    [InlineData(20, true)] // The detector heard speech in the chunk that came back empty.
    [InlineData(0, false)] // The empty chunk really was silent: keep the chunked text.
    public async Task AnEmptyChunkWithSpeechFallsBackToTheWholeRecording(int speechSecondsInFirstChunk, bool expectFallback)
    {
        using var directory = new TestDirectory();
        var recording = SilentRecording(directory, 31);
        var calls = 0;
        var script = speechSecondsInFirstChunk > 0
            ? new[] { ((long)speechSecondsInFirstChunk * Second, 30L * Second + Second / 2) }
            : [];
        var rolling = new DictationRollingTranscriber(
            // The 30 s chunk, cut mid-phrase by the length limit, returns nothing.
            _ => Task.FromResult(new TranscriptionResult(Interlocked.Increment(ref calls) == 1 ? "" : "the end")),
            _ => Task.FromResult(new TranscriptionResult("every word")),
            new ScriptedVad(script));

        FeedSeconds(rolling, 0, 31);
        var result = await rolling.FinishAsync(recording);

        Assert.Equal(expectFallback ? "every word" : "the end", result.Text);
        Assert.StartsWith(expectFallback ? "Rolling fallback: empty-chunk" : "ASR during capture:", result.Diagnostic);
    }

    [Fact]
    public async Task WithoutTheDetectorAFullLengthEmptyChunkFallsBack()
    {
        using var directory = new TestDirectory();
        var recording = SilentRecording(directory, 31);
        var calls = 0;
        var rolling = new DictationRollingTranscriber(
            _ => Task.FromResult(new TranscriptionResult(Interlocked.Increment(ref calls) == 1 ? "" : "the end")),
            _ => Task.FromResult(new TranscriptionResult("every word")));

        FeedSeconds(rolling, 0, 31);
        var result = await rolling.FinishAsync(recording);

        Assert.Equal("every word", result.Text);
        Assert.StartsWith("Rolling fallback: empty-chunk", result.Diagnostic);
    }

    [Fact]
    public async Task TheDetectorIsReleasedBeforeAnAbandonedFinishCompletes()
    {
        // Escape during transcription leaves FinishAsync running while a new dictation may
        // start. The detector must be free by then even though the decode has not returned.
        var decodeGate = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rolling = new DictationRollingTranscriber(
            _ => Task.FromResult(new TranscriptionResult("partial")),
            _ => decodeGate.Task,
            new ScriptedVad());

        FeedSeconds(rolling, 0, 2);
        Assert.False(rolling.DetectorReleased.IsCompleted); // Still capturing.

        var finish = rolling.FinishAsync("recording.wav");
        await rolling.DetectorReleased.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(finish.IsCompleted); // The whole-recording decode is still running.

        decodeGate.SetResult(new TranscriptionResult("complete"));
        Assert.Equal("complete", (await finish).Text);
    }

    [Fact]
    public async Task DrainingOrHavingNoDetectorReleasesIt()
    {
        var withoutDetector = new DictationRollingTranscriber(
            _ => Task.FromResult(new TranscriptionResult("")), _ => Task.FromResult(new TranscriptionResult("")));
        Assert.True(withoutDetector.DetectorReleased.IsCompleted);

        var cancelled = new DictationRollingTranscriber(
            _ => Task.FromResult(new TranscriptionResult("")), _ => Task.FromResult(new TranscriptionResult("")),
            new ScriptedVad());
        FeedSeconds(cancelled, 0, 1);
        await cancelled.DrainAsync();
        Assert.True(cancelled.DetectorReleased.IsCompleted);
    }

    [Fact]
    public async Task MissingCapturePacketUsesDurableRecording()
    {
        var rolling = new DictationRollingTranscriber(
            _ => Task.FromResult(new TranscriptionResult("partial")),
            _ => Task.FromResult(new TranscriptionResult("complete")));
        rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, new float[16 * 16000], 0));
        rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, new float[16000], 17 * 16000));

        var result = await rolling.FinishAsync("recording.wav");
        Assert.Equal("complete", result.Text);
        Assert.StartsWith("Rolling fallback: packet-gap", result.Diagnostic);
    }

    [Fact]
    public void TimedBoundaryPrefersARealPauseOverABriefQuietConsonant()
    {
        // The meeting preview still cuts on a clock and uses this to pick the quietest spot.
        var samples = Enumerable.Repeat(0.3f, 6 * 16000).ToArray();
        Array.Clear(samples, 4 * 16000 + 3200, 6400); // 400 ms pause.
        Array.Clear(samples, 5 * 16000 + 1600, 400); // 25 ms consonant gap.

        var boundaryMs = DictationRollingTranscriber.QuietBoundary([.. samples]) * 1000 / 16000;

        Assert.InRange(boundaryMs, 4_200, 4_600);
    }
}
