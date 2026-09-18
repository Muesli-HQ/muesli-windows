namespace Muesli.Windows.Services;

/// <summary>
/// Forwards transcription progress only when it has moved by a visible amount.
///
/// This exists because of a real failure, not tidiness. The media decode loop produces a report per
/// decoded buffer — tens of thousands for an hour-long file. <see cref="Progress{T}"/> delivers each
/// one as a Normal-priority dispatcher callback, and WPF ranks Normal above Input, so the flood
/// starved mouse input: during decoding the label froze and the Cancel button could not be clicked
/// at all, which defeated the entire point of having one.
///
/// Collapsing to whole-percent steps caps a stage at about 100 callbacks, which the dispatcher
/// absorbs comfortably while still animating smoothly.
/// </summary>
public sealed class ThrottledTranscriptionProgress(IProgress<TranscriptionProgress>? inner)
{
    private int _lastPercent = -1;
    private TranscriptionStage? _lastStage;

    /// <summary>True when there is nothing downstream, so callers can skip computing a fraction.</summary>
    public bool IsNoOp => inner is null;

    public void Report(TranscriptionStage stage, double fraction)
    {
        if (inner is null)
        {
            return;
        }

        if (!double.IsFinite(fraction))
        {
            return;
        }

        var percent = (int)(Math.Clamp(fraction, 0, 1) * 100);
        // A stage change is always worth showing, even if the percent happens to match.
        if (_lastStage == stage && percent == _lastPercent)
        {
            return;
        }

        _lastStage = stage;
        _lastPercent = percent;
        inner.Report(new TranscriptionProgress(stage, percent / 100.0));
    }

    /// <summary>Forwards a stage that reports no measurable fraction.</summary>
    public void ReportUnmeasured(TranscriptionStage stage)
    {
        if (inner is null)
        {
            return;
        }

        _lastStage = stage;
        _lastPercent = -1;
        inner.Report(new TranscriptionProgress(stage, null));
    }
}
