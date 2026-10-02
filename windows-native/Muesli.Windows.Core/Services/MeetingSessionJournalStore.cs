using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Muesli.Windows.Services;

public sealed record MeetingSessionJournal
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    // The immutable baseline makes resume finalization/recovery idempotent, including after a crash during save.
    public PersistedMeeting? ResumedMeeting { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public MeetingSessionState State { get; init; } = MeetingSessionState.Preparing;
    public string MicrophoneName { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? LiveModelId { get; init; }
    public string LiveTranscriptOwnership { get; init; } = "off";
    public string FinalTranscriptOwnerModelId { get; init; } = "";
    public string? GapRecoveryModelId { get; init; }
    public long LiveDroppedPacketCount { get; init; }
    public List<LiveTranscriptGap> LiveTranscriptGaps { get; init; } = [];
    public List<LiveTranscriptSegment> LiveTranscriptSegments { get; init; } = [];
    public bool RetainRecording { get; init; }
    public int? TargetProcessId { get; init; }
    public string SystemCaptureMode { get; init; } = "pending";
    public List<string> MicrophoneParts { get; init; } = [];
    public List<string> SystemParts { get; init; } = [];

    /// <summary>
    /// Wall-clock start of each part, in milliseconds after <see cref="StartedAtUtc"/>, parallel to
    /// the part lists. A channel that starts late, is repaired mid-meeting, or resumes after a
    /// suspend produces a concatenated track whose own timeline no longer equals meeting time;
    /// without these anchors the chronological merge silently misplaces that channel's speech.
    /// Schema 2 and earlier had no anchors and are migrated to contiguous offsets, which is
    /// exactly the behaviour they already assumed.
    /// </summary>
    public List<long> MicrophonePartOffsetsMs { get; init; } = [];
    public List<long> SystemPartOffsetsMs { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public string? LastFailureCategory { get; init; }
    public int MicrophoneRepairAttempts { get; init; }
    public int SystemRepairAttempts { get; init; }
    public bool RecoveredFromInterruption { get; init; }
}

public sealed record RecoverableMeetingSession(
    MeetingSessionJournal Journal,
    int MicrophonePartCount,
    int SystemPartCount,
    IReadOnlyList<string> RecoveryWarnings);

public sealed class MeetingSessionJournalStore
{
    internal static readonly TimeSpan MinimumUsableAudioDuration = TimeSpan.FromMilliseconds(100);
    private const string JournalFileName = "session.json";
    private const string MicrophoneLivePrefix = "meeting-mic-live-";
    private const string SystemLivePrefix = "meeting-system-live-";
    private readonly string _sessionsRoot;
    private readonly string _recordingsRoot;
    private readonly AtomicJsonFile _json;
    private readonly object _gate = new();

    public MeetingSessionJournalStore(
        string? captureDirectory = null,
        Action<string>? report = null)
    {
        var captureRoot = Path.GetFullPath(captureDirectory ??
            Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().CaptureDirectory);
        _sessionsRoot = Path.Combine(captureRoot, "in-progress");
        _recordingsRoot = Path.Combine(captureRoot, "recordings");
        _json = new AtomicJsonFile(report);
    }

    public static string MicrophoneCapturePrefix => MicrophoneLivePrefix;
    public static string SystemCapturePrefix => SystemLivePrefix;

    public MeetingSessionJournal Create(
        string sessionId,
        string title,
        DateTimeOffset startedAt,
        string microphoneName,
        string modelId,
        bool retainRecording,
        int? targetProcessId,
        string? liveModelId = null,
        LiveTranscriptOwnershipMode? liveOwnership = null)
    {
        ValidateSessionId(sessionId);
        lock (_gate)
        {
            var directory = SessionDirectory(sessionId);
            if (Directory.Exists(directory))
            {
                throw new InvalidOperationException("A meeting journal with this ID already exists.");
            }
            Directory.CreateDirectory(directory);
            var journal = new MeetingSessionJournal
            {
                SessionId = sessionId,
                Title = title,
                StartedAtUtc = startedAt,
                UpdatedAtUtc = startedAt,
                State = MeetingSessionState.Preparing,
                MicrophoneName = microphoneName,
                ModelId = modelId,
                LiveModelId = liveModelId,
                LiveTranscriptOwnership = liveModelId is null
                    ? "off"
                    : LiveTranscriptOwnershipDescriptor.SettingValueFor(
                        liveOwnership ?? LiveTranscriptOwnershipMode.PreviewOnly),
                FinalTranscriptOwnerModelId = liveModelId is not null && liveOwnership == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? liveModelId : modelId,
                GapRecoveryModelId = liveModelId is not null && liveOwnership == LiveTranscriptOwnershipMode.UnifiedLiveAndFinal ? modelId : null,
                RetainRecording = retainRecording,
                TargetProcessId = targetProcessId
            };
            SaveUnderGate(journal);
            return journal;
        }
    }

    public void Save(MeetingSessionJournal journal)
    {
        ValidateJournal(journal);
        lock (_gate)
        {
            SaveUnderGate(journal with { UpdatedAtUtc = DateTimeOffset.UtcNow });
        }
    }

    public string GetSessionDirectory(string sessionId)
    {
        ValidateSessionId(sessionId);
        var directory = SessionDirectory(sessionId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public MeetingSessionJournal AppendPart(
        MeetingSessionJournal journal,
        MeetingAudioChannel channel,
        string sourcePath,
        long? startOffsetMs = null)
    {
        ValidateJournal(journal);
        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException("Captured meeting audio part was not found.", fullSource);
        }
        if (!HasUsableAudio(fullSource))
        {
            return journal;
        }

        lock (_gate)
        {
            var existing = channel == MeetingAudioChannel.Microphone
                ? journal.MicrophoneParts
                : journal.SystemParts;
            var existingOffsets = channel == MeetingAudioChannel.Microphone
                ? journal.MicrophonePartOffsetsMs
                : journal.SystemPartOffsetsMs;
            var prefix = channel == MeetingAudioChannel.Microphone ? "microphone-part" : "system-part";
            var relativeName = $"{prefix}-{existing.Count + 1:D4}.wav";
            var destination = Path.Combine(SessionDirectory(journal.SessionId), relativeName);
            AtomicCopy(fullSource, destination);

            var updatedParts = existing.Append(relativeName).ToList();
            // No caller-supplied anchor means "immediately after the previous part", which is the
            // contiguous behaviour every pre-schema-3 journal already assumed.
            var anchor = startOffsetMs ?? ContiguousAnchorUnderGate(journal, channel);
            var updatedOffsets = existingOffsets.Append(Math.Max(0, anchor)).ToList();
            var updated = channel == MeetingAudioChannel.Microphone
                ? journal with
                {
                    MicrophoneParts = updatedParts,
                    MicrophonePartOffsetsMs = updatedOffsets,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }
                : journal with
                {
                    SystemParts = updatedParts,
                    SystemPartOffsetsMs = updatedOffsets,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            SaveUnderGate(updated);
            return updated;
        }
    }

    /// <summary>Measured duration of each retained part, in capture order.</summary>
    public IReadOnlyList<long> PartDurationsMs(MeetingSessionJournal journal, MeetingAudioChannel channel)
    {
        ValidateJournal(journal);
        var parts = channel == MeetingAudioChannel.Microphone ? journal.MicrophoneParts : journal.SystemParts;
        var directory = SessionDirectory(journal.SessionId);
        var durations = new List<long>(parts.Count);
        foreach (var part in parts)
        {
            try
            {
                using var reader = new WaveFileReader(Path.Combine(directory, part));
                durations.Add((long)reader.TotalTime.TotalMilliseconds);
            }
            catch
            {
                durations.Add(0);
            }
        }
        return durations;
    }

    /// <summary>Meeting-time placement of every retained part for one channel.</summary>
    public IReadOnlyList<MeetingTrackPart> BuildTrackTimeline(MeetingSessionJournal journal, MeetingAudioChannel channel) =>
        MeetingTranscriptTimeline.Build(
            PartDurationsMs(journal, channel),
            channel == MeetingAudioChannel.Microphone ? journal.MicrophonePartOffsetsMs : journal.SystemPartOffsetsMs);

    private long ContiguousAnchorUnderGate(MeetingSessionJournal journal, MeetingAudioChannel channel)
    {
        var timeline = MeetingTranscriptTimeline.Build(
            PartDurationsMs(journal, channel),
            channel == MeetingAudioChannel.Microphone ? journal.MicrophonePartOffsetsMs : journal.SystemPartOffsetsMs);
        return timeline.Count == 0 ? 0 : timeline[^1].StartOffsetMs + timeline[^1].DurationMs;
    }

    public IReadOnlyList<RecoverableMeetingSession> DiscoverRecoverable()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_sessionsRoot))
            {
                return [];
            }

            var recovered = new List<RecoverableMeetingSession>();
            foreach (var directory in Directory.EnumerateDirectories(_sessionsRoot))
            {
                var sessionId = Path.GetFileName(directory);
                if (!IsValidSessionId(sessionId))
                {
                    continue;
                }

                var path = Path.Combine(directory, JournalFileName);
                var result = _json.Load<MeetingSessionJournal?>(path, null);
                if (result.Value is not { } journal ||
                    !journal.SessionId.Equals(sessionId, StringComparison.Ordinal) ||
                    journal.SchemaVersion is < 1 or > MeetingSessionJournal.CurrentSchemaVersion ||
                    journal.State is MeetingSessionState.Completed or MeetingSessionState.Cancelled)
                {
                    continue;
                }

                var warnings = new List<string>();
                journal = AdoptLiveFilesUnderGate(journal, MicrophoneLivePrefix, MeetingAudioChannel.Microphone, warnings);
                journal = AdoptLiveFilesUnderGate(journal, SystemLivePrefix, MeetingAudioChannel.System, warnings);
                journal = journal with
                {
                    State = MeetingSessionState.RecoverableInterruption,
                    RecoveredFromInterruption = true,
                    Warnings = journal.Warnings
                        .Concat(warnings)
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                SaveUnderGate(journal);

                if (journal.MicrophoneParts.Count > 0 || journal.SystemParts.Count > 0)
                {
                    recovered.Add(new RecoverableMeetingSession(
                        journal,
                        journal.MicrophoneParts.Count,
                        journal.SystemParts.Count,
                        warnings));
                }
            }

            return recovered
                .OrderBy(item => item.Journal.StartedAtUtc)
                .ToList();
        }
    }

    public MeetingAudioPaths BuildFinalTracks(MeetingSessionJournal journal)
    {
        ValidateJournal(journal);
        lock (_gate)
        {
            if (journal.ResumedMeeting is { } resumed) ValidateSessionId(resumed.Id);
            var destinationDirectory = journal.RetainRecording
                ? Path.Combine(_recordingsRoot, journal.ResumedMeeting?.Id ?? journal.SessionId)
                : SessionDirectory(journal.SessionId);
            Directory.CreateDirectory(destinationDirectory);
            var suffix = journal.ResumedMeeting is null ? "" : $"-resume-{journal.SessionId}";
            var micPath = CombineTrackUnderGate(
                journal,
                journal.MicrophoneParts,
                Path.Combine(destinationDirectory, journal.RetainRecording ? $"microphone{suffix}.wav" : "microphone-final.wav"));
            var systemPath = CombineTrackUnderGate(
                journal,
                journal.SystemParts,
                Path.Combine(destinationDirectory, journal.RetainRecording ? $"system{suffix}.wav" : "system-final.wav"));
            return new MeetingAudioPaths(micPath, systemPath);
        }
    }

    public MeetingAudioPaths BuildResumedPlaybackTracks(MeetingSessionJournal journal, string? microphone, string? system)
    {
        if (!journal.RetainRecording || journal.ResumedMeeting is not { } prior) return new(microphone, system);
        lock (_gate) return new(AppendPriorTrack(prior.MicrophoneAudioPath, microphone), AppendPriorTrack(prior.SystemAudioPath, system));
    }

    public void DeleteSession(string sessionId)
    {
        ValidateSessionId(sessionId);
        lock (_gate)
        {
            var directory = SessionDirectory(sessionId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private MeetingSessionJournal AdoptLiveFilesUnderGate(
        MeetingSessionJournal journal,
        string prefix,
        MeetingAudioChannel channel,
        List<string> warnings)
    {
        var directory = SessionDirectory(journal.SessionId);
        foreach (var path in Directory.EnumerateFiles(directory, $"{prefix}*.wav", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var headerRepaired = WavCrashRecovery.RepairHeader(path);
                if (!HasUsableAudio(path))
                {
                    TryDelete(path);
                    warnings.Add($"{ChannelLabel(channel)} interrupted track contained no usable audio and was discarded.");
                    continue;
                }

                if (headerRepaired)
                {
                    warnings.Add($"{ChannelLabel(channel)} WAV header was repaired after an interrupted recording.");
                }

                journal = AppendPartUnderGate(journal, channel, path);
                TryDelete(path);
            }
            catch (Exception exception)
            {
                warnings.Add($"{ChannelLabel(channel)} interrupted track could not be repaired ({exception.GetType().Name}).");
            }
        }
        return journal;
    }

    private MeetingSessionJournal AppendPartUnderGate(
        MeetingSessionJournal journal,
        MeetingAudioChannel channel,
        string sourcePath)
    {
        var existing = channel == MeetingAudioChannel.Microphone
            ? journal.MicrophoneParts
            : journal.SystemParts;
        var prefix = channel == MeetingAudioChannel.Microphone ? "microphone-part" : "system-part";
        var relativeName = $"{prefix}-{existing.Count + 1:D4}.wav";
        var destination = Path.Combine(SessionDirectory(journal.SessionId), relativeName);
        AtomicCopy(sourcePath, destination);
        return channel == MeetingAudioChannel.Microphone
            ? journal with { MicrophoneParts = existing.Append(relativeName).ToList() }
            : journal with { SystemParts = existing.Append(relativeName).ToList() };
    }

    private string? CombineTrackUnderGate(
        MeetingSessionJournal journal,
        IReadOnlyList<string> relativeParts,
        string destinationPath)
    {
        if (relativeParts.Count == 0)
        {
            return null;
        }

        var sources = relativeParts
            .Select(part => ResolveOwnedPart(journal.SessionId, part))
            .Where(HasUsableAudio)
            .ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        return CombineAudioFiles(sources, destinationPath);
    }

    private static string? AppendPriorTrack(string? prior, string? current)
    {
        if (string.IsNullOrWhiteSpace(prior) || !File.Exists(prior)) return current;
        if (current is null) return prior;
        var combined = Path.Combine(Path.GetDirectoryName(current)!, Path.GetFileNameWithoutExtension(current) + "-combined.wav");
        return CombineAudioFiles([prior, current], combined);
    }

    private static string CombineAudioFiles(IReadOnlyList<string> sources, string destinationPath)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        var readers = new List<AudioFileReader>();
        try
        {
            var providers = new List<ISampleProvider>();
            foreach (var source in sources)
            {
                WavCrashRecovery.RepairHeader(source);
                var reader = new AudioFileReader(source);
                readers.Add(reader);
                ISampleProvider provider = reader;
                if (provider.WaveFormat.Channels == 2)
                {
                    provider = new StereoToMonoSampleProvider(provider);
                }
                else if (provider.WaveFormat.Channels > 2)
                {
                    var mono = new MultiplexingSampleProvider([provider], 1);
                    mono.ConnectInputToOutput(0, 0);
                    provider = mono;
                }
                if (provider.WaveFormat.SampleRate != 16000)
                {
                    provider = new WdlResamplingSampleProvider(provider, 16000);
                }
                providers.Add(provider);
            }

            WaveFileWriter.CreateWaveFile16(temporaryPath, new ConcatenatingSampleProvider(providers));
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            // Release readers before replacing the newly assembled track, including on Windows.
            foreach (var reader in readers) reader.Dispose();
            readers.Clear();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
            TryDelete(temporaryPath);
        }
    }

    private string ResolveOwnedPart(string sessionId, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Meeting journal contains an unsafe audio path.");
        }

        var directory = EnsureTrailingSeparator(SessionDirectory(sessionId));
        var fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!fullPath.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Meeting journal audio path escaped its session directory.");
        }
        return fullPath;
    }

    private void SaveUnderGate(MeetingSessionJournal journal)
    {
        ValidateJournal(journal);
        Directory.CreateDirectory(SessionDirectory(journal.SessionId));
        _json.Save(Path.Combine(SessionDirectory(journal.SessionId), JournalFileName), journal);
    }

    private string SessionDirectory(string sessionId) => Path.Combine(_sessionsRoot, sessionId);

    private static void AtomicCopy(string sourcePath, string destinationPath)
    {
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            using (var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ValidateJournal(MeetingSessionJournal journal)
    {
        ValidateSessionId(journal.SessionId);
        if (journal.SchemaVersion is < 1 or > MeetingSessionJournal.CurrentSchemaVersion)
        {
            throw new InvalidDataException("Meeting journal schema is unsupported.");
        }
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException(
                "Meeting session IDs may contain only letters, digits, underscores, and hyphens.",
                nameof(sessionId));
        }
    }

    private static bool IsValidSessionId(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) &&
        sessionId.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string ChannelLabel(MeetingAudioChannel channel) =>
        channel == MeetingAudioChannel.Microphone ? "Microphone" : "System audio";

    internal static bool HasUsableAudio(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length <= 44)
            {
                return false;
            }

            WavCrashRecovery.RepairHeader(path);
            using var reader = new WaveFileReader(path);
            return reader.TotalTime >= MinimumUsableAudioDuration;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; a locked artifact can be retried later.
        }
    }
}

public static class WavCrashRecovery
{
    public static bool RepairHeader(string path)
    {
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length <= 44 || stream.Length > uint.MaxValue)
        {
            return false;
        }

        var headerLength = (int)Math.Min(stream.Length, 4096);
        var header = new byte[headerLength];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var dataOffset = FindDataChunk(header);
        if (dataOffset < 0)
        {
            return false;
        }

        var expectedRiffSize = checked((uint)(stream.Length - 8));
        var expectedDataSize = checked((uint)(stream.Length - dataOffset - 8));
        var currentRiffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var currentDataSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(dataOffset + 4, 4));
        if (currentRiffSize == expectedRiffSize && currentDataSize == expectedDataSize)
        {
            return false;
        }

        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, expectedRiffSize);
        stream.Position = 4;
        stream.Write(value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, expectedDataSize);
        stream.Position = dataOffset + 4;
        stream.Write(value);
        stream.Flush(flushToDisk: true);
        return true;
    }

    private static int FindDataChunk(ReadOnlySpan<byte> header)
    {
        for (var index = 12; index + 8 <= header.Length; index += 2)
        {
            if (header.Slice(index, 4).SequenceEqual("data"u8))
            {
                return index;
            }
        }
        return -1;
    }
}
