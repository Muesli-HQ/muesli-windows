using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>
/// State for the in-progress meeting's own page: its title, what is being captured, the written
/// notes, the live transcript and the recording controls. The Meetings list no longer hosts any of
/// this; it only links here while a recording is active.
/// </summary>
public partial class MeetingRecordingViewModel : ObservableObject, IDisposable
{
    private readonly WinUiLibraryContext _library;
    private readonly WinUiMeetingContext _meetingRuntime;
    private readonly IAppDialogService _dialogs;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _showLiveTranscript;
    private string? _notesMeetingId;
    private string? _lastMeetingId;
    private bool _loadingNotes;
    private bool _wasActive;
    private CancellationTokenSource? _notesDebounce;

    public MeetingRecordingViewModel(
        WinUiLibraryContext library,
        WinUiMeetingContext meetingRuntime,
        IAppDialogService dialogs,
        IUiDispatcher dispatcher,
        Action showLiveTranscript)
    {
        _library = library;
        _meetingRuntime = meetingRuntime;
        _dialogs = dialogs;
        _dispatcher = dispatcher;
        _showLiveTranscript = showLiveTranscript;
        _meetingRuntime.Changed += OnMeetingChanged;
        UpdateRuntimeState();
    }

    /// <summary>
    /// The recording is over. Carries the saved meeting's id, or null when nothing was kept
    /// (discarded, or the start failed), so the page can open the meeting or return to the list.
    /// </summary>
    public event EventHandler<string?>? Finished;

    private void OnMeetingChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(UpdateRuntimeState);

