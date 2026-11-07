using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Muesli.Windows.WinUI.Indicator;

/// <summary>
/// A system backdrop that paints nothing, so a borderless window shows only its XAML content with
/// per-pixel alpha. The floating indicator uses it to draw an anti-aliased, translucent pill
/// instead of a rectangular window clipped by a region.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static global::Windows.UI.Composition.Compositor? s_compositor;
    private static IntPtr s_dispatcherQueueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureSystemDispatcherQueue();
        s_compositor ??= new global::Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = s_compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    /// <summary>
    /// The system compositor needs a Windows.System dispatcher queue on this thread; WinUI threads
    /// only have the Microsoft.UI one, so create it once.
    /// </summary>
    private static void EnsureSystemDispatcherQueue()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null || s_dispatcherQueueController != IntPtr.Zero)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,    // DQTYPE_THREAD_CURRENT
            ApartmentType = 0  // DQTAT_COM_NONE
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out s_dispatcherQueueController));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr dispatcherQueueController);
}
