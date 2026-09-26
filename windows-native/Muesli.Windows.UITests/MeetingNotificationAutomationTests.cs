using System.Windows.Automation;

namespace Muesli.Windows.UITests;

/// <summary>
/// Packaged qualification for the dedicated meeting-notification window. Uses the development-only
/// deterministic preview route, which never persists a fake meeting and never starts capture.
/// </summary>
[Collection("UiAutomation")]
[Trait(UiAutomationEnvironment.TraitName, UiAutomationEnvironment.TraitValue)]
public sealed class MeetingNotificationAutomationTests
{
    [WinUiAutomationFact]
    public void Preview_notification_shows_while_the_dashboard_stays_hidden() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchWithMeetingNotificationPreview("active", background: true);
        session.Run(() =>
        {
            // The shell attached here is the notification window itself because the dashboard was
            // launched hidden; this proves the notification does not depend on the dashboard.
            var title = session.RequireAutomationId("MeetingNotificationTitle", mustBeOnscreen: true);
            Assert.Equal("Meeting detected", title.Current.Name);
            var subtitle = session.RequireAutomationId("MeetingNotificationSubtitle");
            Assert.Contains("Google Meet", subtitle.Current.Name, StringComparison.Ordinal);
            var primary = session.RequireAutomationId("MeetingNotificationPrimaryAction");
            Assert.Equal("Start Transcribing", primary.Current.Name);
            var dismiss = session.RequireAutomationId("MeetingNotificationDismiss");
            Assert.Equal("Dismiss", dismiss.Current.Name);

            // The dashboard must remain hidden while the notification is shown.
            var navigation = session.TryFindAutomationId("MainNavigation", TimeSpan.FromSeconds(2));
            if (navigation is not null)
            {
                Assert.True(navigation.Current.IsOffscreen,
                    "the dashboard must remain hidden while the notification is shown");
            }
        });
    });

    [WinUiAutomationFact]
    public void Scheduled_preview_shows_the_split_action_and_chevron() => StaRunner.Run(() =>
    {
        using var session = MuesliWinUiSession.LaunchWithMeetingNotificationPreview("scheduled");
        session.Run(() =>
        {
            var window = session.RequireSecondaryWindow("MeetingNotificationWindow");
            var primary = Find(window, "MeetingNotificationPrimaryAction");
            Assert.Equal("Join & Transcribe", primary.Current.Name);
            var chevron = Find(window, "MeetingNotificationChevron");
            Assert.True(chevron.Current.IsEnabled);
            session.CaptureSecondaryWindow(window, "meeting-notification-scheduled-preview");
        });
    });

    private static AutomationElement Find(AutomationElement window, string automationId)
    {
        var element = window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        Assert.NotNull(element);
        return element!;
    }
}
