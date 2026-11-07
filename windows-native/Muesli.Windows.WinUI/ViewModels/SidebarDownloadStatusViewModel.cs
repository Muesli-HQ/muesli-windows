using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;
using Muesli.Windows.WinUI.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>
/// The sidebar's download row (macOS DownloadStatusRow): visible from every page while a model
/// downloads, then briefly with the outcome. It reads only the model that changed, and lifecycle
/// events are already throttled to 250 ms, so it costs one snapshot per event.
/// </summary>
public sealed partial class SidebarDownloadStatusViewModel : ObservableObject
{
    private readonly Func<WinUiModelsContext?> _resolveModels;
    private readonly Func<IUiDispatcher?> _resolveDispatcher;
    private readonly ModelDownloadActivity _activity = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private WinUiModelsContext _models = null!;
    private IUiDispatcher _dispatcher = null!;
    private bool _started;

    /// <remarks>
    /// The shell page is constructed before the app finishes creating its services, so they are
    /// resolved when the row starts rather than captured here.
    /// </remarks>
    public SidebarDownloadStatusViewModel(Func<WinUiModelsContext?> models, Func<IUiDispatcher?> dispatcher)
    {
        _resolveModels = models;
        _resolveDispatcher = dispatcher;
    }

    /// <summary>Starts following the app-scoped lifecycle while the shell is on screen.</summary>
    public void Start()
    {
        if (_started || _resolveModels() is not { } models || _resolveDispatcher() is not { } dispatcher) return;
        _models = models;
        _dispatcher = dispatcher;
        _started = true;
        _models.ModelChanged += OnModelChanged;
        // A download may have started (from onboarding) or finished while the shell was away.
        foreach (var snapshot in _models.Snapshots())
        {
            _activity.Observe(snapshot, Now);
        }
        Apply();
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _models.ModelChanged -= OnModelChanged;
    }

    [ObservableProperty] public partial bool IsVisible { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial double ProgressValue { get; private set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; private set; }
    [ObservableProperty] public partial bool ShowRing { get; private set; }
    [ObservableProperty] public partial bool ShowCompleted { get; private set; }
    [ObservableProperty] public partial bool ShowFailed { get; private set; }
    [ObservableProperty] public partial bool ShowPaused { get; private set; }
    [ObservableProperty] public partial string AccessibleName { get; private set; } = "";

    private TimeSpan Now => Stopwatch.GetElapsedTime(_origin);

    // Lifecycle events arrive on worker threads; the activity and bindings are touched on the UI thread only.
    private void OnModelChanged(object? sender, string modelId) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (!_started || _models.TrySnapshot(modelId) is not { } snapshot) return;
            _activity.Observe(snapshot, Now);
            Apply();
        });

    private void Apply()
    {
        var state = _activity.Current(Now);
        IsVisible = state.Kind != ModelDownloadActivityKind.None;
        Title = state.Title;
        Detail = state.Detail;
        ShowRing = state.Kind == ModelDownloadActivityKind.Active;
        ShowCompleted = state.Kind == ModelDownloadActivityKind.Completed;
        ShowFailed = state.Kind == ModelDownloadActivityKind.Failed;
        ShowPaused = state.Kind == ModelDownloadActivityKind.Paused;
        IsIndeterminate = ShowRing && state.Fraction is null;
        ProgressValue = (state.Fraction ?? 0) * 100;
        AccessibleName = string.IsNullOrWhiteSpace(state.Detail)
            ? state.Title
            : $"{state.Title}. {state.Detail.TrimEnd('…', '.')}. Open Models.";

        if (_activity.NextExpiry(Now) is { } expiry) ScheduleExpiry(expiry);
    }

    // The outcome hides on a timer: nothing else changes, so no lifecycle event would re-read it.
    // Outcomes are rare (one per finished download), so an occasional duplicate timer is harmless.
    private void ScheduleExpiry(TimeSpan delay) =>
        _ = Task.Delay(delay + TimeSpan.FromMilliseconds(50)).ContinueWith(
            _ => _dispatcher.TryEnqueue(() =>
            {
                if (_started) Apply();
            }),
            TaskScheduler.Default);
}
