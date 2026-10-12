using Muesli.Windows.Core.Insights;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class InsightsUsageTests
{
    [Fact]
    public void UsageRanksBySessionsWithinTheRangeAndCollectsMissingKeys()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0);
        var rows = new (DateTime, string, string?, string?)[]
        {
            (now.AddDays(-1), "one two", "a", "Model A"),
            (now.AddDays(-2), "three", "b", "Model B"),
            (now.AddDays(-3), "four five six", "b", null),
            (now.AddDays(-4), "seven", null, null),
            (now.AddDays(-200), "outside the range", "a", "Model A")
        };

        var usage = InsightsWordAnalyzer.Usage(rows, InsightsRange.ThirtyDays, now);

        Assert.Equal(["b", "a", "unknown"], usage.Select(row => row.Id));
        Assert.Equal(new InsightsUsage("b", "Model B", 2, 4), usage[0]);
        Assert.Equal(new InsightsUsage("a", "Model A", 1, 2), usage[1]);
        Assert.Equal(3, InsightsWordAnalyzer.Usage(rows, InsightsRange.AllTime, now).Count);
    }

    [Theory]
    [InlineData("parakeet-tdt-0.6b-v3-int8", "parakeet")]
    [InlineData("base", "Whisper Base")]
    [InlineData("medium.en", "Whisper Medium English")]
    [InlineData("large-v3-turbo", "Whisper Large-v3 Turbo")]
    [InlineData("something-custom", "something-custom")]
    [InlineData("", "")]
    public void ModelDisplayNamesCoverIdsOlderBuildsStored(string id, string expected)
    {
        var name = TranscriptionModelCatalog.DisplayNameFor(id);
        Assert.Contains(expected, name, StringComparison.OrdinalIgnoreCase);
        if (expected == "parakeet") Assert.NotEqual(id, name);
    }
}
