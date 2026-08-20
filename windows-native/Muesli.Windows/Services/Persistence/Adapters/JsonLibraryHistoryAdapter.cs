namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Adapter for the pre-cutover JSON history. The production composition root uses this adapter
/// while the L27 gate is disabled, so FeatureRuntime has one history contract in either mode.
/// </summary>
public sealed class JsonLibraryHistoryAdapter : ILibraryHistoryAdapter
{
    public JsonLibraryHistoryAdapter(AppDataStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public AppDataStore Store { get; }

    public string? LastWarning => Store.LastWarning;

    public IReadOnlyList<PersistedDictation> LoadDictations() => Store.LoadDictations();

    public void SaveDictations(IEnumerable<PersistedDictation> dictations, bool afterExplicitDeletion = false)
    {
        if (afterExplicitDeletion)
        {
            Store.SaveDictationsAfterDeletion(dictations);
        }
        else
        {
            Store.SaveDictations(dictations);
        }
    }

    public IReadOnlyList<PersistedMeeting> LoadMeetings() => Store.LoadMeetings();

    public void SaveMeetings(IEnumerable<PersistedMeeting> meetings, bool afterExplicitDeletion = false)
    {
        if (afterExplicitDeletion)
        {
            Store.SaveMeetingsAfterDeletion(meetings);
        }
        else
        {
            Store.SaveMeetings(meetings);
        }
    }

    public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() => Store.LoadMeetingFolders();

    public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> folders) => Store.SaveMeetingFolders(folders);

    public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() => Store.LoadDictionary();

    public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries) => Store.SaveDictionary(entries);

    public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() => Store.LoadMeetingTemplates();

    public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> templates) => Store.SaveMeetingTemplates(templates);
}
