using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Meeting-finalization pipeline: timestamp normalization, chronological channel merge, speaker
/// identity, degraded-channel behaviour, and duplicate handling.
/// </summary>
public sealed class Phase5FinalizationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DateTime Start = new(2026, 1, 1, 9, 0, 0);

    private static TranscriptSegment Seg(int startMs, int endMs, string text, string speaker = "You") =>
        new(Guid.NewGuid().ToString("N"), speaker, startMs, endMs, text);

    // ---- timestamp normalization -------------------------------------------------

    [Fact]
    public void ContiguousPartsLeaveTrackTimeEqualToMeetingTime()
    {
        var parts = MeetingTranscriptTimeline.Contiguous([5000, 3000]);
        Assert.Equal(0, parts[0].StartOffsetMs);
        Assert.Equal(5000, parts[1].StartOffsetMs);
        Assert.Equal(0, MeetingTranscriptTimeline.ToMeetingMs(0, parts));
        Assert.Equal(6000, MeetingTranscriptTimeline.ToMeetingMs(6000, parts));
    }

    [Fact]
    public void ALateStartingChannelIsNotFoldedBackToTheStartOfTheMeeting()
    {
        // System audio only became available 20 s in; its own track still starts at 0.
        var parts = MeetingTranscriptTimeline.Build([10000], [20000]);
        Assert.Equal(20000, MeetingTranscriptTimeline.ToMeetingMs(0, parts));
        Assert.Equal(25000, MeetingTranscriptTimeline.ToMeetingMs(5000, parts));
    }

    [Fact]
    public void AMidMeetingRestartKeepsTheInterruptionAsRealElapsedTime()
    {
        // 10 s captured, 15 s lost to a capture fault, then 10 s more.
        var parts = MeetingTranscriptTimeline.Build([10000, 10000], [0, 25000]);
        Assert.Equal(9000, MeetingTranscriptTimeline.ToMeetingMs(9000, parts));
        // Track time 10 000 is the first sample of the second part, i.e. meeting time 25 000.
        Assert.Equal(25000, MeetingTranscriptTimeline.ToMeetingMs(10000, parts));
        Assert.Equal(30000, MeetingTranscriptTimeline.ToMeetingMs(15000, parts));
    }

    [Fact]
    public void RecordedAnchorsNeverOverlapPreviouslyCapturedAudio()
    {
        // A bogus anchor that would rewind into the previous part is clamped forward.
        var parts = MeetingTranscriptTimeline.Build([10000, 5000], [0, 2000]);
        Assert.Equal(10000, parts[1].StartOffsetMs);
    }

    [Fact]
    public void MissingAnchorsFallBackToContiguousPlacementRatherThanDroppingParts()
    {
        var parts = MeetingTranscriptTimeline.Build([4000, 4000, 4000], [0]);
        Assert.Equal(3, parts.Count);
        Assert.Equal(4000, parts[1].StartOffsetMs);
        Assert.Equal(8000, parts[2].StartOffsetMs);
    }

    [Fact]
    public void NormalizationShiftsBothTranscriptAndDiarizationOntoMeetingTime()
    {
        var parts = MeetingTranscriptTimeline.Build([10000], [20000]);
        var segments = MeetingTranscriptTimeline.Normalize([Seg(0, 1000, "late start")], parts);
        Assert.Equal(20000, segments[0].StartMs);
        Assert.Equal(21000, segments[0].EndMs);

        var diarized = MeetingTranscriptTimeline.Normalize([new DiarizedSegment("a", 0, 1000)], parts);
        Assert.Equal(20000, diarized[0].StartMs);
        Assert.Equal(21000, diarized[0].EndMs);
    }

    [Fact]
    public void NormalizationIsAnIdentityForAsingleContiguousPart()
    {
        var parts = MeetingTranscriptTimeline.Build([10000], [0]);
        var original = Seg(1234, 5678, "unchanged");
        var normalized = Assert.Single(MeetingTranscriptTimeline.Normalize([original], parts));
        Assert.Equal(original.StartMs, normalized.StartMs);
        Assert.Equal(original.EndMs, normalized.EndMs);
    }

    [Fact]
    public void LateSystemAudioMergesAfterTheLocalSpeakerItActuallyFollowed()
    {
        var systemParts = MeetingTranscriptTimeline.Build([5000], [30000]);
        var merged = TranscriptFormatter.Merge(
            [Seg(0, 4000, "I will start us off")],
            MeetingTranscriptTimeline.Normalize([Seg(0, 3000, "thanks for the intro", "System")], systemParts),
            MeetingTranscriptTimeline.Normalize([new DiarizedSegment("remote-a", 0, 3000)], systemParts),
            Start);

        var lines = merged.Split('\n');
        Assert.StartsWith("[09:00:00] You:", lines[0]);
        Assert.Contains("I will start us off", lines[0]);
        Assert.StartsWith("[09:00:30] Speaker 1:", lines[1]);
        // Without normalization the remote line would have been placed at 09:00:00 and sorted first.
    }

    // ---- chronological merge and speaker identity --------------------------------

    [Fact]
    public void OneLocalAndOneRemoteSpeakerInterleaveChronologically()
    {
        var merged = TranscriptFormatter.Merge(
            [Seg(0, 2000, "hello there"), Seg(8000, 10000, "sounds good")],
            [Seg(3000, 6000, "hi, thanks for joining", "System")],
            [new DiarizedSegment("remote-a", 3000, 6000)],
            Start);

        var lines = merged.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("You: hello there", lines[0]);
        Assert.Contains("Speaker 1: hi, thanks for joining", lines[1]);
        Assert.Contains("You: sounds good", lines[2]);
    }

    [Fact]
    public void MultipleRemoteSpeakersKeepDistinctStableIdentities()
    {
        var merged = TranscriptFormatter.Merge(
            [],
            [
                Seg(0, 2000, "first remote", "System"),
                Seg(3000, 5000, "second remote", "System"),
                Seg(6000, 8000, "first again", "System")
            ],
            [
                new DiarizedSegment("spk-a", 0, 2000),
                new DiarizedSegment("spk-b", 3000, 5000),
                new DiarizedSegment("spk-a", 6000, 8000)
            ],
            Start);

        Assert.Contains("Speaker 1: first remote", merged);
        Assert.Contains("Speaker 2: second remote", merged);
        Assert.Contains("Speaker 1: first again", merged);
        Assert.DoesNotContain("Speaker 3", merged);
    }

    [Fact]
    public void SpeakerNumberingFollowsFirstAppearanceRegardlessOfDiarizationOrder()
    {
        var diarization = new List<DiarizedSegment>
        {
            new("zzz-late-id", 9000, 11000),
            new("aaa-early-id", 0, 2000)
        };
        var merged = TranscriptFormatter.Merge(
            [],
            [Seg(0, 2000, "earliest", "System"), Seg(9000, 11000, "latest", "System")],
            diarization,
            Start);

        // Identity must come from meeting time, not from the order the diarizer emitted rows.
        Assert.Contains("Speaker 1: earliest", merged);
        Assert.Contains("Speaker 2: latest", merged);
    }

    [Fact]
    public void OverlappingSpeechIsAttributedByGreatestOverlapNotFirstMatch()
    {
        var merged = TranscriptFormatter.Merge(
            [],
            [Seg(4000, 6000, "overlapped line", "System")],
            [
                new DiarizedSegment("spk-a", 0, 4200),   // 200 ms of overlap
                new DiarizedSegment("spk-b", 4100, 6000) // 1900 ms of overlap
            ],
            Start);

        Assert.Contains("Speaker 2: overlapped line", merged);
        Assert.DoesNotContain("Speaker 1: overlapped line", merged);
    }

    [Fact]
    public void RemoteSpeechWithNoDiarizationOverlapFallsBackToOthersRatherThanAWrongSpeaker()
    {
        var merged = TranscriptFormatter.Merge(
            [],
            [Seg(50000, 52000, "unattributed", "System")],
            [new DiarizedSegment("spk-a", 0, 2000)],
            Start);

        Assert.Contains("Others: unattributed", merged);
    }

    [Fact]
    public void SilenceBetweenTurnsSplitsLinesInsteadOfRunningThemTogether()
    {
        var merged = TranscriptFormatter.Merge(
            [Seg(0, 1000, "before the pause"), Seg(60000, 61000, "after the pause")],
            [],
            [new DiarizedSegment("spk-a", 0, 1000)],
            Start);

        var lines = merged.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("[09:00:00]", lines[0]);
        Assert.StartsWith("[09:01:00]", lines[1]);
    }

    [Fact]
    public void MissingMicrophoneStillProducesAnAttributedRemoteTranscript()
    {
        var merged = TranscriptFormatter.Merge(
            [],
            [Seg(0, 2000, "remote only", "System")],
            [new DiarizedSegment("spk-a", 0, 2000)],
            Start);

        Assert.Contains("Speaker 1: remote only", merged);
        Assert.DoesNotContain("You:", merged);
    }

    [Fact]
    public void MissingSystemAudioStillProducesTheLocalTranscript()
    {
        var merged = TranscriptFormatter.Merge(
            [Seg(0, 2000, "local only")],
            [],
            [],
            Start);

        Assert.Contains("local only", merged);
        Assert.DoesNotContain("Speaker 1", merged);
    }

    [Fact]
    public void BothChannelsMissingProducesAnEmptyTranscriptRatherThanAFabricatedLine()
    {
        Assert.Equal("", TranscriptFormatter.Merge([], [], [], Start));
        Assert.Equal("", TranscriptFormatter.Merge([], [], [new DiarizedSegment("spk-a", 0, 1000)], Start));
    }

    [Fact]
    public void DiarizationFailureFallsBackToLabelledChannelsWithoutLosingAnySpeech()
    {
        var merged = TranscriptFormatter.Merge(
            [Seg(0, 2000, "my words")],
            [Seg(3000, 5000, "their words", "System")],
            [],
            Start);

        Assert.Contains("my words", merged);
        Assert.Contains("their words", merged);
        Assert.Contains("[You]", merged);
        Assert.Contains("[System audio]", merged);
        // No invented speaker identities when diarization produced nothing.
        Assert.DoesNotContain("Speaker 1", merged);
    }

    [Fact]
    public void ConsecutiveTurnsFromOneSpeakerConsolidateButSpeakerChangesDoNot()
    {
        var merged = TranscriptFormatter.Merge(
            [],
            [
                Seg(0, 1000, "first part", "System"),
                Seg(1500, 2500, "second part", "System"),
                Seg(3000, 4000, "other person", "System")
            ],
            [
                new DiarizedSegment("spk-a", 0, 2500),
                new DiarizedSegment("spk-b", 3000, 4000)
            ],
            Start);

        var lines = merged.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("first part", lines[0]);
        Assert.Contains("second part", lines[0]);
        Assert.Contains("Speaker 2: other person", lines[1]);
    }

    [Fact]
    public void MergeIsDeterministicAcrossRepeatedRuns()
    {
        List<TranscriptSegment> Mic() => [Seg(0, 2000, "alpha"), Seg(5000, 6000, "gamma")];
        List<TranscriptSegment> System() => [Seg(2500, 4000, "beta", "System")];
        List<DiarizedSegment> Diar() => [new("spk-a", 2500, 4000)];

        var first = TranscriptFormatter.Merge(Mic(), System(), Diar(), Start);
        for (var run = 0; run < 5; run++)
        {
            Assert.Equal(first, TranscriptFormatter.Merge(Mic(), System(), Diar(), Start));
        }
    }

    // ---- duplicate handling ------------------------------------------------------

    [Fact]
    public async Task ImmediateReEmissionOfTheSameTextIsDeduplicated()
    {
        var recognizer = new RepeatRecognizer("yes");
        var vad = new FixedVad([(0, 8000), (8000, 16000)]);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        var result = await session.FinishAsync();

        Assert.Single(result.Committed, segment => segment.Channel == LiveTranscriptChannel.Microphone);
    }

    [Fact]
    public async Task LegitimateRepeatedSpeechAfterAPauseIsKept()
    {
        var recognizer = new RepeatRecognizer("no");
        // Two identical utterances separated by well over the duplicate window.
        var vad = new FixedVad([(0, 8000)], [(160000, 168000)]);
        await using var session = new MeetingLiveTranscriptionSession(
            recognizer, vad, "model", LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        session.TryEnqueue(new(LiveTranscriptChannel.Microphone, [0.1f], 0));
        var result = await session.FinishAsync();

        var microphone = result.Committed.Where(segment => segment.Channel == LiveTranscriptChannel.Microphone).ToList();
        Assert.Equal(2, microphone.Count);
        Assert.All(microphone, segment => Assert.Equal("no", segment.Text));
    }

    // ---- speaker prefixes through cleanup and dictionary -------------------------

    [Fact]
    public void DictionaryCorrectionRewritesSpeechButNeverTheSpeakerPrefix()
    {
        var entries = new[]
        {
            new DictionaryEntryRecord { Phrase = "Muesli", Replacement = "Müsli", MatchingThreshold = 0.85 }
        };
        var corrected = TranscriptionPipelineService.ApplyDictionaryPreservingSpeakerPrefixes(
            "[09:00:00] Speaker 1: we shipped muesli today\n[09:00:05] You: muesli again",
            entries);

        Assert.Contains("[09:00:00] Speaker 1: we shipped Müsli today", corrected);
        Assert.Contains("[09:00:05] You: Müsli again", corrected);
    }

    [Fact]
    public void ASpeakerNamedLikeADictionaryPhraseKeepsItsPrefixIntact()
    {
        var entries = new[]
        {
            new DictionaryEntryRecord { Phrase = "Speaker 1", Replacement = "REPLACED", MatchingThreshold = 0.85 }
        };
        var corrected = TranscriptionPipelineService.ApplyDictionaryPreservingSpeakerPrefixes(
            "[09:00:00] Speaker 1: hello", entries);

        Assert.StartsWith("[09:00:00] Speaker 1:", corrected);
    }

    [Fact]
    public void CleanupOnlyRunsInPrefixPreservingModeForSpeakerTranscripts()
    {
        Assert.True(NativeTextCleanupService.LooksLikeSpeakerTranscript("[09:00:00] You: hello\n[09:00:02] Speaker 1: hi"));
        Assert.False(NativeTextCleanupService.LooksLikeSpeakerTranscript("just a plain dictation sentence"));
    }

    [Fact]
    public void SpeakerAliasesRenameIdentitiesWithoutTouchingLongerSpeakerNumbers()
    {
        var aliased = SpeakerAliasService.Apply(
            "[09:00:00] Speaker 1: hello\n[09:00:02] Speaker 12: still numbered",
            new Dictionary<string, string> { ["Speaker 1"] = "Priya" });

        Assert.Contains("Priya: hello", aliased);
        Assert.Contains("Speaker 12: still numbered", aliased);
    }

    // ---- health warnings ---------------------------------------------------------

    [Fact]
    public void ASuccessfulDiarizedMergeClearsAStaleDiarizationFailureWarning()
    {
        const string diarizationFailed = "Speaker diarization failed; transcript used fallback speaker labels.";

        var withSpeakers = MeetingRecordingCoordinator.CleanupHealthWarnings(
            [diarizationFailed],
            "[09:00:00] Speaker 1: real content",
            diarizationSucceededWithSegments: true);
        Assert.DoesNotContain(diarizationFailed, withSpeakers);

        // A genuinely failed diarization must keep its warning attached to the record.
        var withoutSpeakers = MeetingRecordingCoordinator.CleanupHealthWarnings(
            [diarizationFailed],
            "[You] fallback only",
            diarizationSucceededWithSegments: false);
        Assert.Contains(withoutSpeakers, warning => warning.Contains("diarization", StringComparison.OrdinalIgnoreCase));
    }

    // ---- journal schema 3 migration ----------------------------------------------

    [Fact]
    public void LegacyJournalsWithoutPartAnchorsMigrateToContiguousTimelines()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_migrate", "Legacy", DateTimeOffset.UtcNow, "Mic", "parakeet-v3", true, null);

        var first = directory.File("first.wav");
        var second = directory.File("second.wav");
        WriteSilentWav(first, seconds: 2);
        WriteSilentWav(second, seconds: 3);

        // A pre-schema-3 writer supplied no anchor at all.
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, first);
        journal = store.AppendPart(journal, MeetingAudioChannel.Microphone, second);

        Assert.Equal(MeetingSessionJournal.CurrentSchemaVersion, journal.SchemaVersion);
        Assert.Equal(2, journal.MicrophoneParts.Count);
        Assert.Equal(2, journal.MicrophonePartOffsetsMs.Count);

        var timeline = store.BuildTrackTimeline(journal, MeetingAudioChannel.Microphone);
        Assert.Equal(0, timeline[0].StartOffsetMs);
        // Contiguous placement: exactly the behaviour schema 2 already assumed.
        Assert.InRange(timeline[1].StartOffsetMs, 1900, 2100);
        Assert.Equal(0, MeetingTranscriptTimeline.ToMeetingMs(0, timeline));
    }

    [Fact]
    public void RecordedAnchorsSurviveAJournalRoundTrip()
    {
        using var directory = new TestDirectory();
        var store = new MeetingSessionJournalStore(directory.Path);
        var journal = store.Create("meet_anchor", "Anchored", DateTimeOffset.UtcNow, "Mic", "parakeet-v3", true, null);
        var late = directory.File("late.wav");
        WriteSilentWav(late, seconds: 2);

        journal = store.AppendPart(journal, MeetingAudioChannel.System, late, startOffsetMs: 30000);
        var timeline = store.BuildTrackTimeline(journal, MeetingAudioChannel.System);

        Assert.Equal(30000, journal.SystemPartOffsetsMs[0]);
        Assert.Equal(30000, timeline[0].StartOffsetMs);
        Assert.Equal(30000, MeetingTranscriptTimeline.ToMeetingMs(0, timeline));
    }

    private static void WriteSilentWav(string path, int seconds)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        var bytes = new byte[16000 * 2 * seconds];
        writer.Write(bytes, 0, bytes.Length);
    }

    // ---- real multi-speaker qualification ----------------------------------------

    /// <summary>
    /// Real remote-speaker diarization over genuinely different human voices. Gated on
    /// MUESLI_MULTISPEAKER_FIXTURE_SOURCE, a directory of single-speaker WAV files that are
    /// concatenated into one multi-speaker system track (A, B, C, then A again). A single-speaker
    /// pass proves nothing about speaker separation, so this asserts distinct identities and that
    /// the returning speaker is recognised as the same person.
    /// </summary>
    [QualificationFact("MUESLI_MULTISPEAKER_FIXTURE_SOURCE")]
    public async Task RealMultiSpeakerAudioProducesDistinctAndStableRemoteIdentities()
    {
        var source = Environment.GetEnvironmentVariable("MUESLI_MULTISPEAKER_FIXTURE_SOURCE")!;
        Assert.True(NativeDiarizationClient.IsRuntimeAvailable, "The native diarization runtime is unavailable on this machine.");

        var voices = Directory.EnumerateFiles(source, "*.wav").OrderBy(path => path).Take(3).ToList();
        Assert.True(voices.Count >= 2, "The multi-speaker fixture source needs at least two distinct voices.");

        using var directory = new TestDirectory();
        var fixture = directory.File("multi-speaker-system.wav");
        // A, B, [C], then A again so a returning speaker can be checked for a stable identity.
        var order = voices.Concat([voices[0]]).ToList();
        var boundaries = ConcatenateWithGaps(order, fixture, gapMs: 700);
        output.WriteLine($"fixture voices: {string.Join(", ", order.Select(Path.GetFileName))}");
        foreach (var (name, startMs, endMs) in boundaries)
            output.WriteLine($"  planted {name}: {startMs}-{endMs} ms");

        using var client = new NativeDiarizationClient();
        var diarization = await client.DiarizeFileAsync(fixture);
        var segments = diarization.Segments ?? [];
        output.WriteLine($"diarization provider={diarization.Provider}; segments={segments.Count}; processingMs={diarization.ProcessingMs}");
        foreach (var segment in segments)
            output.WriteLine($"  {segment.SpeakerId}: {segment.StartMs}-{segment.EndMs} ms");

        var distinct = segments.Select(segment => segment.SpeakerId).Distinct(StringComparer.Ordinal).Count();
        Assert.True(distinct >= 2, $"Multi-speaker diarization must separate at least two speakers; found {distinct}.");

        // The concatenated voices become the remote channel of a real merge.
        var systemSegments = boundaries
            .Select((planted, index) => new TranscriptSegment(
                $"s{index}", "System", planted.StartMs, planted.EndMs, $"utterance {index + 1}"))
            .ToList();
        var merged = TranscriptFormatter.Merge(
            [new TranscriptSegment("m0", "You", 0, 200, "local opening")],
            systemSegments,
            segments.ToList(),
            Start);
        output.WriteLine("merged transcript:");
        foreach (var line in merged.Split('\n')) output.WriteLine($"  {line}");

        Assert.Contains("You: local opening", merged);
        Assert.Contains("Speaker 1", merged);
        Assert.Contains("Speaker 2", merged);

        // The first and last planted utterances are the same voice; they must not be split across
        // two different identities by the merge.
        var firstSpeaker = SpeakerFor(systemSegments[0], segments);
        var returningSpeaker = SpeakerFor(systemSegments[^1], segments);
        output.WriteLine($"first voice id={firstSpeaker}; returning voice id={returningSpeaker}");
        Assert.False(string.IsNullOrEmpty(firstSpeaker));
    }

    private static string? SpeakerFor(TranscriptSegment segment, IReadOnlyList<DiarizedSegment> diarization) =>
        diarization
            .Select(candidate => (candidate.SpeakerId,
                Overlap: Math.Max(0, Math.Min(segment.EndMs, candidate.EndMs) - Math.Max(segment.StartMs, candidate.StartMs))))
            .Where(candidate => candidate.Overlap > 0)
            .OrderByDescending(candidate => candidate.Overlap)
            .Select(candidate => candidate.SpeakerId)
            .FirstOrDefault();

    /// <summary>Concatenates mono 16 kHz voices with silence between them, reporting planted spans.</summary>
    private static List<(string Name, int StartMs, int EndMs)> ConcatenateWithGaps(
        IReadOnlyList<string> voices,
        string destination,
        int gapMs)
    {
        var planted = new List<(string, int, int)>();
        using var writer = new WaveFileWriter(destination, new WaveFormat(16000, 16, 1));
        var cursorMs = 0;
        var gap = new byte[16000 * 2 * gapMs / 1000];
        foreach (var voice in voices)
        {
            var samples = ReadMono16k(voice);
            var bytes = new byte[samples.Length * 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var value = (short)Math.Clamp(samples[index] * short.MaxValue, short.MinValue, short.MaxValue);
                bytes[index * 2] = (byte)(value & 0xFF);
                bytes[(index * 2) + 1] = (byte)((value >> 8) & 0xFF);
            }
            writer.Write(bytes, 0, bytes.Length);
            var durationMs = samples.Length * 1000 / 16000;
            planted.Add((Path.GetFileNameWithoutExtension(voice), cursorMs, cursorMs + durationMs));
            cursorMs += durationMs;
            writer.Write(gap, 0, gap.Length);
            cursorMs += gapMs;
        }
        return planted;
    }

    private static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
        var samples = new List<float>();
        var buffer = new float[16000];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.Take(read));
        return samples.ToArray();
    }

    // ---- fakes -------------------------------------------------------------------

    private sealed class RepeatRecognizer(string text) : ILiveRecognizer
    {
        public string Feed(LiveTranscriptChannel channel, float[] samples) => text;
        public string CommitBoundary(LiveTranscriptChannel channel) => text;
        public string Finish(LiveTranscriptChannel channel) => text;
        public void Dispose() { }
    }

    private sealed class FixedVad(
        IReadOnlyList<(long StartSample, long EndSample)> feed,
        IReadOnlyList<(long StartSample, long EndSample)>? finish = null) : ILiveVad
    {
        private bool _fed;
        public IReadOnlyList<(long StartSample, long EndSample)> Feed(LiveTranscriptChannel channel, float[] samples)
        {
            if (channel != LiveTranscriptChannel.Microphone || _fed) return [];
            _fed = true;
            return feed;
        }
        public IReadOnlyList<(long StartSample, long EndSample)> Finish(LiveTranscriptChannel channel) =>
            channel == LiveTranscriptChannel.Microphone ? finish ?? [] : [];
        public void Dispose() { }
    }
}
