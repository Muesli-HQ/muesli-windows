using System.Threading.Channels;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

// Ported from macOS BufferedTranscriptionSessionTests.swift and BufferedTranscriptionContinuityTests.swift.
public sealed class BufferedTranscriptionSessionTests
{
    private static BufferedTranscriptionWord Word(string text, double start, double end) => new(text, start, end);

    [Fact]
    public async Task FinishFlushesShortAudioAndPreservesRevisablePunctuation()
    {
        var output = new Output();
        var session = new BufferedTranscriptionSession(
            decode: (_, _) => Task.FromResult(BufferedTranscriptionWindow.Aligned("Hello, café!",
                [Word("Hello,", 0, 0.2), Word("café!", 0.3, 0.8)])),
            onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Filled(16_000, 0.2f));
        await session.FinishAsync();
        Assert.Equal("Hello, café!", output.Values[^1].Text);
        Assert.Equal(0, output.Failures);
        Assert.Equal(new BufferedTranscriptionSession.SessionDiagnostics(16_000, 1, 1, true), session.Diagnostics);
    }

    [Theory]
    [InlineData("go")]
    [InlineData("café")]
    [InlineData("привет")]
    [InlineData("مرحبا")]
    [InlineData("你好")]
    public async Task ContinuousAudioRollsOverUsingTimesWithoutDeletingRepeatedWords(string word)
    {
        var output = new Output();
        var separator = word == "你好" ? "" : " ";
        var session = new BufferedTranscriptionSession(
            new BufferedTranscriptionSession.Configuration
            {
                WindowSamples = 6 * 16_000, RightContextSamples = 2 * 16_000, MaximumBufferedSamples = 40 * 16_000
            },
            decode: (samples, _) =>
            {
                // PCM encodes absolute sample position, giving an oracle independent of the window start.
                var @base = samples[0] / 16_000.0;
                var end = @base + samples.Length / 16_000.0;
                var words = Enumerable.Range(0, 30)
                    .Select(index => (Start: index + 0.1, Finish: index + 0.7))
                    .Where(span => span.Start >= @base && span.Finish <= end)
                    .Select(span => Word(word, span.Start - @base, span.Finish - @base)).ToList();
                return Task.FromResult(BufferedTranscriptionWindow.Aligned(string.Join(separator, words.Select(w => w.Text)), words));
            },
            onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Enumerable.Range(0, 30 * 16_000).Select(index => (float)index).ToArray());
        await session.FinishAsync();
        Assert.Equal(string.Join(separator, Enumerable.Repeat(word, 30)), output.Values[^1].Text);
        Assert.True(output.Values.Count > 3);
        Assert.Equal(Enumerable.Range(1, output.Values.Count).Select(n => (ulong)n), output.Values.Select(v => v.Revision));
        Assert.Equal(0, output.Failures);
        AssertCommittedPrefixes(output.Values);
    }

    [Fact]
    public async Task ArrivalsCoalesceWhileASingleDecoderIsInFlight()
    {
        var decoder = new HeldDecoder();
        var output = new Output();
        var session = new BufferedTranscriptionSession(decode: decoder.Decode, onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Filled(32_000, 0.2f));
        Assert.Equal(32_000, await decoder.Started.Reader.ReadAsync());
        for (var i = 0; i < 8; i++) session.Enqueue(Filled(4_000, 0.3f));
        decoder.Release();
        await session.FinishAsync();
        Assert.Equal(1, decoder.MaximumInFlight);
        Assert.True(decoder.CallCount <= 3);
        Assert.Equal("hello", output.Values[^1].Text);
    }

    [Fact]
    public async Task CancelReleasesFinishAndRejectsInFlightCompletionAndFutureAudio()
    {
        var decoder = new HeldDecoder();
        var output = new Output();
        var session = new BufferedTranscriptionSession(decode: decoder.Decode, onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Filled(32_000, 0.2f));
        await decoder.Started.Reader.ReadAsync();
        session.Cancel();
        var result = await session.FinishForReuseAsync();
        Assert.False(result.CanReuse);
        Assert.Equal(BufferedTranscriptionSession.ReuseFallbackReason.Cancelled, result.FallbackReason);
        session.Enqueue(Filled(32_000, 0.2f));
        decoder.Release();
        await decoder.Completed.Reader.ReadAsync();
        Assert.Empty(output.Values);
        Assert.Equal(0, output.Failures);
        Assert.Equal(1, decoder.CallCount);
    }

