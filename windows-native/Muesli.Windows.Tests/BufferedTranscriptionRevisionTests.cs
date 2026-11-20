using System.Collections.Concurrent;
using System.Threading.Channels;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>macOS 3a9ade10: owned context revised by Whisper is realigned with a timed LCS that needs a unique
/// overlapping endpoint; anything less leaves the committed text alone.</summary>
public sealed class BufferedTranscriptionRevisionTests
{
    private static BufferedTranscriptionWord Word(string text, double start, double end) => new(text, start, end);

    private static BufferedTranscriptionWindow Window(List<BufferedTranscriptionWord> words) =>
        BufferedTranscriptionWindow.Aligned(string.Join(' ', words.Select(word => word.Text)), words);

    private static float[] Filled(int seconds) => Enumerable.Repeat(0.2f, seconds * 16_000).ToArray();

    private sealed class Harness
    {
        private readonly Channel<BufferedTranscriptionUpdate> _updates = Channel.CreateUnbounded<BufferedTranscriptionUpdate>();
        private readonly Queue<BufferedTranscriptionWindow> _windows;
        public readonly List<BufferedTranscriptionUpdate> Values = [];
        public readonly ConcurrentQueue<string> Diagnostics = new();
        public int Failures;
        public readonly BufferedTranscriptionSession Session;

        public Harness(params BufferedTranscriptionWindow[] windows)
        {
            _windows = new Queue<BufferedTranscriptionWindow>(windows);
            Session = new BufferedTranscriptionSession(
                // Keep these reduced hypotheses at one PCM origin.
                BufferedTranscriptionSession.Configuration.Whisper with { WindowSamples = 15 * 16_000 },
                decode: (_, _) =>
                {
                    lock (_windows) return Task.FromResult(_windows.Count > 1 ? _windows.Dequeue() : _windows.Peek());
                },
                onUpdate: update => { lock (Values) Values.Add(update); _updates.Writer.TryWrite(update); },
                onFailure: _ => Interlocked.Increment(ref Failures),
                onDiagnostic: Diagnostics.Enqueue);
        }

        public async Task<BufferedTranscriptionUpdate> Next() =>
            await _updates.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("missing_context")]
    [InlineData("moved_endpoint")]
    [InlineData("zero_endpoint")]
    [InlineData("split_endpoint")]
    [InlineData("unequal_split_endpoint")]
    [InlineData("short_split_tail")]
    [InlineData("overlapping_endpoint")]
    [InlineData("changed_following")]
    [InlineData("moved_following")]
    public async Task EditedContextDoesNotGuessAnUnsupportedOrAmbiguousEndpoint(string change)
    {
        var initial = new List<BufferedTranscriptionWord> { Word("we", 2.0, 2.2), Word("worked", 2.2, 2.6), Word("weekly", 3.0, 4.0) };
        if (change.EndsWith("following", StringComparison.Ordinal)) initial.Add(Word("next", 5.2, 5.8));
        var revised = new List<BufferedTranscriptionWord>
        {
            Word(change == "missing_context" ? "they" : "we", 2.0, 2.2), Word("all", 2.2, 2.25), Word("worked", 2.25, 2.6),
            Word("daily", 3.0, 4.0), Word("next", 7.0, 7.5)
        };
        if (change == "changed_following") revised[4] = Word("later", 5.2, 5.8);
        if (change == "moved_following") revised[4] = Word("next", 5.6, 5.8);
        if (change == "moved_endpoint") revised[3] = Word("daily", 4.5, 5.0);
        if (change == "zero_endpoint") revised[3] = Word("daily", 3.5, 3.5);
        if (change is "split_endpoint" or "unequal_split_endpoint" or "short_split_tail")
        {
            var split = change == "split_endpoint" ? 3.5 : change == "unequal_split_endpoint" ? 3.6 : 3.9;
            revised[3] = Word("day", 3.0, split);
            revised.Insert(4, Word("lee", split, 4.0));
        }
        if (change == "overlapping_endpoint") revised.Insert(4, Word("daily", 3.1, 4.0));

        var harness = new Harness(Window(initial), Window(initial), Window(revised));
        harness.Session.Enqueue(Filled(6));
        await harness.Next();
        harness.Session.Enqueue(Filled(1));
        var stable = await harness.Next();
        Assert.Equal("we worked weekly", stable.CommittedText);
        harness.Session.Enqueue(Filled(1));
        Assert.Equal(stable.CommittedText, (await harness.Next()).CommittedText);
        Assert.DoesNotContain("ownership_realigned:edited_context", harness.Diagnostics);
        harness.Session.Cancel();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TokenInsertionAndEndpointCorrectionKeepCommittedWordsAndResume(bool deleteContextWord, bool hasFollowingAnchor)
    {
        // Reduced from captured Turbo hypotheses: the new "I" and Daly -> daily must not strand the stream.
        var initial = new List<BufferedTranscriptionWord>
        {
            Word("who", 2.56, 2.68), Word("worked", 2.68, 2.92), Word("with", 2.92, 3.16), Word("Daly.", 3.16, 3.94)
        };
        if (hasFollowingAnchor) initial.Add(Word("There's", 5.20, 5.26));
        var revised = new List<BufferedTranscriptionWord>
        {
            Word("who", 2.56, 2.70), Word("I", 2.70, 2.72), Word("worked", 2.72, 2.98), Word("with", 2.98, 3.06),
            Word("daily.", 3.06, 3.64), Word("There's", 7.20, 7.26), Word("a", 7.26, 7.30), Word("few", 7.30, 7.46),
            Word("things.", 7.46, 7.94)
        };
        if (hasFollowingAnchor)
            for (var index = 5; index < revised.Count; index++)
                revised[index] = Word(revised[index].Text, revised[index].Start - 2, revised[index].End - 2);
        if (deleteContextWord) revised.RemoveAll(word => word.Text == "with");

        var harness = new Harness(Window(initial), Window(initial), Window(revised), Window(revised));
        harness.Session.Enqueue(Filled(6));
        await harness.Next();
        harness.Session.Enqueue(Filled(1));
        Assert.Equal("who worked with Daly.", (await harness.Next()).CommittedText);
        harness.Session.Enqueue(Filled(1));
        await harness.Next();
        harness.Session.Enqueue(Filled(1));
        var progressed = await harness.Next();
        Assert.Equal("who worked with Daly. There's a few" + (hasFollowingAnchor ? " things." : ""), progressed.CommittedText);

        var result = await harness.Session.FinishForReuseAsync();
        Assert.True(result.CanReuse);
        Assert.Equal("who worked with Daly. There's a few things.", result.Text);
        Assert.Equal(0, harness.Failures);
        Assert.Contains("ownership_realigned:edited_context", harness.Diagnostics);
        Assert.DoesNotContain(harness.Diagnostics, diagnostic => diagnostic.Contains("ownership_recovery:", StringComparison.Ordinal));
        lock (harness.Values)
            foreach (var (previous, current) in harness.Values.Zip(harness.Values.Skip(1)))
                Assert.StartsWith(previous.CommittedText, current.CommittedText, StringComparison.Ordinal);
    }
}
