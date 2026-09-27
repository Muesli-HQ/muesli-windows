using System.Runtime.InteropServices;

namespace Muesli.Windows.Services.Interop;

/// <summary>
/// Raw P/Invoke surface for the shared Swift <c>MuesliCoreABI</c> dynamic library. Kept internal and
/// isolated: nothing outside this folder should see native pointers, JSON transport or error codes.
/// </summary>
internal static class SwiftDictationCoreNative
{
    internal const string LibraryName = "MuesliCoreABI";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint muesli_core_bridge_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int muesli_core_bridge_open(byte[] pathUtf8, int pathLength, out IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void muesli_core_bridge_close(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int muesli_core_bridge_insert_dictation(
        SwiftDictationStoreHandle handle, byte[] requestUtf8, int requestLength, out long id);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int muesli_core_bridge_recent_dictations(
        SwiftDictationStoreHandle handle, int limit, byte[]? output, int outputCapacity, out int written);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int muesli_core_bridge_last_error(byte[]? output, int outputCapacity, out int written);
}

/// <summary>
/// Deterministic lifetime for the opaque Swift store handle. The native side owns the store; this
/// type guarantees <c>close</c> runs exactly once even if a caller forgets.
/// </summary>
internal sealed class SwiftDictationStoreHandle : SafeHandle
{
    public SwiftDictationStoreHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    internal void Set(IntPtr value) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        SwiftDictationCoreNative.muesli_core_bridge_close(handle);
        return true;
    }
}