    [Fact]
    public async Task OverrunFailsExplicitlyInsteadOfDroppingUncommittedAudio()
    {
        var output = new Output();
        var session = new BufferedTranscriptionSession(
            decode: (_, _) => throw new InvalidOperationException("Overflow must fail before decoding"),
            onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Filled(720_001, 0.2f));
        var result = await session.FinishForReuseAsync();
        Assert.Equal(1, output.Failures);
        Assert.Empty(output.Values);
        Assert.Equal(BufferedTranscriptionSession.ReuseFallbackReason.TranscriptionFailed, result.FallbackReason);
    }

    [Fact]
    public async Task MissingTimingsFailAndAllZeroSilenceBypassesTheModel()
    {
        var output = new Output();
        var session = new BufferedTranscriptionSession(
            decode: (_, _) => Task.FromResult(new BufferedTranscriptionWindow("unaccounted words", [])),
            onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Filled(16_000, 0.2f));
        await session.FinishAsync();
        Assert.Equal(1, output.Failures);

        var silent = new BufferedTranscriptionSession(
            decode: (_, _) => throw new InvalidOperationException("Silence must not reach the decoder"),
            onUpdate: output.Update, onFailure: output.Fail);
        silent.Enqueue(new float[16_000]);
        await silent.FinishAsync();
        Assert.Equal("", output.Values[^1].Text);
        Assert.Equal(new BufferedTranscriptionSession.SessionDiagnostics(16_000, 0, 1, true), silent.Diagnostics);
    }

    [Fact]
    public void WordAlignmentRejectsMissingTextInsteadOfSilentlyDroppingIt()
    {
        Assert.Throws<BufferedTranscriptionSession.SessionFailure>(() =>
            BufferedTranscriptionWindow.Aligned("please keep everything", [Word("please", 0, 1)]));
    }