    public void Dispose()
    {
        _meetingRuntime.Changed -= OnMeetingChanged;
        _notesDebounce?.Cancel();
    }

    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial bool IsRecording { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsPaused { get; private set; }
    [ObservableProperty] public partial string CaptureStatus { get; private set; } = "";
    [ObservableProperty] public partial string Notice { get; private set; } = "";
    [ObservableProperty] public partial string LiveTranscript { get; private set; } = "";
    [ObservableProperty] public partial string PartialYou { get; private set; } = "";
    [ObservableProperty] public partial string PartialOthers { get; private set; } = "";
    [ObservableProperty] public partial string ManualNotes { get; set; } = "";
    [ObservableProperty] public partial string NotesSaveStatus { get; private set; } = "Saved";
    [ObservableProperty] public partial bool ShowLiveTab { get; set; }
    [ObservableProperty] public partial bool IsSourceMissing { get; private set; }

    public bool IsActive => IsRecording || IsBusy || IsPaused;
    public bool CanStop => (IsRecording || IsPaused) && !IsBusy;
    public bool CanPause => IsRecording && !IsBusy;
    public bool CanResume => IsPaused && !IsBusy;
    public bool ShowRecordingNotes => !ShowLiveTab;
    public bool CanEditRecordingNotes => _meetingRuntime.ActiveMeetingId is not null && CanStop;
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public DateTime StartedAt => _meetingRuntime.StartedAt;

    /// <summary>The short state word in front of the elapsed time: Recording, Paused, Saving….</summary>
    public string StateLabel =>
        IsRecording ? "Recording"
        : IsPaused ? (_meetingRuntime.FinalizationFailed ? "Not saved yet" : "Paused")
        : _meetingRuntime.IsStarting ? "Starting"
        : IsBusy ? (_meetingRuntime.ProcessingStage.Length > 0 ? _meetingRuntime.ProcessingStage : "Saving")
        : "";

    partial void OnShowLiveTabChanged(bool value) => OnPropertyChanged(nameof(ShowRecordingNotes));
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    // Typing updates the in-memory draft (which Stop and discard already persist) and writes to
    // disk only after a pause.
    partial void OnManualNotesChanged(string value)
    {
        if (_loadingNotes || !CanEditRecordingNotes) return;
        _meetingRuntime.UpdateManualNotesDraft(value);
        NotesSaveStatus = "Saving…";
        _notesDebounce?.Cancel();
        _notesDebounce = new CancellationTokenSource();
        _ = SaveNotesAfterPauseAsync(_notesDebounce.Token);
    }

    private async Task SaveNotesAfterPauseAsync(CancellationToken token)
    {
        try { await Task.Delay(700, token); }
        catch (OperationCanceledException) { return; }
        if (CanEditRecordingNotes) SaveRecordingNotes();
    }

    [RelayCommand(CanExecute = nameof(CanEditRecordingNotes))]
    private void SaveRecordingNotes()
    {
        try
        {
            _meetingRuntime.SaveManualNotes(ManualNotes);
            NotesSaveStatus = "Saved";
        }
        catch (Exception exception) { NotesSaveStatus = $"Could not save notes: {exception.Message}"; }
    }

    [RelayCommand] private void KeepRecording() => _meetingRuntime.KeepRecording();

    [RelayCommand] private void ShowLiveTranscript() => _showLiveTranscript();

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopRecordingAsync(CancellationToken cancellationToken)
    {
        try
        {
            Notice = "";
            // Leaving the page is driven by the runtime going idle (UpdateRuntimeState), so a stop
            // from the floating pill or the tray ends up in the same place.
            await _meetingRuntime.StopAndSaveAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Notice = "Saving was cancelled. The recording is kept and can be recovered from Meetings.";
        }
        catch (Exception exception)
        {
            Notice = $"Could not save the meeting: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task DiscardRecordingAsync()
    {
        try
        {
            await _meetingRuntime.ConfirmAndDiscardAsync(_dialogs, !string.IsNullOrWhiteSpace(ManualNotes));
        }
        catch (Exception exception)
        {
            Notice = $"Could not discard the recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private async Task PauseRecordingAsync()
    {
        try
        {
            Notice = "";
            await _meetingRuntime.PauseAsync();
        }
        catch (Exception exception)
        {
            Notice = $"Could not pause the recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeRecordingAsync()
    {
        try
        {
            Notice = "";
            await _meetingRuntime.ResumeAsync();
        }
        catch (Exception exception)
        {
            Notice = $"Could not resume the recording: {exception.Message}";
        }
        finally
        {
            UpdateRuntimeState();
        }
    }

    private void UpdateRuntimeState()
    {
        IsRecording = _meetingRuntime.IsRecording;
        IsBusy = _meetingRuntime.IsBusy;
        IsPaused = _meetingRuntime.IsPaused;
        if (IsActive)
        {
            Title = string.IsNullOrWhiteSpace(_meetingRuntime.ActiveTitle) ? "Meeting" : _meetingRuntime.ActiveTitle;
            CaptureStatus = _meetingRuntime.FinalizationFailed
                ? "Saving failed. The recording is kept. Choose Stop to try again."
                : _meetingRuntime.CaptureStatus;
        }
        LiveTranscript = _meetingRuntime.LiveTranscript;
        IsSourceMissing = _meetingRuntime.IsSourceMissing;
        if (_meetingRuntime.ActiveMeetingId is { } id)
        {
            _lastMeetingId = id;
            if (id != _notesMeetingId)
            {
                _notesMeetingId = id;
                _loadingNotes = true;
                ManualNotes = _meetingRuntime.ManualNotes;
                _loadingNotes = false;
                NotesSaveStatus = _meetingRuntime.AreNotesSaved ? "Saved" : "Notes have not been saved. Retry saving.";
                ShowLiveTab = false;
            }
        }
        var snapshot = _meetingRuntime.LatestLiveSnapshot;
        PartialYou = snapshot?.PartialMicrophone ?? "";
        PartialOthers = snapshot?.PartialSystem ?? "";

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanEditRecordingNotes));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StartedAt));
        SaveRecordingNotesCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        DiscardRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();

        var active = IsActive;
        if (_wasActive && !active)
        {
            var saved = _lastMeetingId is { } last && _library.FindMeeting(last) is not null ? last : null;
            Finished?.Invoke(this, saved);
        }
        _wasActive = active;
    }
}
