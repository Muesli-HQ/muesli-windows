namespace Muesli.Windows.Services;

/// <summary>
/// Shell-facing product snapshot. The records are platform-neutral; startup registration is
/// supplied by the host so WinUI and WPF can report truthful platform state independently.
/// </summary>
public sealed record ProductExperienceState(
    bool SetupIncomplete,
    string SetupLabel,
    StartupRegistrationState Startup,
    IReadOnlyList<ProductHistoryEntry> RecentDictations,
    IReadOnlyList<ProductHistoryEntry> RecentMeetings,
    string DetectedNow,
    bool MeetingRecording = false,
    bool MeetingPaused = false);

public sealed record ProductHistoryEntry(string Title, DateTimeOffset Timestamp);

public sealed record StartupRegistrationState(bool Enabled, bool BackgroundRegistration, string Label);
