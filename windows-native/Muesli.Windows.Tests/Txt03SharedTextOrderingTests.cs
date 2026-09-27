using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// TXT-03 cross-workflow golden tests: dictation, meeting, and import all run the deterministic
/// filler pass before dictionary correction, and the meeting/import passes never rewrite a
/// timestamp or speaker prefix.
/// </summary>
public sealed class Txt03SharedTextOrderingTests
{
    private static TranscriptionPipelineService Pipeline() => new(new NativeTextCleanupService());

    private static DictionaryEntryRecord[] MuesliDictionary() =>
    [
        new DictionaryEntryRecord { Phrase = "muesli", Replacement = "Müsli", MatchingThreshold = 0.85 }
    ];

    [Fact]
    public async Task MeetingTranscriptAppliesFillerThenDictionaryBehindSpeakerPrefixes()
    {
        var transcript = await Pipeline().PrepareMeetingTranscriptAsync(
            "[09:00:00] Speaker 1: um we shipped muesli today\n[09:00:05] You: uh muesli again",
            enableCleanup: false,
            MuesliDictionary(),
            removeFillerWords: true);

        Assert.Contains("[09:00:00] Speaker 1: We shipped Müsli today", transcript, StringComparison.Ordinal);
        Assert.Contains("[09:00:05] You: Müsli again", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("um ", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uh ", transcript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportedTranscriptAppliesFillerThenDictionaryBehindSpeakerPrefixes()
    {
        var transcript = await Pipeline().PrepareImportedTranscriptAsync(
            "[09:00:00] Speaker 1: uh hello world",
            enableCleanup: false,
            [],
            CancellationToken.None,
            removeFillerWords: true);

        Assert.Equal("[09:00:00] Speaker 1: Hello world", transcript);
    }

    [Fact]
    public async Task MeetingFillerRemovalIsOptOutWhenTheSettingIsDisabled()
    {
        var transcript = await Pipeline().PrepareMeetingTranscriptAsync(
            "[09:00:00] Speaker 1: um hello",
            enableCleanup: false,
            [],
            removeFillerWords: false);

        Assert.Equal("[09:00:00] Speaker 1: um hello", transcript);
    }

    [Fact]
    public async Task ImportFillerRemovalDefaultsOffForLegacyCallers()
    {
        var transcript = await Pipeline().PrepareImportedTranscriptAsync(
            "[09:00:00] Speaker 1: uh hello",
            enableCleanup: false,
            []);

        Assert.Equal("[09:00:00] Speaker 1: uh hello", transcript);
    }

    [Fact]
    public async Task DictationAppliesFillerBeforeDictionary()
    {
        var transcript = await Pipeline().PrepareDictationTextAsync(
            "um we shipped muesli",
            enableCleanup: false,
            removeFillerWords: true,
            MuesliDictionary());

        Assert.Contains("Müsli", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("um ", transcript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FillerPassNeverRewritesASpeakerPrefixEvenWhenItLooksLikeFiller()
    {
        var filtered = TranscriptionPipelineService.ApplyFillerPreservingSpeakerPrefixes(
            "[09:00:00] Hmm: uh hello\n[09:00:05] Um: um hi");

        Assert.Contains("[09:00:00] Hmm: Hello", filtered, StringComparison.Ordinal);
        Assert.Contains("[09:00:05] Um: Hi", filtered, StringComparison.Ordinal);
    }
}
