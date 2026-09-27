namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// FeatureRuntime-facing history contract. The production composition root selects the JSON
/// adapter while the L27 gate is off and the SQLite adapter after a verified cutover.
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

    void AppendDictation(PersistedDictation dictation);

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

/// <summary>Explicit folder lifecycle operations shared by JSON and SQLite history modes.</summary>
public interface ILibraryFolderAdapter
{
    void MoveMeetingFolder(string id, string? newParentId);

    bool DeleteMeetingFolder(string id);
}
