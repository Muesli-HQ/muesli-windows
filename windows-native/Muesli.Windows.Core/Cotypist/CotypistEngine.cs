using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Muesli.Windows.Cotypist;

/// <summary>
/// Port of macOS CotypistEngine (2f10b23d) on llama.cpp: a base model continuing the user's text
/// greedily, with per-token log-probabilities for gating and a prompt cache kept between requests
/// so typing forward only evaluates the new tokens. Not thread-safe; <see cref="CompleteAsync"/>
/// serialises callers.
/// </summary>
public sealed class CotypistEngine : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly CotypistVocabulary _vocabulary;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LLamaToken> _cached = [];
    private readonly int _vocabSize;

    public CotypistEngine(string modelPath, uint contextSize = 2048)
    {
        var parameters = new ModelParams(modelPath) { ContextSize = contextSize, GpuLayerCount = 0 };
        _weights = LLamaWeights.LoadFromFile(parameters);
        _context = _weights.CreateContext(parameters);
        var vocab = _weights.NativeHandle.Vocab;
        _vocabSize = vocab.Count;
        var pieces = new byte[]?[_vocabSize];
        Span<byte> buffer = stackalloc byte[256];
        for (var id = 0; id < _vocabSize; id++)
        {
            LLamaToken token = id;
            if (token.IsControl(vocab)) continue;
            var length = (int)_weights.NativeHandle.TokenToSpan(token, buffer, 0, false);
            if (length > 0 && length <= buffer.Length) pieces[id] = buffer[..length].ToArray();
        }
        _vocabulary = new CotypistVocabulary(pieces);
    }

    /// <summary>Continues <paramref name="prompt"/>. <paramref name="heal"/> is text already typed past
    /// the prompt (a half-written word); the first tokens are constrained to reproduce it, and the
    /// content starts with it. A request overtaken by newer typing (<paramref name="isCurrent"/>)
    /// throws <see cref="OperationCanceledException"/> rather than holding the engine.</summary>
    public async Task<CotypistCompletion> CompleteAsync(string prompt, int maxTokens, string? heal = null, Func<bool>? isCurrent = null)
    {
        isCurrent ??= () => true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Complete(prompt, maxTokens, heal, isCurrent)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private CotypistCompletion Complete(string prompt, int maxTokens, string? heal, Func<bool> isCurrent)
    {
        if (!isCurrent()) throw new OperationCanceledException();
        var started = Stopwatch.StartNew();
        var ids = _context.Tokenize(prompt, addBos: true, special: false);
        if (ids.Length < 2) return new CotypistCompletion([], CotypistStop.EndOfText);
        var limit = (int)_context.ContextSize - maxTokens - 8;
        if (ids.Length > limit) ids = ids[^limit..];

        // Reuse the longest shared prefix; the last prompt token is always re-fed for its logits.
        var shared = 0;
        while (shared < Math.Min(_cached.Count, ids.Length - 1) && _cached[shared] == ids[shared]) shared++;
        // Hybrid recurrent models (Qwen 3.5) can't trim their state partway: start a fresh cache, as
        // macOS does for a non-trimmable KV cache. Pure appends keep the cache either way.
        if (_cached.Count > shared) _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, shared, -1);
        if (_context.NativeHandle.MemorySequenceMaxPosition(LLamaSeqId.Zero).Value >= shared)
        {
            _context.NativeHandle.MemoryClear(true);
            _cached.Clear();
            shared = 0;
        }
        _cached.RemoveRange(shared, _cached.Count - shared);
        var batch = new LLamaBatch();
        for (var position = shared; position < ids.Length - 1;)
        {
            var end = Math.Min(position + 512, ids.Length - 1);
            batch.Clear();
            for (var index = position; index < end; index++) batch.Add(ids[index], index, LLamaSeqId.Zero, false);
            Check(_context.Decode(batch), $"prefill {position}..{end} of {ids.Length}, memory max {_context.NativeHandle.MemorySequenceMaxPosition(LLamaSeqId.Zero)}");
            _cached.AddRange(ids[position..end]);
            position = end;
        }
        var promptMs = started.Elapsed.TotalMilliseconds;

        var decodeStarted = Stopwatch.StartNew();
        var vocab = _weights.NativeHandle.Vocab;
        var next = ids[^1];
        var generated = new List<byte>();
        var tokens = new List<CotypistToken>();
        var stop = CotypistStop.Limit;
        var remaining = heal ?? "";
        var emitted = "";
        var pendingLogProb = 0.0;
        var pendingTokens = 0;
        var logprobs = new double[_vocabSize];
        Span<byte> buffer = stackalloc byte[256];
        for (var step = 0; step < maxTokens + (heal is null ? 0 : 6); step++)
        {
            if (!isCurrent()) throw new OperationCanceledException();
            batch.Clear();
            batch.Add(next, _cached.Count, LLamaSeqId.Zero, true);
            Check(_context.Decode(batch));
            _cached.Add(next);
            LogSoftmax(_context.NativeHandle.GetLogitsIth(0), logprobs);

            int token;
            var lineBreakMass = 0.0;
            // While healing, confidence is P(rest of word | typed part): relative to the tokens
            // consistent with the typed prefix, not P(whole word).
            var healNorm = 0.0;
            if (remaining.Length == 0 && tokens.Count == 0 && pendingTokens == 0)
            {
                // A suggestion never starts with a line break; the best other token's confidence is
                // rescaled to the non-line-break choices.
                var newlines = _vocabulary.Newlines;
                lineBreakMass = Math.Min(newlines.Sum(id => Math.Exp(logprobs[id])), 0.999);
                var excluded = newlines.ToHashSet();
                token = ArgMax(logprobs, id => !excluded.Contains(id));
            }
            else if (remaining.Length == 0)
            {
                token = ArgMax(logprobs, _ => true);
            }
            else
            {
                var allowed = _vocabulary.Allowed(remaining);
                if (allowed.Count == 0) { stop = CotypistStop.HealFailed; break; }
                token = allowed.MaxBy(id => logprobs[id]);
                healNorm = LogSumExp(allowed.Select(id => logprobs[id]));
                var piece = _vocabulary.Pieces[token];
                remaining = piece.Length >= remaining.Length ? "" : remaining[piece.Length..];
            }
            var logprob = logprobs[token] - Math.Log(1 - lineBreakMass) - healNorm;
            if (((LLamaToken)token).IsEndOfGeneration(vocab)) { stop = CotypistStop.EndOfText; break; }

            // Decode against everything so far so multi-byte characters come out whole.
            var length = (int)_weights.NativeHandle.TokenToSpan(token, buffer, 0, false);
            var candidate = new List<byte>(generated);
            candidate.AddRange(buffer[..Math.Clamp(length, 0, buffer.Length)].ToArray());
            var text = Utf8.GetString(candidate.ToArray());
            if (text.EndsWith('�'))
            {
                // UTF-8 needs at most four bytes: anything longer isn't going to complete.
                if (++pendingTokens >= 4) break;
                pendingLogProb += logprob;
                generated = candidate;
                next = token;
                continue;
            }
            var added = text[Math.Min(emitted.Length, text.Length)..];
            var newline = added.IndexOfAny(['\n', '\r']);
            if (newline >= 0)
            {
                // Keep what comes before the line break (".\n" still ends the sentence with ".").
                if (newline > 0) tokens.Add(new CotypistToken(added[..newline], logprob + pendingLogProb));
                stop = CotypistStop.Newline;
                break;
            }
            generated = candidate;
            tokens.Add(new CotypistToken(added, logprob + pendingLogProb));
            emitted = text;
            pendingLogProb = 0;
            pendingTokens = 0;
            next = token;
        }
        return new CotypistCompletion(tokens, stop, promptMs, decodeStarted.Elapsed.TotalMilliseconds, shared);
    }

    private static void Check(DecodeResult result, string where = "")
    {
        if (result != DecodeResult.Ok) throw new InvalidOperationException($"llama decode failed: {result} {where}");
    }

    private static void LogSoftmax(ReadOnlySpan<float> logits, double[] output)
    {
        var max = double.NegativeInfinity;
        foreach (var value in logits) max = Math.Max(max, value);
        var sum = 0.0;
        for (var index = 0; index < output.Length; index++) sum += Math.Exp(logits[index] - max);
        var log = max + Math.Log(sum);
        for (var index = 0; index < output.Length; index++) output[index] = logits[index] - log;
    }

    private static int ArgMax(double[] values, Func<int, bool> allowed)
    {
        var best = -1;
        for (var index = 0; index < values.Length; index++)
            if (allowed(index) && (best < 0 || values[index] > values[best])) best = index;
        return best;
    }

    private static double LogSumExp(IEnumerable<double> values)
    {
        var list = values.ToList();
        var max = list.Max();
        return max + Math.Log(list.Sum(value => Math.Exp(value - max)));
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
        _gate.Dispose();
    }
}
