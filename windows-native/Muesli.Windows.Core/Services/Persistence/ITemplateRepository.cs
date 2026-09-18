namespace Muesli.Windows.Services.Persistence;

public interface ITemplateRepository
{
    TemplateRecord? Find(string id);

    /// <summary>Templates in presentation order: explicit sort order, then name.</summary>
    IReadOnlyList<TemplateRecord> List();

    TemplateRecord? FindByName(string name);

    void Upsert(TemplateRecord template);

    void UpsertRange(IEnumerable<TemplateRecord> templates);

    bool Delete(string id);

    int Count();
}
