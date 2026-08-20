namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// FeatureRuntime-facing history contract. Wave 1 keeps this unwired: the running app still talks
/// to <see cref="AppDataStore"/> through locked runtime files. L27 supplies
/// <see cref="SqliteLibraryHistoryAdapter"/>; Agent E (integration) is the only owner allowed to swap
/// <c>AppServices</c> / <c>FeatureRuntime</c> onto it after <see cref="PersistenceCutoverGate"/> is enabled.
/// </summary>
/// <remarks>
/// Dictionary entries have no SQLite repository today. A repository-backed adapter must keep
/// serving dictionary reads and writes (delegating to JSON until a dictionary table exists) so
/// FeatureRuntime does not grow a second store path.
/// </remarks>
public interface ILibraryHistoryAdapter
{
    string? LastWarning { get; }

    IReadOnlyList<PersistedDictation> LoadDictations();

    void SaveDictations(IEnumerable<PersistedDictation> dictations, bool afterExplicitDeletion = false);

    IReadOnlyList<PersistedMeeting> LoadMeetings();

    void SaveMeetings(IEnumerable<PersistedMeeting> meetings, bool afterExplicitDeletion = false);

    IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders();

    void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders);

    IReadOnlyList<DictionaryEntryRecord> LoadDictionary();

    void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries);

    IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates();

    void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates);
}