    [Theory]
    [InlineData("오늘은", "좋은 날입니다", "오늘은 좋은 날입니다")]
    [InlineData("안녕하세요.", "오늘은 맑아요", "안녕하세요. 오늘은 맑아요")]
    [InlineData("你好", "世界", "你好世界")]
    public async Task IndependentChunksRetainWordSpacesWithoutSpacingHanText(string first, string second, string expected)
    {
        var session = new BufferedTranscriptionSession(
            decodeWithoutTimings: (audio, _) => Task.FromResult(Pcm.Start(audio) == 0 ? first : second),
            onFailure: error => throw new Xunit.Sdk.XunitException($"Unexpected stream failure: {error}"));
        session.Enqueue(Pcm.Samples(0, 64_000));
        var result = await session.FinishForReuseAsync();
        Assert.True(result.CanReuse);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task UntimedBackendsStreamDisjointChunksFromTheFirstChunk()
    {
        var output = new Output();
        var ranges = new List<(int, int)>();
        var session = new BufferedTranscriptionSession(
            decodeWithoutTimings: (audio, _) =>
            {
                lock (ranges) ranges.Add((Pcm.Start(audio), audio.Length));
                return Task.FromResult($"chunk{Pcm.Start(audio)}");
            },
            onUpdate: output.Update, onFailure: output.Fail);
        for (var offset = 0; offset < 640_000; offset += 32_000)
        {
            session.Enqueue(Pcm.Samples(offset, 32_000));
            await output.Next();
            Assert.False(session.Diagnostics.Closed);
        }
        var result = await session.FinishForReuseAsync();
        Assert.True(result.CanReuse && result.Completed);
        Assert.Equal(640_000, result.DecodedThroughSample);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (i * 32_000, 32_000)), ranges);
        AssertCommittedPrefixes(output.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTimingsSwitchToBoundedDisjointChunksForEitherRuntimePolicy(bool whisper)
    {
        var output = new Output();
        var timedCalls = 0;
        var ranges = new List<int>();
        var session = new BufferedTranscriptionSession(
            whisper ? BufferedTranscriptionSession.Configuration.Whisper : new BufferedTranscriptionSession.Configuration(),
            decode: (_, _) =>
            {
                Interlocked.Increment(ref timedCalls);
                throw new BufferedTranscriptionPreviewDeferral(BufferedTranscriptionPreviewDeferral.MissingWordTimings);
            },
            decodeWithoutTimings: (audio, _) =>
            {
                lock (ranges) ranges.Add(Pcm.Start(audio));
                return Task.FromResult($"chunk{Pcm.Start(audio)}");
            },
            onUpdate: output.Update, onFailure: output.Fail);
        session.Enqueue(Pcm.Samples(0, 32_000));
        Assert.Equal("", (await output.Next()).CommittedText);
        session.Enqueue(Pcm.Samples(32_000, 32_000));
        Assert.Equal("chunk0", (await output.Next()).CommittedText);
        Assert.Equal("chunk0 chunk32000", (await output.Next()).CommittedText);
        session.Enqueue(Pcm.Samples(64_000, 32_000));
        Assert.Equal("chunk0 chunk32000 chunk64000", (await output.Next()).CommittedText);

        var result = await session.FinishForReuseAsync();
        Assert.True(result.CanReuse && result.Completed);
        Assert.Equal((96_000, 96_000), (result.ReceivedSamples, result.DecodedThroughSample));
        Assert.Equal(2, timedCalls);
        Assert.Equal([0, 32_000, 64_000], ranges);
        Assert.Equal(0, output.Failures);
    }

    [Theory]
    [InlineData("Hello [BLANK_AUDIO] world", "Hello world")]
    [InlineData("Keep going [musi", "Keep going")]
    [InlineData("A (note) stays", "A (note) stays")]
    public void CommittedTextHoldsUnfinishedCuesAndStripsKnownOnes(string committed, string expected)
    {
        Assert.Equal(expected, TranscriptionEngineArtifactsFilter.ApplyToCommittedText(committed));
    }

    private static float[] Filled(int count, float value) => Enumerable.Repeat(value, count).ToArray();

    private static void AssertCommittedPrefixes(IReadOnlyList<BufferedTranscriptionUpdate> values)
    {
        foreach (var (previous, current) in values.Zip(values.Skip(1)))
            Assert.StartsWith(previous.CommittedText, current.CommittedText, StringComparison.Ordinal);
    }

    /// <summary>PCM carries its absolute sample index plus one, so zero never reads as silence.</summary>
    private static class Pcm
    {
        public static float[] Samples(int start, int count) => Enumerable.Range(start + 1, count).Select(i => (float)i).ToArray();
        public static int Start(float[] samples) => (int)samples[0] - 1;
    }

    private sealed class Output
    {
        private readonly Channel<BufferedTranscriptionUpdate> _updates = Channel.CreateUnbounded<BufferedTranscriptionUpdate>();
        private readonly List<BufferedTranscriptionUpdate> _values = [];
        private int _failures;

        public IReadOnlyList<BufferedTranscriptionUpdate> Values { get { lock (_values) return [.. _values]; } }
        public int Failures => Volatile.Read(ref _failures);

        public void Update(BufferedTranscriptionUpdate update)
        {
            lock (_values) _values.Add(update);
            _updates.Writer.TryWrite(update);
        }

        public void Fail(Exception _) => Interlocked.Increment(ref _failures);

        public async Task<BufferedTranscriptionUpdate> Next() =>
            await _updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class HeldDecoder
    {
        private readonly TaskCompletionSource _permit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _maximumInFlight;
        private int _calls;

        public Channel<int> Started { get; } = Channel.CreateUnbounded<int>();
        public Channel<bool> Completed { get; } = Channel.CreateUnbounded<bool>();
        public int MaximumInFlight => Volatile.Read(ref _maximumInFlight);
        public int CallCount => Volatile.Read(ref _calls);

        public void Release() => _permit.TrySetResult();

        public async Task<BufferedTranscriptionWindow> Decode(float[] samples, CancellationToken _)
        {
            Interlocked.Increment(ref _calls);
            var inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maximumInFlight, inFlight);
            Started.Writer.TryWrite(samples.Length);
            // macOS holds the continuation regardless of cancellation; the session must not wait for it.
            await _permit.Task;
            Interlocked.Decrement(ref _inFlight);
            Completed.Writer.TryWrite(true);
            return BufferedTranscriptionWindow.Aligned("hello", [Word("hello", 0, 0.5)]);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
