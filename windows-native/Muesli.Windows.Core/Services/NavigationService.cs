namespace Muesli.Windows.Services;

public enum AppPage
{
    Dashboard,
    Dictations = Dashboard,
    Search,
    Meetings,
    MeetingDetail,
    Dictionary,
    Models,
    Shortcuts,
    Settings,
    About
}

public sealed record NavigationState(
    AppPage CurrentPage,
    AppPage LastContentPage,
    string? SelectedMeetingId,
    bool MeetingsExpanded,
    string SettingsTab);

public sealed class NavigationChangedEventArgs(NavigationState state) : EventArgs
{
    public NavigationState State { get; } = state;
}

public interface INavigationService
{
    NavigationState State { get; }
    event EventHandler<NavigationChangedEventArgs>? Changed;
    void Navigate(AppPage page);
    void OpenMeetingDetail(string meetingId);
    void ReturnToMeetings();
    void SetMeetingsExpanded(bool expanded);
    void SetSettingsTab(string tab);
}

/// <summary>
/// Pure navigation state machine. WPF element visibility and focus remain the shell's job.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private NavigationState _state = new(AppPage.Dashboard, AppPage.Dashboard, null, true, "General");

    public NavigationState State => _state;

    public event EventHandler<NavigationChangedEventArgs>? Changed;

    public void Navigate(AppPage page)
    {
        var lastContentPage = page == AppPage.Search || page == AppPage.MeetingDetail
            ? _state.LastContentPage
            : page;
        SetState(_state with { CurrentPage = page, LastContentPage = lastContentPage });
    }

    public void OpenMeetingDetail(string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
            throw new ArgumentException("A meeting id is required.", nameof(meetingId));

        SetState(_state with
        {
            CurrentPage = AppPage.MeetingDetail,
            SelectedMeetingId = meetingId,
            LastContentPage = AppPage.Meetings
        });
    }

    public void ReturnToMeetings() =>
        SetState(_state with { CurrentPage = AppPage.Meetings, LastContentPage = AppPage.Meetings, SelectedMeetingId = null });

    public void SetMeetingsExpanded(bool expanded) =>
        SetState(_state with { MeetingsExpanded = expanded });

    public void SetSettingsTab(string tab)
    {
        if (string.IsNullOrWhiteSpace(tab))
        {
            throw new ArgumentException("A settings tab is required.", nameof(tab));
        }

        SetState(_state with { SettingsTab = tab.Trim() });
    }

    private void SetState(NavigationState state)
    {
        if (state == _state)
            return;

        _state = state;
        Changed?.Invoke(this, new NavigationChangedEventArgs(_state));
    }
}
