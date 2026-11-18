namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Compatibility names for older qualification callers. SQLite is always the production store.
/// </summary>
public static class PersistenceCutoverGate
{
    /// <summary>Stable flag name for settings/env binding when the cutover is qualified.</summary>
    public const string FeatureFlagName = "L27SqliteHistoryCutover";

    public const string EnvironmentVariableName = "MUESLI_SQLITE_HISTORY_CUTOVER";

    /// <summary>Production default; no environment switch can select legacy JSON history.</summary>
    public const bool DefaultEnabled = true;

    /// <summary>
    /// Retained for callers compiled against the former gate. Always enabled.
    /// </summary>
    public static bool IsEnabled => true;
}
