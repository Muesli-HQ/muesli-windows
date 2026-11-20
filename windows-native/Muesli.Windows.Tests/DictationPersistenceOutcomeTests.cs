using Muesli.Windows.Core.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-17 (macOS 9edaca3d): History is mentioned only once the dictation is actually saved.</summary>
public sealed class DictationPersistenceOutcomeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASavedDictationKeepsTheNormalStatus(bool pasted) =>
        Assert.Null(DictationPersistenceOutcome.Status(persisted: true, pasted));

    [Fact]
    public void AnUnsavedDictationNeverPointsToHistory()
    {
        Assert.Equal("Dictation was pasted, but could not be saved to History.",
            DictationPersistenceOutcome.Status(persisted: false, pasted: true));
        var unpasted = DictationPersistenceOutcome.Status(persisted: false, pasted: false)!;
        Assert.Contains("could not be saved to History", unpasted, StringComparison.Ordinal);
        Assert.DoesNotContain("Find it in History", unpasted, StringComparison.Ordinal);
    }
}
