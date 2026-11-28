using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingCompletedNotificationTests
{
    [Fact]
    public void TheCompletedMeetingPanelOffersViewNotesOnly()
    {
        var request = new MeetingNotificationRequest("meeting-completed:1", MeetingNotificationKind.MeetingCompleted,
            "Transcription complete", "Weekly sync", "Muesli", "", "#34C759", "", "View Notes", null);

        Assert.Equal(MeetingNotificationAction.ViewNotes, request.SingleAction);
        Assert.False(request.HasSplitAction);
    }
}
