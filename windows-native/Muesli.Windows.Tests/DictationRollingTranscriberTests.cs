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
    public async Task CompletedAudioDecodesBeforeStopAndTailIsAppendedInOrder()
    {
        using var directory = new TestDirectory();
        var recording = directory.File("recording.wav");
        using (var writer = new WaveFileWriter(recording, new WaveFormat(16000, 16, 1)))
            writer.Write(new byte[7 * 16000 * 2]);
        var firstDecoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var fallbackCalls = 0;
        var rolling = new DictationRollingTranscriber(
            _ =>
            {
                var number = Interlocked.Increment(ref calls);
                if (number == 1) firstDecoded.SetResult();
                return Task.FromResult(new TranscriptionResult($"part {number}"));
            },
            _ =>
            {
                Interlocked.Increment(ref fallbackCalls);
                return Task.FromResult(new TranscriptionResult("fallback"));
            });

        rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, new float[6 * 16000], 0));
        await firstDecoded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls); // Recognition ran while recording was still active.
        rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, new float[16000], 6 * 16000));

        var result = await rolling.FinishAsync(recording);

        Assert.Equal("part 1 part 2", result.Text);
        Assert.StartsWith("ASR during capture:", result.Diagnostic);
        Assert.Equal(0, fallbackCalls);
        Assert.Equal(7_000, result.DurationMs);
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
    public async Task ChunkBoundaryPrefersARealPauseOverABriefQuietConsonant()
    {
        var samples = Enumerable.Repeat(0.3f, 6 * 16000).ToArray();
        Array.Clear(samples, 4 * 16000 + 3200, 6400); // 400 ms pause.
        Array.Clear(samples, 5 * 16000 + 1600, 400); // 25 ms consonant gap.
        var firstChunk = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rolling = new DictationRollingTranscriber(
            wav =>
            {
                using var reader = new WaveFileReader(new MemoryStream(wav));
                firstChunk.SetResult((int)reader.TotalTime.TotalMilliseconds);
                return Task.FromResult(new TranscriptionResult("first"));
            },
            _ => Task.FromResult(new TranscriptionResult("fallback")));

        rolling.Feed(null, new LivePcmSamplesEventArgs(LiveTranscriptChannel.Microphone, samples, 0));
        var boundaryMs = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rolling.DrainAsync();

        Assert.InRange(boundaryMs, 4_200, 4_600);
    }
}
