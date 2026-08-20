namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// L27 production gate. Default is off: JSON <see cref="AppDataStore"/> remains the running app's
/// history until Agent E wires <see cref="PersistenceCutover"/> in
/// <c>FeatureServiceScope.CreateProduction</c>. This type is not referenced from locked runtime files.
/// </summary>
public static class PersistenceCutoverGate
{
    /// <summary>Stable flag name for settings/env binding when Agent E wires the cutover.</summary>
    public const string FeatureFlagName = "L27SqliteHistoryCutover";

    public const string EnvironmentVariableName = "MUESLI_SQLITE_HISTORY_CUTOVER";

    /// <summary>Production default. Do not flip this to true from the data lane.</summary>
    public const bool DefaultEnabled = false;

    /// <summary>
    /// Reads the process-scoped qualification switch. Only the exact value <c>1</c> enables the
    /// cutover; all other values, including a missing variable, retain the safe JSON default.
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            "1",
            StringComparison.Ordinal);
}
