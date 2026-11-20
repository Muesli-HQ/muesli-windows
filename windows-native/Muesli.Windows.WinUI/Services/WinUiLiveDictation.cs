using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS BufferedDictationContext with beginBufferedDictationIfNeeded/completeBufferedDictationCapture
/// (MuesliController.swift, 2f10b23d): one hands-free dictation's live session, cursor target and
/// preview delivery. Without a safe target the dictation keeps the normal paste-on-stop path.
/// </summary>
internal sealed class WinUiLiveDictation(Action<string> setStatus, Action<string> log)
{
    private readonly object _gate = new();
    private BufferedDictationPreviewDelivery? _delivery;
    private bool _stopped;
    private volatile bool _transcriptionFailed;

    public BufferedDictationCaptureAudio Audio { get; } = new();
    public BufferedTranscriptionSession? Session { get; private set; }
    public ILiveDictationTarget? Target { get; private set; }

    /// <summary>Capture runs concurrently with recording; <see cref="Audio"/> holds the first PCM meanwhile.</summary>
    /// <param name="fallback">Starts normal chunked recognition when there is no live target.</param>
    public void BeginCapture(IntPtr window, NativeTranscriptionClient transcription, Func<Action<float[]>> fallback) => _ = Task.Run(async () =>
    {
        ILiveDictationTarget? target = null;
        try
        {
            target = (ILiveDictationTarget?)await UiaLiveDictationTarget.CaptureAsync(window, reason => log($"buffered_target:{reason}"))
                ?? UiaLiveDictationTarget.CaptureAppendOnly(window, reason => log($"buffered_target:{reason}"));
        }
        catch (Exception exception) { log($"buffered_target:capture_failed:{exception.GetType().Name}"); }
        lock (_gate)
        {
            if (_stopped) { target?.Cancel(); return; }
            if (target is null)
            {
                FallBack(fallback);
                setStatus("Live updates unavailable here; text will paste when you stop");
                return;
            }
            Complete(target, transcription, fallback);
        }
    });

    private void FallBack(Func<Action<float[]>> fallback)
    {
        if (!Audio.Attach(fallback())) Audio.Detach();
    }

    private void Complete(ILiveDictationTarget target, NativeTranscriptionClient transcription, Func<Action<float[]>> fallback)
    {
        target.OnSuspended = ShowInsertionStopped;
        var delivery = new BufferedDictationPreviewDelivery(
            update => target.UpdateAsync(update.CommittedText, update.Tail),
            (update, accepted) =>
            {
                log($"buffered_preview revision={update.Revision} committed={update.CommittedText.Length} draft={update.Tail.Length} accepted={accepted}");
                // Delivery closes itself; recognition keeps decoding for history.
                if (!accepted && target.IsSuspended) ShowInsertionStopped();
            });
        try
        {
            var model = transcription.SelectedModel;
            var kind = model.Kind;
            // Sherpa Whisper has no word timings; whisper.cpp supplies them for Large Turbo once its model is
            // downloaded. Until then (and for other Whisper sizes) Whisper streams untimed continuity chunks.
            var whisperCpp = WhisperCppPreviewDecoder.Supports(model) && WhisperCppPreviewDecoder.IsReady;
            if (WhisperCppPreviewDecoder.Supports(model) && WhisperCppPreviewDecoder.StartDownload(message => log($"buffered_{message}")))
                setStatus($"Downloading live Whisper model ({WhisperCppPreviewDecoder.SizeLabel}) · live text is basic until it finishes");
            var language = TranscriptionLanguageSelection.Resolve(model);
            log($"buffered_decoder:{(BufferedLiveDictationSupport.HasWordTimings(kind) ? "sherpa_timed" : whisperCpp ? "whisper_cpp_timed" : "untimed")}");
            var session = new BufferedTranscriptionSession(
                kind == NativeAsrModelKind.Whisper ? BufferedTranscriptionSession.Configuration.Whisper : null,
                decode: BufferedLiveDictationSupport.HasWordTimings(kind)
                    ? (samples, token) => transcription.TranscribePreviewAsync(samples, true, token)
                    : whisperCpp
                        ? (samples, token) => WhisperCppPreviewDecoder.DecodeAsync(samples, language, token)
                        : null,
                decodeWithoutTimings: async (samples, token) =>
                    (await transcription.TranscribePreviewAsync(samples, false, token)).Text,
                onUpdate: update => { if (!_stopped) delivery.Offer(update); },
                onFailure: exception =>
                {
                    if (_stopped) return;
                    log($"buffered_preview_failed:{(exception as BufferedTranscriptionSession.SessionFailure)?.Category ?? exception.GetType().Name}");
                    // Keep the target lease for the recovery path; a failed preview never triggers a second paste.
                    _transcriptionFailed = true;
                    setStatus(BufferedDictationInsertionStop.TranscriptionFailed.Detail());
                },
                onDiagnostic: diagnostic => log($"buffered_{diagnostic}"));
            Session = session;
            Target = target;
            _delivery = delivery;
            if (!Audio.Attach(session.Enqueue))
            {
                CancelCore();
                setStatus("Live updates unavailable; text will paste when you stop");
                return;
            }
            log("buffered_preview_started");
        }
        catch (Exception exception)
        {
            log($"buffered_preview_start_failed:{exception.GetType().Name}");
            FallBack(fallback);
            target.Cancel();
            Session = null;
            Target = null;
            setStatus("Live updates unavailable; text will paste when you stop");
        }
    }

