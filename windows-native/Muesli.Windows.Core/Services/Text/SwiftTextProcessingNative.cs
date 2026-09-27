using System.Runtime.InteropServices;
using System.Text;

namespace Muesli.Windows.Services.Text;

/// <summary>Raised when the shared Swift text bridge fails in a recoverable way.</summary>
public sealed class SwiftTextProcessingException : Exception
{
    public SwiftTextProcessingException(string message) : base(message)
    {
    }
}

/// <summary>Raised when the bridge loads but does not advertise text-processing capability.</summary>
public sealed class SwiftTextProcessingUnavailableException : Exception
{
    public SwiftTextProcessingUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
/// Explicit loader for the shared Swift text-processing exports.
/// <para>
/// The library is loaded by full path from the trusted application directory only; the resolver never
/// consults the current directory or the user's <c>PATH</c>. Exports are resolved by name, so an old
/// bridge without the text functions fails deterministically instead of being guessed at.
/// </para>
/// </summary>
internal sealed class SwiftTextProcessingNative : IDisposable
{
    internal const string BridgeFileName = "MuesliCoreABI.dll";
    internal const uint CapabilityTextProcessing = 1u << 1;

    private const int StatusOk = 0;
    private const int StatusBufferTooSmall = 5;
    private const int MaxOutputBytes = 64 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CapabilitiesDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BufferDelegate(byte[]? input, int inputLength, byte[]? output, int outputCapacity, out int written);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WordCountDelegate(byte[]? input, int inputLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LastErrorDelegate(byte[]? output, int outputCapacity, out int written);

    private readonly IntPtr _module;
    private readonly BufferDelegate _normalize;
    private readonly BufferDelegate _metrics;
    private readonly WordCountDelegate _wordCount;
    private readonly LastErrorDelegate _lastError;

    public SwiftTextProcessingNative(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException(
                $"Shared Swift bridge '{Path.GetFileName(libraryPath)}' was not found in the application directory.");
        }

        _module = NativeLibrary.Load(libraryPath);
        try
        {
            Capabilities = Resolve<CapabilitiesDelegate>("muesli_core_bridge_capabilities")();
            _normalize = Resolve<BufferDelegate>("muesli_core_bridge_normalize_transcript");
            _metrics = Resolve<BufferDelegate>("muesli_core_bridge_text_metrics");
            _wordCount = Resolve<WordCountDelegate>("muesli_core_bridge_word_count");
            _lastError = Resolve<LastErrorDelegate>("muesli_core_bridge_last_error");
        }
        catch
        {
            NativeLibrary.Free(_module);
            throw;
        }
    }

    public uint Capabilities { get; }

    public bool SupportsTextProcessing => CapabilitiesIncludeTextProcessing(Capabilities);

    /// <summary>Pure capability gate, unit-testable independent of a loaded bridge.</summary>
    internal static bool CapabilitiesIncludeTextProcessing(uint capabilities) =>
        (capabilities & CapabilityTextProcessing) != 0;

    public string NormalizeTranscript(string text) => ReadString(_normalize, text, "normalize_transcript");

    public string TextMetricsJson(string text) => ReadString(_metrics, text, "text_metrics");

    public int WordCount(string text)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var count = _wordCount(input, input.Length);
        if (count < 0)
        {
            throw Failure("word_count");
        }

        return count;
    }

    public void Dispose() => NativeLibrary.Free(_module);

    private T Resolve<T>(string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, export));

    private string ReadString(BufferDelegate function, string text, string operation)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var required = Probe(function, input, operation);
        if (required == 0)
        {
            return string.Empty;
        }

        var output = new byte[required];
        var code = function(input, input.Length, output, output.Length, out var written);
        if (code != StatusOk)
        {
            throw Failure(operation);
        }

        if (written < 0 || written > output.Length)
        {
            throw new SwiftTextProcessingException($"Shared Swift bridge '{operation}' returned an invalid length.");
        }

        return StrictUtf8.GetString(output, 0, written);
    }

    private int Probe(BufferDelegate function, byte[] input, string operation)
    {
        var code = function(input, input.Length, null, 0, out var required);
        if (code == StatusOk)
        {
            return 0;
        }

        if (code != StatusBufferTooSmall)
        {
            throw Failure(operation);
        }

        if (required < 0 || required > MaxOutputBytes)
        {
            throw new SwiftTextProcessingException($"Shared Swift bridge '{operation}' reported an implausible output size.");
        }

        return required;
    }

    private SwiftTextProcessingException Failure(string operation)
    {
        var message = $"Shared Swift bridge '{operation}' failed.";
        try
        {
            var probe = _lastError(null, 0, out var required);
            if (probe == StatusBufferTooSmall && required is > 0 and <= 64 * 1024)
            {
                var buffer = new byte[required];
                if (_lastError(buffer, buffer.Length, out var written) == StatusOk)
                {
                    message = StrictUtf8.GetString(buffer, 0, written);
                }
            }
        }
        catch
        {
            // Diagnostics must never mask the original failure.
        }

        return new SwiftTextProcessingException(message);
    }
}
