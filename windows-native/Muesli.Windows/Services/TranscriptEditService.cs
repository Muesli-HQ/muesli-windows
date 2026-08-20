using System.Collections.Concurrent;
using System.IO;

namespace Muesli.Windows.Services;

/// <summary>
/// MTG-03 service slice: explicit transcript save/cancel with optimistic backup, and candidate-based
/// retranscription so failure or cancellation cannot destroy the prior transcript. The composition
/// root owns this service and the UI prompts for re-summarization when generated notes go stale.
/// </summary>
public sealed class TranscriptEditService : IDisposable
{
    public static readonly TimeSpan DefaultRetranscribeTimeout = TimeSpan.FromMinutes(10);

    private readonly ITranscriptMeetingStore _store;
    private readonly RetranscriptionCandidateStore _candidates;
    private readonly IMeetingRetranscriptionAsr? _asr;
    private readonly ITranscriptEditDiagnostics? _log;
    private readonly TimeProvider _time;
    private readonly TimeSpan _retranscribeTimeout;
    private readonly CaptureStorageService? _captureStorage;
    private readonly ConcurrentDictionary<string, InFlightRetranscribe> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TranscriptEditSession> _edits = new(StringComparer.Ordinal);
    private bool _disposed;

    public TranscriptEditService(
        ITranscriptMeetingStore store,
        RetranscriptionCandidateStore candidates,
        IMeetingRetranscriptionAsr? asr = null,
        ITranscriptEditDiagnostics? diagnostics = null,
        TimeProvider? time = null,
        TimeSpan? retranscribeTimeout = null,
        CaptureStorageService? captureStorage = null)
    {
        _store = store;
        _candidates = candidates;
        _asr = asr;
        _log = diagnostics;
        _time = time ?? TimeProvider.System;
        _retranscribeTimeout = retranscribeTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultRetranscribeTimeout;
        _captureStorage = captureStorage;
    }

    public TranscriptEditResult BeginTranscriptEdit(string meetingId)
    {
        ThrowIfDisposed();
        if (_flights.ContainsKey(meetingId) || IsScratchBusy(meetingId))
        {
            return EditBusy(meetingId, "Transcript editing is unavailable while a retranscription candidate is in progress or waiting to be accepted.");
        }

        var meeting = _store.Find(meetingId);
        if (meeting is null)
        {
            return EditNotFound(meetingId);
        }

        var session = new TranscriptEditSession
        {
            MeetingId = meeting.Id,
            OriginalTranscript = meeting.Transcript ?? "",
            HadGeneratedNotes = HasGeneratedNotes(meeting),
            ManualNotesSnapshot = meeting.ManualNotes ?? "",
            TitleIsManual = meeting.TitleIsManual,
            TitleSnapshot = meeting.Title ?? "",
            AliasesSnapshot = CloneAliases(meeting)
        };
        _edits[meetingId] = session;
        _log?.Info($"Transcript edit began. meetingId={meetingId}; backupChars={session.OriginalTranscript.Length}; generatedNotesPresent={session.HadGeneratedNotes}");
        return new TranscriptEditResult
        {
            Succeeded = true,
            Outcome = TranscriptEditOutcome.Unchanged,
            Status = "Editing transcript. Save to keep changes, or cancel to restore the previous transcript.",
            Meeting = Clone(meeting),
            Session = session
        };
    }