    private void ShowInsertionStopped()
    {
        // A later focus loss must not replace the inference failure, which promises less.
        if (!_stopped && !_transcriptionFailed) setStatus(BufferedDictationInsertionStop.CursorOwnershipLost.Detail());
    }

    /// <summary>
    /// macOS finishStandardDictationStop: close live delivery; recognition drains the tail at stop.
    /// True when this dictation owns a live target, so it must not fall back to a normal paste.
    /// </summary>
    public bool Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _delivery?.Close();
            if (Session is { } session)
            {
                var diagnostics = session.Diagnostics;
                log($"buffered_preview_stop samples={diagnostics.ReceivedSamples} decodes={diagnostics.DecodeCount} revisions={diagnostics.Revision} closed={diagnostics.Closed}");
            }
            // No session: Audio may feed the rolling fallback, which needs the tail until capture stops.
            return Session is not null && Target is not null;
        }
    }

    /// <summary>Reuses the streamed transcript; an incomplete stream is preserved for recovery, never re-run.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(string wavPath, string recoveryDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return await BufferedDictationFinalizer.TranscribeAsync(
                Session,
                BufferedDictationFinalizer.RecordedSampleCount(wavPath),
                text => Task.FromResult(new TranscriptionResult(text, "ASR during capture: live dictation")),
                () => throw new InvalidOperationException("Live dictation has no session."),
                decision => log($"buffered_finalizer {decision}"),
                cancellationToken: cancellationToken);
        }
        catch (BufferedDictationFinalizer.IncompleteStream incomplete)
        {
            // Do not replace the safe visible prefix with an incomplete hypothesis.
            Target?.Cancel();
            throw new LiveDictationRecoveryException(
                BufferedDictationRecovery.Preserve(wavPath, incomplete.PartialText, recoveryDirectory), incomplete);
        }
    }

    /// <summary>Cancellation leaves already inserted text in place.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _stopped = true;
            CancelCore();
        }
    }

    private void CancelCore()
    {
        _delivery?.Close();
        Session?.Cancel();
        Target?.Cancel();
        Audio.Detach();
    }

    /// <summary>macOS "Live transcription could not finish" alert.</summary>
    public static void ShowRecovery(BufferedDictationRecovery.Outcome outcome) => App.UiDispatcher.TryEnqueue(async () =>
    {
        if (App.Window is MainWindow window) window.ShowDashboard();
        if (App.Window.Content is not FrameworkElement owner) return;
        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            Title = "Live transcription could not finish",
            Content = new TextBlock { Text = outcome.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            PrimaryButtonText = "Show Recording",
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{outcome.ShowPath}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open; the outcome is in the log and the files are on disk.
        }
    });
}

internal sealed class LiveDictationRecoveryException(BufferedDictationRecovery.Outcome outcome, Exception inner)
    : Exception(inner.Message, inner)
{
    public BufferedDictationRecovery.Outcome Outcome { get; } = outcome;
}
