namespace Muesli.Windows.Tests;

/// <summary>
/// Skips a test that exercises the staged shared Swift bridge (<c>MuesliCoreABI.dll</c>) instead of
/// failing when no bridge is staged. Windows release builds may intentionally ship the
/// parity-tested managed text processor (see <c>windows-native/shared-core.lock.json</c>), so the
/// optional native component is a named prerequisite, not an unconditional gate.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BridgeFactAttribute : FactAttribute
{
    public BridgeFactAttribute()
    {
        var bridge = Path.Combine(AppContext.BaseDirectory, "MuesliCoreABI.dll");
        if (!File.Exists(bridge))
        {
            Skip = "The shared Swift bridge (MuesliCoreABI.dll) is not staged; the release ships the managed-fallback text processor.";
        }
    }
}
