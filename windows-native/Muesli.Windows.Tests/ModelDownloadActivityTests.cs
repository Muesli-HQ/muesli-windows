using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class ModelDownloadActivityTests
{
    private static readonly TranscriptionModelDefinition First = TranscriptionModelCatalog.Models[0];
    private static readonly TranscriptionModelDefinition Second = TranscriptionModelCatalog.Models[1];

    private static ModelDownloadSnapshot Transfer(
        TranscriptionModelDefinition model,
        long done,
        long? total,
        ModelDownloadPhase phase = ModelDownloadPhase.Downloading,
        TimeSpan? left = null) =>
        new(model.Id, phase, "model.onnx", done, total, 0, 1, 1_000_000, left, 0, "");

    private static TranscriptionModelSnapshot Snapshot(
        TranscriptionModelDefinition model,
        TranscriptionModelStatus status,
        bool busy,
        ModelDownloadSnapshot? download = null,
        string text = "") =>
        new(model, status, text, 0, "0 B", "", busy, !busy, busy, false, false, false) { Download = download };

    [Fact]
    public void Nothing_is_shown_before_any_download()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Ready, busy: false), TimeSpan.Zero);

        Assert.Equal(ModelDownloadActivityKind.None, activity.Current(TimeSpan.Zero).Kind);
    }

    [Fact]
    public void A_single_download_shows_percent_and_time_left()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true,
            Transfer(First, 250, 1000, left: TimeSpan.FromSeconds(26))), TimeSpan.Zero);

        var state = activity.Current(TimeSpan.Zero);
        Assert.Equal(ModelDownloadActivityKind.Active, state.Kind);
        Assert.Equal($"Downloading {First.DisplayName}", state.Title);
        Assert.Equal("25% · ~26s left", state.Detail);
        Assert.Equal(0.25, state.Fraction);
    }

    [Fact]
    public void Preparing_has_no_fraction()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true), TimeSpan.Zero);

        var state = activity.Current(TimeSpan.Zero);
        Assert.Equal("Preparing…", state.Detail);
        Assert.Null(state.Fraction);
    }

    [Fact]
    public void After_the_transfer_it_reads_finishing_not_preparing()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true,
            Transfer(First, 1000, 1000, ModelDownloadPhase.Ready)), TimeSpan.Zero);

        Assert.Equal("Finishing…", activity.Current(TimeSpan.Zero).Detail);
    }

    [Fact]
    public async Task Resumed_bytes_do_not_count_as_speed()
    {
        var manifest = new ModelDownloadManifest("m", "v1",
            [new ModelDownloadFile("a.bin", new Uri("https://models.test/a.bin"), 1_000_000)]);
        var reports = new List<ModelDownloadSnapshot>();
        var tracker = new ModelDownloadCoordinator.ProgressTracker(manifest, new SyncProgress(reports.Add));
        tracker.UseFiles(manifest.Files);

        // The clock starts first; the saved part is found afterwards, as in a real resume.
        tracker.SetPhase(ModelDownloadPhase.Downloading, "");
        tracker.MarkPresent("a.bin", 900_000, completed: false);
        await Task.Delay(600);
        tracker.Add("a.bin", 10_000);
        tracker.SetPhase(ModelDownloadPhase.Downloading, "", force: true);

        var last = reports[^1];
        Assert.Equal(910_000, last.CompletedBytes);
        // At most 10 kB in ≥0.6 s; counting the saved bytes would report over 1.5 MB/s.
        Assert.InRange(last.BytesPerSecond, 1, 10_000 / 0.6);
        Assert.True(last.TimeRemaining > TimeSpan.FromSeconds(5));
    }

    private sealed class SyncProgress(Action<ModelDownloadSnapshot> report) : IProgress<ModelDownloadSnapshot>
    {
        public void Report(ModelDownloadSnapshot value) => report(value);
    }

    [Fact]
    public void Two_downloads_are_summed()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true, Transfer(First, 100, 400)), TimeSpan.Zero);
        activity.Observe(Snapshot(Second, TranscriptionModelStatus.Downloading, busy: true, Transfer(Second, 300, 600)), TimeSpan.Zero);

        var state = activity.Current(TimeSpan.Zero);
        Assert.Equal("Downloading 2 models", state.Title);
        Assert.Equal("40%", state.Detail);
        Assert.Equal(0.4, state.Fraction!.Value, 3);
    }

    [Fact]
    public void Success_is_held_for_five_seconds_then_hidden()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true, Transfer(First, 900, 1000)), TimeSpan.Zero);
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Ready, busy: false), TimeSpan.FromSeconds(10));

        var held = activity.Current(TimeSpan.FromSeconds(14));
        Assert.Equal(ModelDownloadActivityKind.Completed, held.Kind);
        Assert.Equal($"{First.DisplayName} downloaded", held.Title);
        Assert.Equal(1d, held.Fraction);
        Assert.Equal(TimeSpan.FromSeconds(1), activity.NextExpiry(TimeSpan.FromSeconds(14)));
        Assert.Equal(ModelDownloadActivityKind.None, activity.Current(TimeSpan.FromSeconds(15)).Kind);
    }

    [Fact]
    public void Failure_is_held_for_twelve_seconds_with_its_message()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true, Transfer(First, 1, 10)), TimeSpan.Zero);
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Failed, busy: false, text: "Not enough disk space"), TimeSpan.Zero);

        var state = activity.Current(TimeSpan.FromSeconds(11));
        Assert.Equal(ModelDownloadActivityKind.Failed, state.Kind);
        Assert.Equal("Not enough disk space", state.Detail);
        Assert.Equal(ModelDownloadActivityKind.None, activity.Current(TimeSpan.FromSeconds(12)).Kind);
    }

    [Fact]
    public void Pause_keeps_the_saved_fraction_briefly()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true, Transfer(First, 300, 1000)), TimeSpan.Zero);
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Missing, busy: false,
            Transfer(First, 300, 1000, ModelDownloadPhase.Paused)), TimeSpan.Zero);

        var state = activity.Current(TimeSpan.FromSeconds(1));
        Assert.Equal(ModelDownloadActivityKind.Paused, state.Kind);
        Assert.Equal(0.3, state.Fraction);
    }

    [Fact]
    public void Deleting_and_verifying_alone_are_not_downloads()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Verifying, busy: true), TimeSpan.Zero);
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Missing, busy: false), TimeSpan.Zero);

        Assert.Equal(ModelDownloadActivityKind.None, activity.Current(TimeSpan.Zero).Kind);
        Assert.Null(activity.NextExpiry(TimeSpan.Zero));
    }

    [Fact]
    public void An_active_download_wins_over_an_earlier_outcome()
    {
        var activity = new ModelDownloadActivity();
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Downloading, busy: true, Transfer(First, 1, 2)), TimeSpan.Zero);
        activity.Observe(Snapshot(First, TranscriptionModelStatus.Ready, busy: false), TimeSpan.Zero);
        activity.Observe(Snapshot(Second, TranscriptionModelStatus.Downloading, busy: true, Transfer(Second, 1, 4)), TimeSpan.FromSeconds(1));

        var state = activity.Current(TimeSpan.FromSeconds(1));
        Assert.Equal(ModelDownloadActivityKind.Active, state.Kind);
        Assert.Equal($"Downloading {Second.DisplayName}", state.Title);
    }
}
