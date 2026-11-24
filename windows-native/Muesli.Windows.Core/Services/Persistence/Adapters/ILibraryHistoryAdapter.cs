namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Library history contract. Production always selects SQLite after verified initialization.
/// </summary>
/// <remarks>
/// <see cref="SqliteLibraryHistoryAdapter"/> is the only implementation.
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

/// <summary>Explicit folder lifecycle operations of the library.</summary>
public interface ILibraryFolderAdapter
{
    void MoveMeetingFolder(string id, string? newParentId);

    bool DeleteMeetingFolder(string id);
}

