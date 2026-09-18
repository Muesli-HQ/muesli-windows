namespace Muesli.Windows.Services;

/// <summary>
/// Persistence used by transcript edit and candidate retranscription. The integration owner should
/// back this with the in-memory meeting list plus <c>SaveMeetings</c>, not a second JSON writer
/// racing FeatureRuntime.
/// </summary>
public interface ITranscriptMeetingStore
{
    PersistedMeeting? Find(string meetingId);
    void Save(PersistedMeeting meeting);
}

/// <summary>
/// File ASR used only to produce a retranscription candidate. Implementations must not write the
/// meeting record; the service accepts or rejects the candidate after this returns.
/// </summary>
public interface IMeetingRetranscriptionAsr
{
    Task<TranscriptionResult> TranscribeOwnedAudioAsync(
        string filePath,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Optional diagnostics. Messages must never contain transcript body, title, or raw paths.</summary>
public interface ITranscriptEditDiagnostics
{
    void Info(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class AppLogTranscriptEditDiagnostics : ITranscriptEditDiagnostics
{
    private readonly AppLogService _log;

    public AppLogTranscriptEditDiagnostics(AppLogService log)
    {
        _log = log;
    }

    public void Info(string message) => _log.Info(message);

    public void Error(string message, Exception? exception = null) => _log.Error(message, exception);
}

/// <summary>
/// Wraps <see cref="NativeTranscriptionClient"/> for later FeatureRuntime wiring. The title passed
/// to the native client is a fixed role label, never the meeting title.
/// </summary>
public sealed class NativeMeetingRetranscriptionAsr : IMeetingRetranscriptionAsr
{
    private readonly NativeTranscriptionClient _client;

    public NativeMeetingRetranscriptionAsr(NativeTranscriptionClient client)
    {
        _client = client;
    }

    public Task<TranscriptionResult> TranscribeOwnedAudioAsync(
        string filePath,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken) =>
        _client.TranscribeFileAsync("retranscription", filePath, progress, cancellationToken);
}

public enum TranscriptEditOutcome
{
    Saved,
    Unchanged,
    Cancelled,
    SaveFailed,
    MeetingNotFound,
    Busy
}

public enum RetranscriptionOutcome
{
    CandidateReady,
    Accepted,
    Rejected,
    Cancelled,
    TimedOut,
    MissingAudio,
    CorruptAudio,
    AsrFailed,
    EmptyTranscript,
    PersistFailed,
    RecoveredInFlight,
    MeetingNotFound,
    Busy,
    EngineMissing,
    NoCandidate
}

public enum RetranscriptionCandidateStatus
{
    InFlight,
    Ready,
    Abandoned
}

/// <summary>
/// Snapshot taken when the user starts editing. Cancel and failed saves restore this transcript
/// rather than whatever draft was on screen.
/// </summary>
public sealed class TranscriptEditSession
{
    public required string MeetingId { get; init; }
    public required string OriginalTranscript { get; init; }
    public required bool HadGeneratedNotes { get; init; }
    public required string ManualNotesSnapshot { get; init; }
    public required bool TitleIsManual { get; init; }
    public required string TitleSnapshot { get; init; }
    public required IReadOnlyDictionary<string, string> AliasesSnapshot { get; init; }
}

public sealed record TranscriptEditResult
{
    public bool Succeeded { get; init; }

    /// <summary>True only when the persisted meeting transcript actually changed.</summary>
    public bool AppliedToMeeting { get; init; }

    public TranscriptEditOutcome Outcome { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public PersistedMeeting? Meeting { get; init; }
    public TranscriptEditSession? Session { get; init; }
    public bool GeneratedNotesStale { get; init; }
    public bool RequestsResummary { get; init; }
    public bool RestoredFromBackup { get; init; }
}

public sealed record RetranscriptionCandidate
{
    public required string CandidateId { get; init; }
    public required string MeetingId { get; init; }
    public required RetranscriptionCandidateStatus Status { get; init; }
    public string Transcript { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public int DurationMs { get; init; }
    public string? Error { get; init; }
}

public sealed record RetranscriptionResult
{
    public bool Succeeded { get; init; }

    /// <summary>
    /// True only after the user accepts a candidate. Cancellation, timeout, ASR failure, and a
    /// ready-but-unaccepted candidate never set this.
    /// </summary>
    public bool AppliedToMeeting { get; init; }

    public RetranscriptionOutcome Outcome { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public PersistedMeeting? Meeting { get; init; }
    public RetranscriptionCandidate? Candidate { get; init; }
    public bool GeneratedNotesStale { get; init; }
    public bool RequestsResummary { get; init; }
    public bool TitleRemainsUserOwned { get; init; }
}
