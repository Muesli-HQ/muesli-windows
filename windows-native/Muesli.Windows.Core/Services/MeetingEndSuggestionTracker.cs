using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>
/// Advisory only: a short closing exchange followed by quiet can invite the user to finish.
/// Capture continues until an explicit user action. No transcript text leaves this policy.
/// </summary>
public sealed class MeetingEndSuggestionTracker
{
    private readonly object _gate = new();
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();
    private DateTimeOffset _started;
    private DateTimeOffset? _lastAudio;
    private DateTimeOffset? _lastSound;
    private DateTimeOffset? _lastActivity;
    private DateTimeOffset? _closingAt;
    private ClosingCue _cues;
    private string _partialMicrophone = "";
    private string _partialSystem = "";
    private bool _offered;
    private long _dropped;

    [Flags]
    private enum ClosingCue { None = 0, Wrap = 1, Thanks = 2, Farewell = 4 }

    // Consume only closing language and small conversational fillers. Unmatched content (including
    // a question, a quotation, "before we wrap up", or "one more thing") disarms the suggestion.
    private static readonly Regex Words = new(@"[^\p{L}\p{N}']+", RegexOptions.Compiled);
    private static readonly Regex Closing = new(
        @"\G\s*(?:(?<wrap>that(?:'s| is) (?:all|everything) (?:for (?:today|now)|from me)|(?:let's|let us) (?:wrap (?:it |this )?up|call it a day)|we(?:'re| are) (?:all )?done (?:for today|here))|" +
        @"(?<thanks>(?:thanks|thank you) (?:everyone|everybody|all|so much|for (?:your time|joining|coming|today)))|" +
        @"(?<farewell>(?:goodbye|bye(?: bye)?|see you (?:all )?(?:soon|tomorrow|next (?:week|time))|talk (?:to you )?(?:soon|later)|have a (?:good|great|nice) (?:day|evening|weekend)|take care))|" +
        @"(?:okay|ok|alright|all right|well|great|and|then|yes|yeah|everyone|everybody|you too|thanks|thank you))(?=\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public DateTimeOffset? LastActivity { get { lock (_gate) return _lastActivity; } }
    public bool CanOffer { get { lock (_gate) return !_offered; } }

    public void BeginCapture(DateTimeOffset now, bool newRecording)
    {
        lock (_gate)
        {
            _started = now;
            _lastAudio = _lastSound = _lastActivity = _closingAt = null;
            _cues = ClosingCue.None;
            _partialMicrophone = _partialSystem = "";
            if (!newRecording) return;
            _offered = false;
            _dropped = 0;
            _seen.Clear();
            _seenOrder.Clear();
        }
    }

    /// <summary>Fresh capture packets distinguish actual quiet from a stalled transcript decoder.</summary>
    public void ObserveAudio(ReadOnlySpan<float> samples, DateTimeOffset now)
    {
        if (samples.IsEmpty) return;
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        var audible = !double.IsFinite(energy) || energy / samples.Length >= 0.000009; // RMS 0.003; noise delays a prompt safely.
        lock (_gate)
        {
            // A stopped/recovering capture feed is not evidence of silence.
            if (_lastAudio is null || now - _lastAudio > TimeSpan.FromSeconds(3)) _lastActivity = now;
            _lastAudio = now;
            if (audible) _lastSound = _lastActivity = now;
        }
    }

    public void ObserveTranscript(LiveTranscriptSnapshot snapshot, DateTimeOffset now)
    {
        lock (_gate)
        {
            if ((!string.IsNullOrWhiteSpace(snapshot.PartialMicrophone) && snapshot.PartialMicrophone != _partialMicrophone) ||
                (!string.IsNullOrWhiteSpace(snapshot.PartialSystem) && snapshot.PartialSystem != _partialSystem))
                _lastActivity = now;
            _partialMicrophone = snapshot.PartialMicrophone;
            _partialSystem = snapshot.PartialSystem;
            var dropped = snapshot.DroppedPackets != _dropped;
            _dropped = snapshot.DroppedPackets;
            if (dropped) ClearClosing();

            // Bound work and memory even in an hours-long meeting. Both streaming and rolling
            // snapshots may reorder channels, so deduplicate segment IDs rather than list indices.
            foreach (var segment in snapshot.Committed.TakeLast(16))
            {
                if (!_seen.Add(segment.Id)) continue;
                _seenOrder.Enqueue(segment.Id);
                if (_seenOrder.Count > 128) _seen.Remove(_seenOrder.Dequeue());
                if (string.IsNullOrWhiteSpace(segment.Text)) continue;
                _lastActivity = now;
                if (_offered) continue;
                var cues = Classify(segment.Text);
                if (dropped || snapshot.QueuedPackets > 0 || cues == ClosingCue.None || _lastSound is null ||
                    now - _lastSound > TimeSpan.FromSeconds(20))
                {
                    ClearClosing();
                    continue;
                }
                if (_closingAt is null || now - _closingAt > TimeSpan.FromSeconds(40))
                    _cues = ClosingCue.None;
                _cues |= cues;
                _closingAt = now;
            }
        }
    }

    public bool ShouldSuggest(DateTimeOffset now)
    {
        lock (_gate)
        {
            var multipleCues = _cues != ClosingCue.None && (((int)_cues & ((int)_cues - 1)) != 0);
            return !_offered && multipleCues && now - _started >= TimeSpan.FromMinutes(2) &&
                _closingAt is { } closing && now - closing <= TimeSpan.FromSeconds(60) &&
                _lastAudio is { } audio && now - audio <= TimeSpan.FromSeconds(3) &&
                _lastActivity is { } activity && now - activity >= TimeSpan.FromSeconds(25) &&
                string.IsNullOrWhiteSpace(_partialMicrophone) && string.IsNullOrWhiteSpace(_partialSystem);
        }
    }

    // Shared with the source-loss prompt: ignoring, dismissing or acting on either surface means
    // no more end suggestions for this recording, including after a pause or a brief reconnect.
    public void MarkOffered() { lock (_gate) _offered = true; }

    private void ClearClosing() { _closingAt = null; _cues = ClosingCue.None; }

    private static ClosingCue Classify(string text)
    {
        if (text.Length > 600 || text.Contains('?') || text.Contains('"') || text.Contains('“')) return ClosingCue.None;
        var normalized = Words.Replace(text.Replace('’', '\''), " ").Trim();
        var position = 0;
        var cues = ClosingCue.None;
        while (position < normalized.Length)
        {
            var match = Closing.Match(normalized, position);
            if (!match.Success || match.Index != position) return ClosingCue.None;
            if (match.Groups["wrap"].Success) cues |= ClosingCue.Wrap;
            if (match.Groups["thanks"].Success) cues |= ClosingCue.Thanks;
            if (match.Groups["farewell"].Success) cues |= ClosingCue.Farewell;
            position += match.Length;
        }
        return cues;
    }
}
