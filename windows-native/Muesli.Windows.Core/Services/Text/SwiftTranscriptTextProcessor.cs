namespace Muesli.Windows.Services.Text;

/// <summary>
/// Calls the shared Swift text core through the C ABI. On a recoverable native
/// failure it logs once and completes the operation with the parity-tested
/// managed implementation, so a missing bridge degrades safely instead of
/// breaking dictation. It never logs transcript contents.
/// </summary>
public sealed class SwiftTranscriptTextProcessor : ITranscriptTextProcessor, IDisposable
{
    private readonly SwiftTextProcessingNative _native;
    private readonly ITranscriptTextProcessor _fallback;
    private readonly Action<string>? _log;
    private readonly bool _ownsNative;
    private int _fallbackActivated;

    public SwiftTranscriptTextProcessor(string libraryPath, ITranscriptTextProcessor fallback, Action<string>? log = null)
        : this(new SwiftTextProcessingNative(libraryPath), fallback, log, ownsNative: true)
    {
    }

    internal SwiftTranscriptTextProcessor(
        SwiftTextProcessingNative native,
        ITranscriptTextProcessor fallback,
        Action<string>? log,
        bool ownsNative)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _log = log;
        _ownsNative = ownsNative;

        if (!SwiftTextProcessingNative.CapabilitiesIncludeTextProcessing(native.Capabilities))
        {
            if (ownsNative)
            {
                native.Dispose();
            }

            throw new SwiftTextProcessingUnavailableException(
                "The shared Swift bridge does not advertise text-processing capability.");
        }
    }

    /// <summary>True while native text processing is the active path.</summary>
    public bool IsNative => Volatile.Read(ref _fallbackActivated) == 0;

    /// <summary>True once a recoverable native failure switched this instance to the managed fallback.</summary>
    public bool FallbackActivated => Volatile.Read(ref _fallbackActivated) == 1;

    public uint Capabilities => _native.Capabilities;

    public string NormalizeTranscript(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Run(
            native => native.NormalizeTranscript(text),
            fallback => fallback.NormalizeTranscript(text),
            "normalize_transcript");
    }

    public int CountWords(string? text) =>
        string.IsNullOrEmpty(text)
            ? 0
            : Run(
                native => native.WordCount(text),
                fallback => fallback.CountWords(text),
                "word_count");

    public void Dispose()
    {
        if (_ownsNative)
        {
            _native.Dispose();
        }
    }

    private T Run<T>(
        Func<SwiftTextProcessingNative, T> nativeCall,
        Func<ITranscriptTextProcessor, T> fallbackCall,
        string operation)
    {
        if (Volatile.Read(ref _fallbackActivated) == 0)
        {
            try
            {
                return nativeCall(_native);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                ActivateFallback(operation, exception);
            }
        }

        return fallbackCall(_fallback);
    }

    private void ActivateFallback(string operation, Exception exception)
    {
        if (Interlocked.Exchange(ref _fallbackActivated, 1) == 0)
        {
            _log?.Invoke(
                $"Shared Swift text processing failed during {operation}; using parity-safe managed fallback. ({exception.GetType().Name})");
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is SwiftTextProcessingException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException;
}
