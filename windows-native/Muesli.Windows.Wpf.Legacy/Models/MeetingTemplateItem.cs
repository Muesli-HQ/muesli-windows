using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed class MeetingTemplateItem
{
    public MeetingTemplateItem(PersistedMeetingTemplate record)
    {
        Id = string.IsNullOrWhiteSpace(record.Id) ? $"template_{Guid.NewGuid():N}" : record.Id;
        Name = record.Name;
        Prompt = record.Prompt;
        Icon = string.IsNullOrWhiteSpace(record.Icon) ? "square.and.pencil" : record.Icon;
    }

    public MeetingTemplateItem(string name, string prompt, string icon = "square.and.pencil")
    {
        Id = $"template_{Guid.NewGuid():N}";
        Name = name;
        Prompt = prompt;
        Icon = icon;
    }

    public string Id { get; }
    public string Name { get; set; }
    public string Prompt { get; set; }
    public string Icon { get; set; }

    public PersistedMeetingTemplate Record => new()
    {
        Id = Id,
        Name = Name,
        Prompt = Prompt,
        Icon = Icon
    };
}
