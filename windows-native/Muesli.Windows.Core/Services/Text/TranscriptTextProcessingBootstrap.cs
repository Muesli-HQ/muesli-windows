namespace Muesli.Windows.Services.Text;

/// <summary>
/// Resolves and installs the shared Swift text processor from the trusted application directory.
/// Kept in the build process/app startup, never in ad-hoc runtime probing: the bridge is loaded by
/// full path and never searched for on the current directory or the user's <c>PATH</c>.
/// </summary>
public static class TranscriptTextProcessingBootstrap
{
    public static string LastDiagnostic { get; private set; } = "Shared text processing: not initialized.";

    /// <summary>
    /// Installs the Swift-backed processor when the bridge is present and usable, otherwise the
    /// parity-safe managed fallback (unless the caller forbids fallback).
    /// </summary>
    /// <returns>True when native text processing is active.</returns>
    public static bool Initialize(
        Action<string>? log = null,
        string? applicationDirectory = null,
        bool allowManagedFallback = true)
    {
        var directory = string.IsNullOrWhiteSpace(applicationDirectory)
            ? AppContext.BaseDirectory
            : applicationDirectory;
        var bridgePath = Path.Combine(directory, SwiftTextProcessingNative.BridgeFileName);

        try
        {
            var native = new SwiftTranscriptTextProcessor(bridgePath, new ManagedTranscriptTextProcessor(), log);
            TranscriptTextProcessing.Initialize(native);
            LastDiagnostic =
                $"Shared text processing: native=active bridge={SwiftTextProcessingNative.BridgeFileName} abi=1 capabilities=0x{native.Capabilities:X} fallback=inactive.";
            log?.Invoke(LastDiagnostic);
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            LastDiagnostic =
                $"Shared text processing: native=unavailable reason={exception.GetType().Name} fallback=parity-safe-managed.";
            log?.Invoke(LastDiagnostic);
            if (!allowManagedFallback)
            {
                throw;
            }

            TranscriptTextProcessing.Initialize(new ManagedTranscriptTextProcessor());
            return false;
        }
    }

    private static bool IsUnavailable(Exception exception) =>
        exception is DllNotFoundException
            or FileNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException
            or SwiftTextProcessingException
            or SwiftTextProcessingUnavailableException;
}
