using System.Linq;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
    public int MeetingCount => Meetings.Count;
    public int VisibleMeetingCount => FilteredMeetings?.Cast<MeetingItem>().Count() ?? Meetings.Count;
    public string MeetingsChevron => _meetingsExpanded ? "⌄" : "›";
    public string MeetingSortLabel => _meetingSortNewestFirst ? "Newest first⌄" : "Oldest first⌄";
    public string CurrentMeetingFolderName => _selectedMeetingFolderId is null
        ? "All Meetings"
        : MeetingFolders.FirstOrDefault(folder => folder.Id == _selectedMeetingFolderId)?.Name ?? "All Meetings";
}
