using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class L23TranscriptIntegrationTests
{
    [Fact]
    public void LibraryTranscriptMeetingStoreUsesTheActiveJsonAdapter()
    {
        using var directory = new TestDirectory();
        var json = new AppDataStore(directory.Path);
        var adapter = new JsonLibraryHistoryAdapter(json);
        var bridge = new LibraryTranscriptMeetingStore(adapter);
        var meeting = new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = "meeting-bridge",
            Title = "Bridge test",
            CreatedAt = new DateTime(2026, 8, 20, 9, 0, 0),
            Transcript = "Original transcript",
            ManualNotes = "Keep this note",
            TitleIsManual = true,
            SpeakerAliases = new Dictionary<string, string> { ["Speaker 1"] = "Priya" }
        };

        bridge.Save(meeting);

        var loaded = bridge.Find(meeting.Id);
        Assert.NotNull(loaded);
        Assert.Equal(meeting.Transcript, loaded!.Transcript);
        Assert.Equal(meeting.ManualNotes, loaded.ManualNotes);
        Assert.True(loaded.TitleIsManual);
        Assert.Equal("Priya", loaded.SpeakerAliases["Speaker 1"]);
    }

    [Fact]
    public async Task ProductionOwnershipGuardRejectsImportedAudio()
    {
        using var directory = new TestDirectory();
        var importedAudio = directory.File("imported.wav");
        File.WriteAllBytes(importedAudio, [1, 2, 3, 4]);
        var store = new MemoryStore(new PersistedMeeting
        {
            SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
            Id = "meeting-owned",
            Title = "Imported meeting",
            CreatedAt = DateTime.UtcNow,
            Transcript = "Original transcript",
            SourcePath = importedAudio,
            MicrophoneAudioPath = importedAudio
        });
        var asr = new CountingAsr();
        using var service = new TranscriptEditService(
            store,
            new RetranscriptionCandidateStore(directory.File("retranscription")),
            asr,
            captureStorage: new CaptureStorageService(directory.File("captures")));

        var result = await service.RetranscribeAsync("meeting-owned");

        Assert.Equal(RetranscriptionOutcome.MissingAudio, result.Outcome);
        Assert.Equal(0, asr.CallCount);
        Assert.Equal("Original transcript", store.Find("meeting-owned")!.Transcript);
    }

    private sealed class CountingAsr : IMeetingRetranscriptionAsr
    {
        public int CallCount { get; private set; }

        public Task<TranscriptionResult> TranscribeOwnedAudioAsync(
            string filePath,
            IProgress<TranscriptionProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TranscriptionResult("unexpected"));
        }
    }

    private sealed class MemoryStore(PersistedMeeting meeting) : ITranscriptMeetingStore
    {
        private PersistedMeeting _meeting = meeting;

        public PersistedMeeting? Find(string meetingId) =>
            string.Equals(_meeting.Id, meetingId, StringComparison.Ordinal)
                ? _meeting
                : null;

        public void Save(PersistedMeeting value) => _meeting = value;
    }
}
