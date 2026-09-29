using System.Net;
using System.Net.Http;

namespace Muesli.Windows.Tests;

public sealed class TextAndSummaryTests
{
    [Fact]
    public void DictionaryCorrectionAndSpeakerAliasesRespectBoundaries()
    {
        var corrected = DictionaryCorrectionService.Apply(
            "muesly and Mueslium",
            [new DictionaryEntryRecord { Phrase = "muesli", Replacement = "Muesli", MatchingThreshold = 0.80 }]);
        Assert.Equal("Muesli and Mueslium", corrected);

        var aliased = SpeakerAliasService.Apply(
            "Speaker 1: hello\nSpeaker 10: intact",
            new Dictionary<string, string> { ["Speaker 1"] = "Alice" });
        Assert.Contains("Alice: hello", aliased);
        Assert.Contains("Speaker 10: intact", aliased);
    }

    [Fact]
    public void TranscriptFormattingAndTimestampSegmentationRemainStable()
    {
        var start = new DateTime(2026, 1, 1, 9, 0, 0);
        var formatted = TranscriptFormatter.Merge(
            [new TranscriptSegment("m", "You", 0, 500, "hello")],
            [new TranscriptSegment("s", "System", 600, 1200, "world")],
            [new DiarizedSegment("remote", 500, 1300)],
            start);
        Assert.Contains("[09:00:00] You: hello", formatted);
        Assert.Contains("Speaker 1: world", formatted);

        var segmented = ParakeetTimestampSegmenter.Build(
            "Meeting system",
            "Hello. Next",
            ["Hello.", " Next"],
            [0f, 1.2f],
            [0.3f, 0.4f],
            2000);
        Assert.Equal(2, segmented.Segments.Count);
        Assert.Equal("System audio", segmented.Segments[0].Speaker);
        Assert.True(segmented.Segments[0].EndMs <= segmented.Segments[1].StartMs);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", true, "http-500")]
    [InlineData(HttpStatusCode.OK, "not-json", true, "malformed-response")]
    [InlineData(HttpStatusCode.OK, "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"## Summary\\nCloud notes\"}]}]}", false, null)]
    public async Task CloudSummaryReportsFallbackAndSuccess(
        HttpStatusCode status,
        string body,
        bool fallback,
        string? reason)
    {
        using var client = new HttpClient(new StubHandler(status, body));
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            "A useful meeting transcript with an action item.",
            "Test",
            new MuesliSettings { MeetingSummaryProvider = "openai", ResolvedOpenAIApiKey = "test-key" },
            httpClient: client);
        Assert.Equal(fallback, result.UsedLocalFallback);
        Assert.Equal(reason, result.SafeFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Fact]
    public async Task MissingKeyUsesDisclosedLocalFallback()
    {
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            "Transcript content.",
            "Test",
            new MuesliSettings { MeetingSummaryProvider = "openrouter" });
        Assert.True(result.UsedLocalFallback);
        Assert.Equal("missing-key", result.SafeFailureReason);
    }

    [Fact]
    public async Task ProviderTimeoutUsesDisclosedFallbackButCallerCancellationPropagates()
    {
        using var timeoutClient = new HttpClient(new BlockingHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        var settings = new MuesliSettings
        {
            MeetingSummaryProvider = "openai",
            ResolvedOpenAIApiKey = "test-key"
        };
        var timedOut = await MeetingSummaryService.CreateSummaryResultAsync(
            "Transcript content with an action item.",
            "Test",
            settings,
            httpClient: timeoutClient);
        Assert.True(timedOut.UsedLocalFallback);
        Assert.Equal("timeout", timedOut.SafeFailureReason);

        using var callerClient = new HttpClient(new BlockingHandler());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MeetingSummaryService.CreateSummaryResultAsync(
            "Transcript content.",
            "Test",
            settings,
            cancellation.Token,
            callerClient));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new System.Diagnostics.UnreachableException();
        }
    }
}
