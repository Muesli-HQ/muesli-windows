using System.Text;

namespace Muesli.Windows.Cotypist;

/// <summary>
/// Port of macOS CotypistVocabulary: each token's surface text, indexed by first character, for
/// token healing. On Windows llama.cpp's <c>token_to_piece</c> already returns the raw bytes for both
/// byte-level BPE (Qwen) and SentencePiece (Gemma) vocabularies, so no Ġ/▁ table is needed here.
/// A piece holding part of a multi-byte character decodes to U+FFFD, which no typed text starts
/// with, so it simply never matches.
/// </summary>
public sealed class CotypistVocabulary
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly Dictionary<char, List<int>> _byFirst = [];

    public IReadOnlyList<string> Pieces { get; }

    /// <summary>Tokens that are or contain a line break.</summary>
    public IReadOnlyList<int> Newlines { get; }

    /// <param name="pieces">Raw piece bytes per token id; null for control tokens.</param>
    public CotypistVocabulary(IReadOnlyList<byte[]?> pieces)
    {
        var decoded = new string[pieces.Count];
        var newlines = new List<int>();
        for (var id = 0; id < pieces.Count; id++)
        {
            decoded[id] = "";
            if (pieces[id] is not { Length: > 0 } bytes) continue;
            var piece = Utf8.GetString(bytes);
            if (piece.Contains('\n') || piece.Contains('\r')) { newlines.Add(id); continue; }
            decoded[id] = piece;
            if (!_byFirst.TryGetValue(piece[0], out var list)) _byFirst[piece[0]] = list = [];
            list.Add(id);
        }
        Pieces = decoded;
        Newlines = newlines;
    }

    /// <summary>Tokens that keep the output consistent with <paramref name="remaining"/>: they finish
    /// it or are a prefix of it. Tokens covering all of it win (a word is usually one token, and
    /// stepping through a fragment derails what follows); shorter fragments are the fallback.</summary>
    public IReadOnlyList<int> Allowed(string remaining)
    {
        if (remaining.Length == 0 || !_byFirst.TryGetValue(remaining[0], out var candidates)) return [];
        var covering = candidates.Where(id => Pieces[id].StartsWith(remaining, StringComparison.Ordinal)).ToList();
        return covering.Count > 0 ? covering : candidates.Where(id => remaining.StartsWith(Pieces[id], StringComparison.Ordinal)).ToList();
    }
}