    public TranscriptEditResult SaveTranscriptEdit(TranscriptEditSession session, string editedTranscript)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);

        var meeting = _store.Find(session.MeetingId);
        if (meeting is null)
        {
            _edits.TryRemove(session.MeetingId, out _);
            return EditNotFound(session.MeetingId);
        }

        if (_flights.ContainsKey(session.MeetingId) || IsScratchBusy(session.MeetingId))
        {
            return EditBusy(session.MeetingId, "Cannot save a transcript edit while a retranscription candidate is in progress or waiting to be accepted.", Clone(meeting));
        }

        var backup = session.OriginalTranscript ?? "";
        var edited = editedTranscript ?? "";
        if (string.Equals(backup, edited, StringComparison.Ordinal))
        {
            _edits.TryRemove(session.MeetingId, out _);
            return new TranscriptEditResult
            {
                Succeeded = true,
                AppliedToMeeting = false,
                Outcome = TranscriptEditOutcome.Unchanged,
                Status = "Transcript unchanged.",
                Meeting = Clone(meeting),
                Session = session
            };
        }

        var original = Clone(meeting);
        var updated = WithTranscript(meeting, edited);
        try
        {
            _store.Save(updated);
        }
        catch (Exception exception)
        {
            var restored = TryRestore(original);
            _log?.Error(
                $"Transcript save failed. meetingId={session.MeetingId}; restoredFromBackup={restored}",
                exception);
            return new TranscriptEditResult
            {
                Succeeded = false,
                AppliedToMeeting = false,
                Outcome = TranscriptEditOutcome.SaveFailed,
                Status = restored
                    ? "Transcript could not be saved. The previous transcript was restored."
                    : "Transcript could not be saved, and the previous transcript could not be rewritten. The on-disk meeting may be inconsistent.",
                Error = HonestException(exception),
                Meeting = restored ? Clone(original) : _store.Find(session.MeetingId) ?? Clone(original),
                Session = session,
                RestoredFromBackup = restored
            };
        }

        _edits.TryRemove(session.MeetingId, out _);
        var requestsResummary = session.HadGeneratedNotes &&
            !string.Equals(backup, edited, StringComparison.Ordinal);
        if (requestsResummary)
        {
            _candidates.SetGeneratedNotesStale(session.MeetingId, "transcript-edit", _time.GetUtcNow());
        }

        _log?.Info(
            $"Transcript edit saved. meetingId={session.MeetingId}; fingerprint={AppLogService.SensitiveTextFingerprint(edited)}; requestsResummary={requestsResummary}; titleOwned={updated.TitleIsManual}");
        return new TranscriptEditResult
        {
            Succeeded = true,
            AppliedToMeeting = true,
            Outcome = TranscriptEditOutcome.Saved,
            Status = requestsResummary
                ? "Transcript saved. Generated notes may no longer match. Re-summarize to update them. Your written notes were not changed."
                : "Transcript saved.",
            Meeting = Clone(updated),
            Session = session,
            GeneratedNotesStale = requestsResummary,
            RequestsResummary = requestsResummary
        };
    }

    public TranscriptEditResult CancelTranscriptEdit(TranscriptEditSession session)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);

        _edits.TryRemove(session.MeetingId, out _);
        var meeting = _store.Find(session.MeetingId);
        if (meeting is null)
        {
            return EditNotFound(session.MeetingId);
        }

        var restored = WithTranscript(meeting, session.OriginalTranscript ?? "");
        if (!string.Equals(meeting.Transcript, session.OriginalTranscript, StringComparison.Ordinal))
        {
            // Cancel restores the backup even if a racing writer changed the store. Failure here
            // still reports cancellation rather than success.
            try
            {
                _store.Save(restored);
            }
            catch (Exception exception)
            {
                _log?.Error($"Transcript edit cancel could not rewrite the backup. meetingId={session.MeetingId}", exception);
                return new TranscriptEditResult
                {
                    Succeeded = false,
                    Outcome = TranscriptEditOutcome.Cancelled,
                    Status = "Transcript edit cancelled, but the previous transcript could not be rewritten.",
                    Error = HonestException(exception),
                    Meeting = Clone(meeting),
                    Session = session
                };
            }
        }

        _log?.Info($"Transcript edit cancelled. meetingId={session.MeetingId}");
        return new TranscriptEditResult
        {
            Succeeded = false,
            AppliedToMeeting = false,
            Outcome = TranscriptEditOutcome.Cancelled,
            Status = "Transcript edit cancelled. The previous transcript was restored.",
            Meeting = Clone(restored),
            Session = session
        };
    }

    public bool IsGeneratedNotesStale(string meetingId) => _candidates.IsGeneratedNotesStale(meetingId);

    public void ClearGeneratedNotesStale(string meetingId) => _candidates.ClearGeneratedNotesStale(meetingId);

    public RetranscriptionCandidate? GetPendingCandidate(string meetingId)
    {
        var scratch = _candidates.TryLoad(meetingId);
        return scratch is null ? null : ToCandidate(scratch);
    }

    public async Task<RetranscriptionResult> RetranscribeAsync(
        string meetingId,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_asr is null)
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.EngineMissing,
                "Re-transcription is unavailable because no speech engine is configured. The original transcript was not changed.",
                _store.Find(meetingId));
        }

        if (_edits.ContainsKey(meetingId))
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.Busy,
                "Re-transcription is unavailable while the transcript is being edited. The original transcript was not changed.",
                _store.Find(meetingId));
        }

        var meeting = _store.Find(meetingId);
        if (meeting is null)
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.MeetingNotFound,
                "That meeting could not be found. No transcript was changed.");
        }

        var original = Clone(meeting);
        if (_flights.ContainsKey(meetingId) || IsScratchBusy(meetingId))
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.Busy,
                "A retranscription candidate is already in progress or waiting to be accepted. The original transcript was not changed.",
                original);
        }

        var audio = ResolveRetainedAudio(meeting);
        if (audio.Kind == AudioResolveKind.Missing)
        {
            _log?.Info($"Retranscribe failed closed. meetingId={meetingId}; reason=missing-audio");
            return RetranscribeClosed(
                RetranscriptionOutcome.MissingAudio,
                "Re-transcription failed: the retained recording is missing. The original transcript was not changed.",
                original,
                "missing-audio");
        }

        if (audio.Kind == AudioResolveKind.Corrupt)
        {
            _log?.Info($"Retranscribe failed closed. meetingId={meetingId}; reason=corrupt-audio; bytes={audio.ByteLength}");
            return RetranscribeClosed(
                RetranscriptionOutcome.CorruptAudio,
                "Re-transcription failed: the retained recording is unreadable. The original transcript was not changed.",
                original,
                "corrupt-audio");
        }

        var now = _time.GetUtcNow();
        var candidateId = Guid.NewGuid().ToString("N");
        var scratch = new RetranscriptionScratchState
        {
            MeetingId = meetingId,
            CandidateId = candidateId,
            Status = nameof(RetranscriptionCandidateStatus.InFlight),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            AudioFileName = audio.FileName,
            AudioByteLength = audio.ByteLength
        };

        using var flightCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        flightCts.CancelAfter(_retranscribeTimeout);
        var flight = new InFlightRetranscribe(candidateId, flightCts);
        if (!_flights.TryAdd(meetingId, flight))
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.Busy,
                "A retranscription candidate is already in progress. The original transcript was not changed.",
                original);
        }

        try
        {
            // Admission owns the per-meeting flight before the shared candidate path is touched.
            // A concurrent caller that lost the admission race must leave the winner's scratch
            // untouched; deleting the path here could erase the winning candidate.
            try
            {
                _candidates.Save(scratch);
            }
            catch (Exception exception)
            {
                _log?.Error($"Retranscribe could not write in-flight scratch. meetingId={meetingId}", exception);
                return RetranscribeClosed(
                    RetranscriptionOutcome.PersistFailed,
                    "Re-transcription could not start because candidate scratch could not be written. The original transcript was not changed.",
                    original,
                    HonestException(exception));
            }

            _log?.Info(
                $"Retranscribe started. meetingId={meetingId}; candidate={candidateId}; audioFile={audio.FileName}; bytes={audio.ByteLength}");
            TranscriptionResult asrResult;
            try
            {
                asrResult = await _asr.TranscribeOwnedAudioAsync(audio.Path!, progress, flightCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var current = _candidates.TryLoad(meetingId);
                var abandoned = current is null ||
                    !string.Equals(current.CandidateId, candidateId, StringComparison.Ordinal) ||
                    !string.Equals(current.Status, nameof(RetranscriptionCandidateStatus.InFlight), StringComparison.Ordinal);
                AssertOriginalIntact(meetingId, original);
                if (cancellationToken.IsCancellationRequested)
                {
                    AbandonScratch(meetingId, candidateId, "cancelled");
                    _log?.Info($"Retranscribe cancelled. meetingId={meetingId}; candidate={candidateId}");
                    return RetranscribeClosed(
                        RetranscriptionOutcome.Cancelled,
                        "Re-transcription cancelled. The original transcript was not changed.",
                        original,
                        "cancelled");
                }

                if (abandoned)
                {
                    _log?.Info($"Retranscribe discarded after in-flight recovery. meetingId={meetingId}; candidate={candidateId}");
                    return RetranscribeClosed(
                        RetranscriptionOutcome.RecoveredInFlight,
                        "An in-progress retranscription was interrupted and was not completed. The original transcript was not changed.",
                        original,
                        "recovered-in-flight");
                }

                AbandonScratch(meetingId, candidateId, "timeout");
                _log?.Info($"Retranscribe timed out. meetingId={meetingId}; candidate={candidateId}");
                return RetranscribeClosed(
                    RetranscriptionOutcome.TimedOut,
                    "Re-transcription timed out. The original transcript was not changed.",
                    original,
                    "timeout");
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
            {
                AbandonScratch(meetingId, candidateId, "corrupt-audio");
                AssertOriginalIntact(meetingId, original);
                _log?.Error($"Retranscribe audio could not be decoded. meetingId={meetingId}; candidate={candidateId}", exception);
                return RetranscribeClosed(
                    RetranscriptionOutcome.CorruptAudio,
                    "Re-transcription failed: the retained recording is unreadable. The original transcript was not changed.",
                    original,
                    HonestException(exception));
            }
            catch (Exception exception)
            {
                AbandonScratch(meetingId, candidateId, "asr-failed");
                AssertOriginalIntact(meetingId, original);
                _log?.Error($"Retranscribe ASR failed. meetingId={meetingId}; candidate={candidateId}", exception);
                return RetranscribeClosed(
                    RetranscriptionOutcome.AsrFailed,
                    "Re-transcription failed. The original transcript was not changed.",
                    original,
                    HonestException(exception));
            }

            var scratchAfterAsr = _candidates.TryLoad(meetingId);
            if (scratchAfterAsr is null ||
                !string.Equals(scratchAfterAsr.CandidateId, candidateId, StringComparison.Ordinal) ||
                !string.Equals(scratchAfterAsr.Status, nameof(RetranscriptionCandidateStatus.InFlight), StringComparison.Ordinal))
            {
                AssertOriginalIntact(meetingId, original);
                _log?.Info($"Retranscribe result discarded after recovery. meetingId={meetingId}; candidate={candidateId}");
                return RetranscribeClosed(
                    RetranscriptionOutcome.RecoveredInFlight,
                    "Re-transcription was interrupted. The original transcript was not changed.",
                    original,
                    "abandoned-after-recovery");
            }

            var transcript = asrResult.Text?.Trim() ?? "";
            if (transcript.Length == 0)
            {
                AbandonScratch(meetingId, candidateId, "empty-transcript");
                AssertOriginalIntact(meetingId, original);
                _log?.Info($"Retranscribe produced an empty transcript. meetingId={meetingId}; candidate={candidateId}");
                return RetranscribeClosed(
                    RetranscriptionOutcome.EmptyTranscript,
                    "Re-transcription produced no text. The original transcript was not changed.",
                    original,
                    "empty-transcript");
            }

            var ready = scratchAfterAsr with
            {
                Status = nameof(RetranscriptionCandidateStatus.Ready),
                Transcript = transcript,
                TranscriptFingerprint = AppLogService.SensitiveTextFingerprint(transcript),
                UpdatedAtUtc = _time.GetUtcNow(),
                DurationMs = asrResult.DurationMs
            };

            try
            {
                _candidates.Save(ready);
            }
            catch (Exception exception)
            {
                AbandonScratch(meetingId, candidateId, "persist-failed");
                AssertOriginalIntact(meetingId, original);
                _log?.Error($"Retranscribe could not persist the candidate. meetingId={meetingId}; candidate={candidateId}", exception);
                return RetranscribeClosed(
                    RetranscriptionOutcome.PersistFailed,
                    "Re-transcription finished, but the candidate could not be saved. The original transcript was not changed.",
                    original,
                    HonestException(exception));
            }

            AssertOriginalIntact(meetingId, original);
            _log?.Info(
                $"Retranscribe candidate ready. meetingId={meetingId}; candidate={candidateId}; fingerprint={ready.TranscriptFingerprint}; durationMs={ready.DurationMs}");
            return new RetranscriptionResult
            {
                Succeeded = true,
                AppliedToMeeting = false,
                Outcome = RetranscriptionOutcome.CandidateReady,
                Status = "Re-transcription produced a candidate. Accept it to replace the current transcript, or reject it to keep the original.",
                Meeting = Clone(original),
                Candidate = ToCandidate(ready)
            };
        }
        finally
        {
            // Recovery/deletion can deliberately remove this flight and admit a newer one while
            // the old ASR call is still unwinding. Remove only this exact flight so its teardown
            // cannot clear the newer admission.
            RemoveFlight(meetingId, flight);
        }
    }

    public RetranscriptionResult AcceptCandidate(string meetingId, string candidateId)
    {
        ThrowIfDisposed();
        var meeting = _store.Find(meetingId);
        if (meeting is null)
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.MeetingNotFound,
                "That meeting could not be found. No transcript was changed.");
        }

        var original = Clone(meeting);
        var scratch = _candidates.TryLoad(meetingId);
        if (scratch is null ||
            !string.Equals(scratch.CandidateId, candidateId, StringComparison.Ordinal) ||
            !string.Equals(scratch.Status, nameof(RetranscriptionCandidateStatus.Ready), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(scratch.Transcript))
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.NoCandidate,
                "There is no ready retranscription candidate to accept. The original transcript was not changed.",
                original);
        }

        var updated = WithTranscript(meeting, scratch.Transcript);
        try
        {
            _store.Save(updated);
        }
        catch (Exception exception)
        {
            var restored = TryRestore(original);
            _log?.Error($"Accepting a retranscription candidate failed. meetingId={meetingId}; restoredFromBackup={restored}", exception);
            return new RetranscriptionResult
            {
                Succeeded = false,
                AppliedToMeeting = false,
                Outcome = RetranscriptionOutcome.PersistFailed,
                Status = restored
                    ? "The candidate could not be saved. The original transcript was restored."
                    : "The candidate could not be saved, and the original transcript could not be rewritten.",
                Error = HonestException(exception),
                Meeting = restored ? Clone(original) : _store.Find(meetingId) ?? Clone(original)
            };
        }

        _candidates.Delete(meetingId);
        var requestsResummary = HasGeneratedNotes(original);
        if (requestsResummary)
        {
            _candidates.SetGeneratedNotesStale(meetingId, "retranscribe-accepted", _time.GetUtcNow());
        }

        _log?.Info(
            $"Retranscribe candidate accepted. meetingId={meetingId}; candidate={candidateId}; fingerprint={scratch.TranscriptFingerprint}; requestsResummary={requestsResummary}; titleOwned={updated.TitleIsManual}");
        return new RetranscriptionResult
        {
            Succeeded = true,
            AppliedToMeeting = true,
            Outcome = RetranscriptionOutcome.Accepted,
            Status = requestsResummary
                ? "Candidate accepted. Generated notes may no longer match. Re-summarize to update them. Your written notes were not changed."
                : "Candidate accepted. The previous transcript was replaced.",
            Meeting = Clone(updated),
            Candidate = ToCandidate(scratch with { Status = nameof(RetranscriptionCandidateStatus.Ready) }),
            GeneratedNotesStale = requestsResummary,
            RequestsResummary = requestsResummary,
            TitleRemainsUserOwned = original.TitleIsManual && updated.TitleIsManual
        };
    }

    public RetranscriptionResult RejectCandidate(string meetingId, string candidateId)
    {
        ThrowIfDisposed();
        var meeting = _store.Find(meetingId);
        if (meeting is null)
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.MeetingNotFound,
                "That meeting could not be found. No transcript was changed.");
        }

        var original = Clone(meeting);
        var scratch = _candidates.TryLoad(meetingId);
        if (scratch is null || !string.Equals(scratch.CandidateId, candidateId, StringComparison.Ordinal))
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.NoCandidate,
                "There is no retranscription candidate to reject. The original transcript was not changed.",
                original);
        }

        _candidates.Delete(meetingId);
        AssertOriginalIntact(meetingId, original);
        _log?.Info($"Retranscribe candidate rejected. meetingId={meetingId}; candidate={candidateId}");
        return new RetranscriptionResult
        {
            Succeeded = true,
            AppliedToMeeting = false,
            Outcome = RetranscriptionOutcome.Rejected,
            Status = "Candidate rejected. The original transcript was kept.",
            Meeting = Clone(original)
        };
    }

    /// <summary>
    /// Recovers an in-flight candidate after a crash or restart. A ready candidate is left for the
    /// user to accept or reject. An in-flight candidate is abandoned and is never treated as success.
    /// </summary>
    public RetranscriptionResult RecoverInFlight(string meetingId)
    {
        ThrowIfDisposed();
        var meeting = _store.Find(meetingId);
        var original = meeting is null ? null : Clone(meeting);
        var scratch = _candidates.TryLoad(meetingId);
        if (scratch is null)
        {
            return RetranscribeClosed(
                RetranscriptionOutcome.NoCandidate,
                "No retranscription candidate needed recovery.",
                original);
        }

        if (string.Equals(scratch.Status, nameof(RetranscriptionCandidateStatus.Ready), StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(scratch.Transcript))
        {
            _log?.Info($"Recovered a ready retranscription candidate. meetingId={meetingId}; candidate={scratch.CandidateId}");
            return new RetranscriptionResult
            {
                Succeeded = true,
                AppliedToMeeting = false,
                Outcome = RetranscriptionOutcome.CandidateReady,
                Status = "A retranscription candidate was recovered and is waiting to be accepted or rejected. The original transcript was not changed.",
                Meeting = original,
                Candidate = ToCandidate(scratch)
            };
        }

        AbandonScratch(meetingId, scratch.CandidateId, "recovered-in-flight");
        if (_flights.TryRemove(meetingId, out var flight))
        {
            try
            {
                flight.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The live flight is already tearing down.
            }
        }

        _log?.Info($"Abandoned an in-flight retranscription candidate. meetingId={meetingId}; candidate={scratch.CandidateId}");
        return RetranscribeClosed(
            RetranscriptionOutcome.RecoveredInFlight,
            "An in-progress retranscription was interrupted and was not completed. The original transcript was not changed.",
            original,
            "recovered-in-flight");
    }

    public IReadOnlyList<RetranscriptionResult> RecoverAbandonedFlights()
    {
        var results = new List<RetranscriptionResult>();
        foreach (var meetingId in _candidates.ListMeetingIds())
        {
            results.Add(RecoverInFlight(meetingId));
        }

        return results;
    }

    public void DeleteMeetingScratch(string meetingId)
    {
        ThrowIfDisposed();
        if (_flights.TryRemove(meetingId, out var flight))
        {
            try
            {
                flight.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _edits.TryRemove(meetingId, out _);
        _candidates.DeleteAllForMeeting(meetingId);
        _log?.Info($"Retranscribe scratch deleted. meetingId={meetingId}");
    }

    public static bool CanRetranscribe(PersistedMeeting meeting, CaptureStorageService? captureStorage = null) =>
        ResolveRetainedAudio(meeting, captureStorage).Kind == AudioResolveKind.Ok;

    public void Dispose()
    {
        _disposed = true;
        foreach (var pair in _flights)
        {
            try
            {
                pair.Value.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _flights.Clear();
        _edits.Clear();
    }

    private bool TryRestore(PersistedMeeting original)
    {
        try
        {
            _store.Save(original);
            return true;
        }
        catch (Exception exception)
        {
            _log?.Error($"Optimistic transcript backup restore failed. meetingId={original.Id}", exception);
            return false;
        }
    }

    private void AbandonScratch(string meetingId, string candidateId, string reason)
    {
        var existing = _candidates.TryLoad(meetingId);
        if (existing is null || !string.Equals(existing.CandidateId, candidateId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _candidates.Delete(meetingId);
        }
        catch (Exception exception)
        {
            _log?.Error($"Could not delete abandoned retranscription scratch. meetingId={meetingId}; reason={reason}", exception);
        }
    }

    private void RemoveFlight(string meetingId, InFlightRetranscribe flight)
    {
        ((ICollection<KeyValuePair<string, InFlightRetranscribe>>)_flights)
            .Remove(new KeyValuePair<string, InFlightRetranscribe>(meetingId, flight));
    }

    private void AssertOriginalIntact(string meetingId, PersistedMeeting original)
    {
        var current = _store.Find(meetingId);
        if (current is null)
        {
            return;
        }

        if (!string.Equals(current.Transcript, original.Transcript, StringComparison.Ordinal))
        {
            TryRestore(original);
        }
    }

    private bool IsScratchBusy(string meetingId)
    {
        var scratch = _candidates.TryLoad(meetingId);
        if (scratch is null)
        {
            return false;
        }

        return string.Equals(scratch.Status, nameof(RetranscriptionCandidateStatus.InFlight), StringComparison.Ordinal) ||
               string.Equals(scratch.Status, nameof(RetranscriptionCandidateStatus.Ready), StringComparison.Ordinal);
    }

    private AudioResolve ResolveRetainedAudio(PersistedMeeting meeting) =>
        ResolveRetainedAudio(meeting, _captureStorage);

    private static AudioResolve ResolveRetainedAudio(PersistedMeeting meeting, CaptureStorageService? captureStorage)
    {
        var candidates = EnumerateAudioPaths(meeting).ToList();
        if (candidates.Count == 0)
        {
            return new AudioResolve(AudioResolveKind.Missing, null, null, 0);
        }

        IEnumerable<string> ordered = candidates;
        if (captureStorage is not null)
        {
            var owned = candidates
                .Where(path => captureStorage.IsOwnedMeetingAudioPath(meeting.Id, path))
                .ToList();
            if (owned.Count == 0)
            {
                // Imported SourcePath values and arbitrary user-selected files are deliberately
                // excluded from production retranscription. Only audio copied into the retained
                // per-meeting capture directory is eligible when the ownership guard is present.
                return new AudioResolve(AudioResolveKind.Missing, null, null, 0);
            }

            ordered = owned;
        }

        string? missingPath = null;
        foreach (var path in ordered)
        {
            if (!File.Exists(path))
            {
                missingPath ??= path;
                continue;
            }

            var info = new FileInfo(path);
            var fileName = info.Name;
            if (info.Length <= 0)
            {
                return new AudioResolve(AudioResolveKind.Corrupt, path, fileName, 0);
            }

            return new AudioResolve(AudioResolveKind.Ok, path, fileName, info.Length);
        }

        return new AudioResolve(AudioResolveKind.Missing, missingPath, missingPath is null ? null : Path.GetFileName(missingPath), 0);
    }

    private static IEnumerable<string> EnumerateAudioPaths(PersistedMeeting meeting)
    {
        if (!string.IsNullOrWhiteSpace(meeting.MicrophoneAudioPath))
        {
            yield return meeting.MicrophoneAudioPath;
        }

        if (!string.IsNullOrWhiteSpace(meeting.SystemAudioPath))
        {
            yield return meeting.SystemAudioPath;
        }

        if (string.IsNullOrWhiteSpace(meeting.SourcePath))
        {
            yield break;
        }

        foreach (var path in meeting.SourcePath.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            yield return path;
        }
    }

    private static PersistedMeeting WithTranscript(PersistedMeeting meeting, string transcript)
    {
        var aliases = CloneAliases(meeting);
        return meeting with
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Transcript = transcript,
            WordCount = CountWords(transcript) + CountWords(meeting.ManualNotes),
            Title = meeting.Title,
            TitleIsManual = meeting.TitleIsManual,
            ManualNotes = meeting.ManualNotes,
            Summary = meeting.Summary,
            SpeakerAliases = aliases
        };
    }

    private static PersistedMeeting Clone(PersistedMeeting meeting) =>
        meeting with { SpeakerAliases = CloneAliases(meeting) };

    private static Dictionary<string, string> CloneAliases(PersistedMeeting meeting) =>
        meeting.SpeakerAliases is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(meeting.SpeakerAliases, StringComparer.Ordinal);

    private static bool HasGeneratedNotes(PersistedMeeting meeting) =>
        !string.IsNullOrWhiteSpace(meeting.Summary);

    private static int CountWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static RetranscriptionCandidate ToCandidate(RetranscriptionScratchState scratch)
    {
        var status = scratch.Status switch
        {
            nameof(RetranscriptionCandidateStatus.Ready) => RetranscriptionCandidateStatus.Ready,
            nameof(RetranscriptionCandidateStatus.Abandoned) => RetranscriptionCandidateStatus.Abandoned,
            _ => RetranscriptionCandidateStatus.InFlight
        };
        return new RetranscriptionCandidate
        {
            CandidateId = scratch.CandidateId,
            MeetingId = scratch.MeetingId,
            Status = status,
            Transcript = scratch.Transcript ?? "",
            CreatedAtUtc = scratch.CreatedAtUtc,
            UpdatedAtUtc = scratch.UpdatedAtUtc,
            DurationMs = scratch.DurationMs,
            Error = scratch.Error
        };
    }

    private static TranscriptEditResult EditNotFound(string meetingId) => new()
    {
        Succeeded = false,
        Outcome = TranscriptEditOutcome.MeetingNotFound,
        Status = "That meeting could not be found. No transcript was changed.",
        Error = "meeting-not-found"
    };

    private static TranscriptEditResult EditBusy(string meetingId, string status, PersistedMeeting? meeting = null)
    {
        _ = meetingId;
        return new TranscriptEditResult
        {
            Succeeded = false,
            Outcome = TranscriptEditOutcome.Busy,
            Status = status,
            Error = "busy",
            Meeting = meeting
        };
    }

    private static RetranscriptionResult RetranscribeClosed(
        RetranscriptionOutcome outcome,
        string status,
        PersistedMeeting? meeting = null,
        string? error = null) => new()
    {
        Succeeded = false,
        AppliedToMeeting = false,
        Outcome = outcome,
        Status = status,
        Error = error,
        Meeting = meeting,
        TitleRemainsUserOwned = meeting?.TitleIsManual == true
    };

    private static string HonestException(Exception exception) =>
        exception switch
        {
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            FileNotFoundException => "missing-audio",
            DirectoryNotFoundException => "missing-audio",
            InvalidDataException => "corrupt-audio",
            EndOfStreamException => "corrupt-audio",
            IOException => "io-failure",
            _ => "asr-failed"
        };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record InFlightRetranscribe(string CandidateId, CancellationTokenSource Cancellation);

    private enum AudioResolveKind
    {
        Ok,
        Missing,
        Corrupt
    }

    private sealed record AudioResolve(AudioResolveKind Kind, string? Path, string? FileName, long ByteLength);
}
