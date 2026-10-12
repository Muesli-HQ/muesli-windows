using System.Runtime.InteropServices;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class GlobalHotkeyThreadTests
{
    // Requires an interactive desktop because this exercises the real Windows hook with F24.
    [QualificationFact("MUESLI_HOTKEY_QUALIFICATION")]
    public void ShortcutStillArrivesWhenTheRegisteringThreadIsBlocked()
    {
        using var registered = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        using var down = new ManualResetEventSlim();
        using var up = new ManualResetEventSlim();
        Exception? registrationFailure = null;
        var owner = new Thread(() =>
        {
            using var hook = new GlobalHotkeyService();
            try
            {
                hook.Register("F24", () => down.Set(), () => up.Set());
                registered.Set();
                // Deliberately do not pump the registering thread's messages, like a stalled UI.
                releaseOwner.Wait();
            }
            catch (Exception exception)
            {
                registrationFailure = exception;
                registered.Set();
            }
        }) { IsBackground = true };
        owner.Start();
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(10)), "Hook registration timed out.");
            Assert.Null(registrationFailure);
            keybd_event(0x87 /* F24 */, 0, 0, UIntPtr.Zero);
            Assert.True(down.Wait(TimeSpan.FromSeconds(5)), "Blocked owner lost shortcut down.");
            keybd_event(0x87, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
            Assert.True(up.Wait(TimeSpan.FromSeconds(5)), "Blocked owner lost shortcut up.");
        }
        finally
        {
            keybd_event(0x87, 0, 2, UIntPtr.Zero);
            releaseOwner.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)), "Hook disposal did not finish.");
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
