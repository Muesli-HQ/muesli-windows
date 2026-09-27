using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>Whether a top-level UI exception can be contained or must end the process.</summary>
public enum UiExceptionDisposition
{
    Recoverable,
    Fatal,
}

public sealed record UiExceptionClassification(UiExceptionDisposition Disposition, string Reason);

/// <summary>
/// Pure classifier for top-level UI exceptions. Only a small, explicit set of failures that come
/// from known Windows operations (cancellation, timeouts, permission denial, transient clipboard /
/// shell / device COM failures) is recoverable. Everything else — including unknown exceptions and
/// corrupted-state errors — is fatal, so the app never continues after an unknown fault.
/// </summary>
public static class UiExceptionClassifier
{
    // Explicit, evidence-backed recoverable HRESULTs for clipboard, picker, shell, notification and
    // transient device operations. Deliberately small; anything unknown is fatal.
    private static readonly HashSet<int> RecoverableHResults =
    [
        unchecked((int)0x800401D0), // CLIPBRD_E_CANT_OPEN
        unchecked((int)0x80070005), // E_ACCESSDENIED (permission denial)
        unchecked((int)0x8007001F), // ERROR_GEN_FAILURE (device failure)
        unchecked((int)0x80070490), // ERROR_NOT_FOUND (transient shell/device)
        unchecked((int)0x8007048F), // ERROR_DEVICE_NOT_CONNECTED
        unchecked((int)0x800704C7), // ERROR_CANCELLED (shell/picker cancel)
        unchecked((int)0x80004005), // E_FAIL (transient COM/shell)
        unchecked((int)0x80263001), // WININET/notification transient
    ];

    public static UiExceptionClassification Classify(Exception? exception)
    {
        if (exception is null)
        {
            return new UiExceptionClassification(UiExceptionDisposition.Fatal, "unknown");
        }

        if (exception is OperationCanceledException)
        {
            return new UiExceptionClassification(UiExceptionDisposition.Recoverable, "cancelled");
        }

        if (exception is TimeoutException)
        {
            return new UiExceptionClassification(UiExceptionDisposition.Recoverable, "timeout");
        }

        if (exception is UnauthorizedAccessException)
        {
            return new UiExceptionClassification(UiExceptionDisposition.Recoverable, "access-denied");
        }

        if (exception is COMException comException && RecoverableHResults.Contains(comException.HResult))
        {
            return new UiExceptionClassification(UiExceptionDisposition.Recoverable, "transient-com");
        }

        if (RecoverableHResults.Contains(exception.HResult))
        {
            return new UiExceptionClassification(UiExceptionDisposition.Recoverable, "transient-hresult");
        }

        return new UiExceptionClassification(UiExceptionDisposition.Fatal, exception.GetType().Name);
    }
}

/// <summary>
/// Applies the classifier with reentrancy protection and redacted diagnostics. The fatal path logs
/// once and requests shutdown exactly once; a second fatal exception cannot recurse. Diagnostics
/// never include the exception message, so transcript or secret text cannot reach the log.
/// </summary>
public sealed class UiExceptionPolicy
{
    private readonly Action<string> _logRecoverable;
    private readonly Action<string> _logFatal;
    private readonly Action _requestShutdown;
    private int _fatalHandled;

    public UiExceptionPolicy(Action<string> logRecoverable, Action<string> logFatal, Action requestShutdown)
    {
        _logRecoverable = logRecoverable ?? throw new ArgumentNullException(nameof(logRecoverable));
        _logFatal = logFatal ?? throw new ArgumentNullException(nameof(logFatal));
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
    }

    public bool FatalHandled => Volatile.Read(ref _fatalHandled) == 1;

    public UiExceptionDisposition Handle(Exception exception)
    {
        var classification = UiExceptionClassifier.Classify(exception);
        if (classification.Disposition == UiExceptionDisposition.Recoverable)
        {
            _logRecoverable($"Recoverable UI exception contained ({classification.Reason}).");
            return UiExceptionDisposition.Recoverable;
        }

        if (Interlocked.Exchange(ref _fatalHandled, 1) == 1)
        {
            // Reentrancy guard: a fatal exception raised while handling a fatal exception must not
            // re-log or re-enter shutdown.
            return UiExceptionDisposition.Fatal;
        }

        _logFatal(BuildRedactedDiagnostic(exception, classification));
        _requestShutdown();
        return UiExceptionDisposition.Fatal;
    }

    /// <summary>Type, HRESULT and stack only — never the exception message.</summary>
    internal static string BuildRedactedDiagnostic(Exception exception, UiExceptionClassification classification)
    {
        var hresult = exception.HResult == 0
            ? ""
            : $" hresult=0x{exception.HResult:X8}";
        var stack = string.IsNullOrWhiteSpace(exception.StackTrace) ? "" : $"{Environment.NewLine}{exception.StackTrace}";
        return $"Fatal UI exception: type={exception.GetType().FullName}{hresult} reason={classification.Reason}.{stack}";
    }
}
