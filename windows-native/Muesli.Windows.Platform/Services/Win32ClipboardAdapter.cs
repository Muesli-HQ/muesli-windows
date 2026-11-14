using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Muesli.Windows.Services;

/// <summary>
/// Clipboard adapter for active-app delivery. Dictation pastes from a background thread while
/// another app has focus, where the WinRT clipboard refuses access; the Win32 clipboard has no
/// such restriction. The snapshot keeps every memory-backed format, so text, rich text, images
/// (as DIB) and file lists survive the paste.
/// </summary>
public sealed class Win32ClipboardAdapter : IClipboardAdapter
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    // Tells clipboard history and cloud clipboard to ignore a transcript that is only in transit.
    private static readonly uint ExcludeFromHistoryFormat =
        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    public Task<ClipboardSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var formats = new List<(uint Format, byte[] Data)>();
        Open();
        try
        {
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                if (!IsMemoryBacked(format)) continue;
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                var size = (long)GlobalSize(handle);
                if (size is <= 0 or > int.MaxValue) continue;
                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(pointer, data, 0, data.Length);
                    formats.Add((format, data));
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }

        return Task.FromResult(new ClipboardSnapshot(formats));
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            Put(CfUnicodeText, System.Text.Encoding.Unicode.GetBytes((text ?? string.Empty) + '\0'));
            if (ExcludeFromHistoryFormat != 0) Put(ExcludeFromHistoryFormat, new byte[4]);
        }
        finally
        {
            CloseClipboard();
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();
        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero) return Task.FromResult<string?>(null);
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return Task.FromResult<string?>(null);
            try
            {
                return Task.FromResult(Marshal.PtrToStringUni(pointer));
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public Task RestoreAsync(ClipboardSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.Data is not List<(uint Format, byte[] Data)> formats)
        {
            throw new ArgumentException("The clipboard snapshot was created by another adapter.", nameof(snapshot));
        }

        Open();
        try
        {
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            foreach (var (format, data) in formats)
            {
                Put(format, data);
            }
        }
        finally
        {
            CloseClipboard();
        }

        return Task.CompletedTask;
    }

    // GDI and private handles cannot be copied as bytes. Images still survive through CF_DIB,
    // from which Windows synthesizes CF_BITMAP again.
    private static bool IsMemoryBacked(uint format) => format switch
    {
        2 or 3 or 9 or 14 => false,            // CF_BITMAP, CF_METAFILEPICT, CF_PALETTE, CF_ENHMETAFILE
        >= 0x0080 and <= 0x008E => false,      // owner-display and CF_DSP* formats
        >= 0x0200 and <= 0x03FF => false,      // CF_PRIVATEFIRST..CF_GDIOBJLAST
        _ => true
    };

    private static void Put(uint format, byte[] data)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)Math.Max(1, data.Length));
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(handle);
        // The clipboard owns the memory only once SetClipboardData succeeds.
        if (SetClipboardData(format, handle) == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            GlobalFree(handle);
            throw new Win32Exception(error);
        }
    }

    private static void Open()
    {
        // Another process may hold the clipboard for a few milliseconds.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(5);
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard is in use by another app.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);
}
