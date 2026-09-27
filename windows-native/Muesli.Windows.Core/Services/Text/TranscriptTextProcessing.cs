namespace Muesli.Windows.Services.Text;

/// <summary>
/// Process-wide selection of the active transcript text processor. Defaults to the managed
/// implementation so tests and any pre-startup caller behave as before; the WinUI composition root
/// installs the Swift-backed processor once at startup.
/// </summary>
public static class TranscriptTextProcessing
{
    private static ITranscriptTextProcessor _current = new ManagedTranscriptTextProcessor();
    private static int _nativeActive;

    public static ITranscriptTextProcessor Current => Volatile.Read(ref _current);

    /// <summary>True when the shared native Swift core is the active text processor.</summary>
    public static bool NativeActive => Volatile.Read(ref _nativeActive) == 1;

    public static void Initialize(ITranscriptTextProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        Volatile.Write(ref _current, processor);
        Volatile.Write(ref _nativeActive, processor.IsNative ? 1 : 0);
    }

    /// <summary>Test/reset hook: return to the managed implementation.</summary>
    public static void ResetToManaged() => Initialize(new ManagedTranscriptTextProcessor());
}
