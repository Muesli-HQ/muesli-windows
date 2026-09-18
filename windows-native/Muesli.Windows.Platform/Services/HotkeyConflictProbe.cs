using System.Runtime.InteropServices;
using Muesli.Windows.Core.Contracts;

namespace Muesli.Windows.Services;

/// <summary>Result of probing a candidate registration and restoring the active hook.</summary>
public sealed record HotkeyCandidateHookResult(bool CandidateRegistered, bool PreviousHookRestored, Exception? RestorationException = null);

public static class HotkeyCandidateHookTest
{
    public static HotkeyCandidateHookResult Run(Func<bool> registerCandidate, Func<bool> restorePrevious)
    {
        var candidateRegistered = false;
        var previousHookRestored = false;
        Exception? restorationException = null;
        try
        {
            candidateRegistered = registerCandidate();
        }
        finally
        {
            try { previousHookRestored = restorePrevious(); }
            catch (Exception exception) { restorationException = exception; }
        }

        return new HotkeyCandidateHookResult(candidateRegistered, previousHookRestored, restorationException);
    }
}

public static class HotkeyConflictProbe
{
    public static bool IsAdvisoryRegistrationAvailable(string gesture)
    {
        var parsed = HotkeyGestureParser.Parse(gesture);
        var id = unchecked((int)(0x4D550000 | (Environment.TickCount & 0x7fff)));
        try
        {
            return RegisterHotKey(IntPtr.Zero, id, (uint)parsed.Modifiers, (uint)parsed.VirtualKey);
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
