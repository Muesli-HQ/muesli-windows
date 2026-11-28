namespace Muesli.Windows.Services;

/// <summary>
/// After a dictation is pasted, watches the text box it went into for up to 45 seconds and reports
/// corrections the user makes to it. The Windows port of macOS <c>DictationCorrectionMonitor</c>:
/// same polling schedule, quiet window and per-dictation limit (<see cref="DictationCorrectionMonitorPolicy"/>),
/// same detector (<see cref="DictionaryCorrectionDetector"/>). The text is read only on this
/// background loop and never logged; the log gets counts.
/// </summary>
public sealed class DictationCorrectionMonitor : IDisposable
{
    private static readonly WindowsSpellChecker SpellChecker = new();
    private readonly AppLogService _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _session;

    public DictationCorrectionMonitor(AppLogService log) => _log = log;

    /// <summary>
    /// Starts watching <paramref name="targetWindow"/> for edits of <paramref name="pastedText"/>,
    /// replacing any session already running. <paramref name="onSuggestion"/> is called on a
    /// background thread.
    /// </summary>
    public void Start(string pastedText, IntPtr targetWindow, Action<DictionarySuggestion> onSuggestion)
    {
        Cancel();
        if (string.IsNullOrWhiteSpace(pastedText) || targetWindow == IntPtr.Zero) return;
        var session = new CancellationTokenSource();
        lock (_gate) _session = session;
        _log.Info($"Dictionary correction monitor started. originalCharacters={pastedText.Length}");
        _ = Task.Run(() => RunAsync(pastedText, targetWindow, onSuggestion, session.Token));
    }

    public void Cancel()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
        }
        previous?.Cancel();
    }

    private async Task RunAsync(string original, IntPtr target, Action<DictionarySuggestion> onSuggestion, CancellationToken cancellation)
    {
        var started = DateTime.UtcNow;
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        var baseline = new DictationCorrectionBaseline(original);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        int polls = 0, stable = 0, lastSnapshotCount = 0;
        try
        {
            using var reader = new FocusedTextReader();
            await Task.Delay(DictationCorrectionMonitorPolicy.InitialPollDelay, cancellation);
            while (!cancellation.IsCancellationRequested && DateTime.UtcNow - started < DictationCorrectionMonitorPolicy.MonitoringWindow)
            {
                polls++;
                var snapshots = baseline.EditedSnapshots(reader.ReadSnapshots(target, DictationCorrectionMonitorPolicy.MaxCandidateCharacters));
                if (snapshots.Count != lastSnapshotCount)
                {
                    // Counts only, as macOS logs: whether the text box is readable and edited at all.
                    _log.Info($"Dictionary correction monitor reading. poll={polls}; editedSnapshots={snapshots.Count}");
                    lastSnapshotCount = snapshots.Count;
                }

                foreach (var snapshot in stabilizer.Observe(snapshots, DateTime.UtcNow, DictationCorrectionMonitorPolicy.SnapshotQuietWindow))
                {
                    stable++;
                    var recognized = SpellChecker.RecognizedWords(DictationCorrectionMonitorPolicy.EnglishWordCandidates(original, snapshot));
                    var suggestions = DictionaryCorrectionDetector.Suggestions(
                        original, baseline.Before, snapshot, DictationCorrectionMonitorPolicy.MaxSuggestionsPerSession, recognized);
                    foreach (var suggestion in suggestions)
                    {
                        if (cancellation.IsCancellationRequested) return;
                        if (!emitted.Add(DictionarySuggestionLedger.Key(suggestion.Observed, suggestion.Replacement))) continue;
                        _log.Info($"Dictionary correction found. observedChars={suggestion.Observed.Length}; replacementChars={suggestion.Replacement.Length}");
                        onSuggestion(suggestion);
                        if (emitted.Count >= DictationCorrectionMonitorPolicy.MaxSuggestionsPerSession)
                        {
                            _log.Info($"Dictionary correction monitor finished. reason=maxSuggestions; polls={polls}");
                            return;
                        }
                    }
                }

                var interval = DateTime.UtcNow - started < DictationCorrectionMonitorPolicy.FastPollingWindow
                    ? DictationCorrectionMonitorPolicy.FastPollInterval
                    : DictationCorrectionMonitorPolicy.SteadyPollInterval;
                await Task.Delay(interval, cancellation);
            }

            _log.Info($"Dictionary correction monitor finished. reason=timeout; polls={polls}; stableSnapshots={stable}; suggestions={emitted.Count}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // The monitor is optional: a failure ends this session and never reaches the dictation.
            _log.Error("Dictionary correction monitor stopped.", exception);
        }
    }

    public void Dispose() => Cancel();
}
