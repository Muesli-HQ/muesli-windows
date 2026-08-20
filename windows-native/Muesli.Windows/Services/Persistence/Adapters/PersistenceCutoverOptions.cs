namespace Muesli.Windows.Services.Persistence;

/// <summary>Test and support options for <see cref="PersistenceCutover"/>. Production uses defaults.</summary>
public sealed record PersistenceCutoverOptions
{
    /// <summary>
    /// Copies JSON aside, then returns <see cref="JsonMigrationOutcome.Failed"/> without opening
    /// SQLite. Proves a forced failure retains the original JSON files.
    /// </summary>
    public bool FailAfterJsonSnapshot { get; init; }
}
