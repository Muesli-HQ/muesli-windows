namespace Muesli.Windows.Services.Persistence;

/// <summary>Test and support options for <see cref="PersistenceCutover"/>. Production uses defaults.</summary>
public sealed record PersistenceCutoverOptions
{
    /// <summary>
    /// Copies JSON aside, then returns <see cref="JsonMigrationOutcome.Failed"/> without opening
    /// SQLite. Proves a forced failure retains the original JSON files.
    /// </summary>
    public bool FailAfterJsonSnapshot { get; init; }

    /// <summary>
    /// Test hook invoked after each source file has been copied into the protected staging
    /// directory. It is intentionally not used by production composition. A hook can mutate the
    /// legacy profile between files so the capture validation path is exercised deterministically.
    /// </summary>
    internal Action<string>? AfterCaptureFile { get; init; }

    /// <summary>Test-only notification of the profile-local staging directory.</summary>
    internal Action<string>? CaptureDirectoryCreated { get; init; }

    /// <summary>Test-only pause immediately before the SQLite authority transaction opens.</summary>
    internal Action? BeforeAuthorityCommit { get; init; }

    /// <summary>Maximum number of complete source captures attempted after a detected mutation.</summary>
    internal int CaptureAttemptCount { get; init; } = 3;
}
