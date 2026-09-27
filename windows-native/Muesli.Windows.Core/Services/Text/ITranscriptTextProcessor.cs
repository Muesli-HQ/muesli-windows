namespace Muesli.Windows.Services.Text;

/// <summary>
/// Domain-facing transcript text operations. The rest of the application depends on this shape only:
/// no P/Invoke, native pointers, buffers, ABI versions, DLL names or JSON transport leak out.
/// </summary>
public interface ITranscriptTextProcessor
{
    /// <summary>Trim and collapse whitespace to the canonical single-spaced transcript form.</summary>
    string NormalizeTranscript(string text);

    /// <summary>Canonical whitespace-delimited word count.</summary>
    int CountWords(string? text);

    /// <summary>True when this processor calls the shared native Swift core.</summary>
    bool IsNative { get; }
}
