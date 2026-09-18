using System.Windows.Controls;
using Muesli.Windows.Features;

namespace Muesli.Windows.Features.Meetings;

public partial class MeetingDetailView : FeatureViewBase
{
    public MeetingDetailView()
    {
        InitializeComponent();
    }

    public Grid PageRoot => MeetingDetailRoot;
    public Border MeetingNotesTabControl => MeetingNotesTab;
    public TextBlock MeetingNotesTabLabelControl => MeetingNotesTabLabel;
    public Border MeetingTranscriptTabControl => MeetingTranscriptTab;
    public TextBlock MeetingTranscriptTabLabelControl => MeetingTranscriptTabLabel;
    public StackPanel MeetingWarningsPanelControl => MeetingWarningsPanel;
    public ItemsControl MeetingWarningsItemsControl => MeetingWarningsItems;
    public StackPanel MeetingNotesPanelControl => MeetingNotesPanel;
    public StackPanel MeetingNotesContentControl => MeetingNotesContent;
    public StackPanel MeetingTranscriptPanelControl => MeetingTranscriptPanel;
    public StackPanel SpeakerAliasPanelControl => SpeakerAliasPanel;
}
