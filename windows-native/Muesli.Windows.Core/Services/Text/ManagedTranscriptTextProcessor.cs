using System.Text.RegularExpressions;

namespace Muesli.Windows.Services.Text;

/// <summary>
/// Parity-safe managed implementation of the canonical transcript text contract.
/// It is the fallback when the shared Swift bridge is unavailable and the oracle the
/// Swift implementation is parity-tested against. It must produce identical output.
/// </summary>
public sealed class ManagedTranscriptTextProcessor : ITranscriptTextProcessor
{
    private static readonly Regex WhitespaceRun = new(
        @"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsNative => false;

    public string NormalizeTranscript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return WhitespaceRun.Replace(text.Trim(), " ");
    }

    public int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}
