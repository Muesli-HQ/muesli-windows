using System.Globalization;

namespace Muesli.Windows.Services;

public enum ModelDownloadActivityKind
{
    None,
    Active,
    Completed,
    Paused,
    Failed
}

/// <summary>What the sidebar download row shows. <see cref="Fraction"/> is null when the size is unknown.</summary>
public sealed record ModelDownloadActivityState(
    ModelDownloadActivityKind Kind,
    string Title,
    string Detail,
    double? Fraction)
{
    public static readonly ModelDownloadActivityState None = new(ModelDownloadActivityKind.None, "", "", null);
}

/// <summary>
/// The macOS sidebar download row: one line for whatever is downloading, from any page, and a short
/// outcome afterwards (5 s for success or pause, 12 s for a failure) before it hides. Fed from the
/// app-scoped model lifecycle; time is passed in so the holds are testable.
/// </summary>
public sealed class ModelDownloadActivity
{
    public static readonly TimeSpan CompletedHold = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FailedHold = TimeSpan.FromSeconds(12);

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public required string Name;
        public bool Active;
        public bool Verifying;
        public ModelDownloadSnapshot? Download;
        public ModelDownloadActivityKind Outcome;
        public string OutcomeDetail = "";
        public TimeSpan FinishedAt;
    }

    /// <summary>Records the latest lifecycle state of one model.</summary>
    public void Observe(TranscriptionModelSnapshot snapshot, TimeSpan now)
    {
        var id = snapshot.Model.Id;
        // A download is busy and either still transferring or carrying transfer numbers. Deleting
        // and a stand-alone verification are busy without either, and are not downloads.
        var downloading = snapshot.IsBusy &&
                          (snapshot.Status == TranscriptionModelStatus.Downloading || snapshot.Download is not null);
        _entries.TryGetValue(id, out var entry);

        if (downloading)
        {
            entry ??= _entries[id] = new Entry { Name = snapshot.Model.DisplayName };
            entry.Active = true;
            entry.Verifying = snapshot.Status == TranscriptionModelStatus.Verifying;
            entry.Download = snapshot.Download ?? entry.Download;
            entry.Outcome = ModelDownloadActivityKind.None;
            return;
        }

        if (entry is null || !entry.Active) return;

        entry.Active = false;
        entry.FinishedAt = now;
        switch (snapshot.Status)
        {
            case TranscriptionModelStatus.Ready or TranscriptionModelStatus.Selected:
                entry.Outcome = ModelDownloadActivityKind.Completed;
                entry.OutcomeDetail = "Ready to use";
                break;
            case TranscriptionModelStatus.Failed:
                entry.Outcome = ModelDownloadActivityKind.Failed;
                entry.OutcomeDetail = snapshot.StatusText;
                break;
            case TranscriptionModelStatus.Missing when snapshot.Download?.Phase == ModelDownloadPhase.Paused:
                entry.Outcome = ModelDownloadActivityKind.Paused;
                entry.OutcomeDetail = "Resume from Models";
                break;
            default:
                _entries.Remove(id);
                break;
        }
    }

    public ModelDownloadActivityState Current(TimeSpan now)
    {
        var active = _entries.Values.Where(entry => entry.Active).ToList();
        if (active.Count == 1) return Describe(active[0]);
        if (active.Count > 1) return Describe(active);

        var latest = _entries.Values
            .Where(entry => entry.Outcome != ModelDownloadActivityKind.None && now - entry.FinishedAt < HoldFor(entry.Outcome))
            .OrderByDescending(entry => entry.FinishedAt)
            .FirstOrDefault();
        if (latest is null) return ModelDownloadActivityState.None;

        var title = latest.Outcome switch
        {
            ModelDownloadActivityKind.Completed => $"{latest.Name} downloaded",
            ModelDownloadActivityKind.Paused => $"{latest.Name} paused",
            _ => $"{latest.Name} download failed"
        };
        var fraction = latest.Outcome == ModelDownloadActivityKind.Completed ? 1d : latest.Download?.Fraction;
        return new ModelDownloadActivityState(latest.Outcome, title, latest.OutcomeDetail, fraction);
    }

    /// <summary>How long until the shown outcome expires and the row should be re-read; null when nothing is pending.</summary>
    public TimeSpan? NextExpiry(TimeSpan now)
    {
        if (_entries.Values.Any(entry => entry.Active)) return null;
        TimeSpan? next = null;
        foreach (var entry in _entries.Values.Where(entry => entry.Outcome != ModelDownloadActivityKind.None))
        {
            var left = entry.FinishedAt + HoldFor(entry.Outcome) - now;
            if (left > TimeSpan.Zero && (next is null || left < next)) next = left;
        }

        return next;
    }

    private static TimeSpan HoldFor(ModelDownloadActivityKind kind) =>
        kind == ModelDownloadActivityKind.Failed ? FailedHold : CompletedHold;

    private static ModelDownloadActivityState Describe(Entry entry)
    {
        var download = entry.Download;
        var verifying = entry.Verifying || download?.Phase == ModelDownloadPhase.Verifying;
        var title = verifying ? $"Verifying {entry.Name}" : $"Downloading {entry.Name}";
        if (download is null || verifying || download.Phase != ModelDownloadPhase.Downloading)
        {
            return new ModelDownloadActivityState(
                ModelDownloadActivityKind.Active,
                title,
                // Before any bytes: preparing. After the transfer (unpacking, moving into place): finishing.
                verifying ? "Checking files" : download is null ? "Preparing…" : "Finishing…",
                verifying ? download?.Fraction : null);
        }

        var parts = new List<string>();
        if (download.Fraction is { } fraction) parts.Add(string.Format(Culture, "{0:0}%", fraction * 100));
        var remaining = ModelDownloadFormatting.Remaining(download.TimeRemaining);
        if (remaining.Length > 0) parts.Add($"~{remaining} left");
        else if (download.TotalBytes is null) parts.Add(ModelDownloadFormatting.Bytes(download.CompletedBytes));
        return new ModelDownloadActivityState(ModelDownloadActivityKind.Active, title, string.Join(" · ", parts), download.Fraction);
    }

    private static ModelDownloadActivityState Describe(IReadOnlyList<Entry> entries)
    {
        var known = entries.Select(entry => entry.Download).Where(download => download?.TotalBytes is > 0).ToList();
        double? fraction = known.Count == entries.Count
            ? (double)known.Sum(download => download!.CompletedBytes) / known.Sum(download => download!.TotalBytes!.Value)
            : null;
        var detail = fraction is { } value ? string.Format(Culture, "{0:0}%", Math.Clamp(value, 0, 1) * 100) : "In progress";
        return new ModelDownloadActivityState(
            ModelDownloadActivityKind.Active,
            string.Format(Culture, "Downloading {0} models", entries.Count),
            detail,
            fraction is { } clamped ? Math.Clamp(clamped, 0, 1) : null);
    }
}
