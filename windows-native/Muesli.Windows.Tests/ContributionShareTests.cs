using Muesli.Windows.Core.Insights;

namespace Muesli.Windows.Tests;

/// <summary>macOS ContributionSocialShare behaviour.</summary>
public sealed class ContributionShareTests
{
    [Fact]
    public void SharesTheLastCompletedThousandWords()
    {
        Assert.Null(ContributionShare.CompletedWordMilestone(999));
        Assert.Equal(1_000, ContributionShare.CompletedWordMilestone(1_000));
        Assert.Equal(31_000, ContributionShare.CompletedWordMilestone(31_999));

        Assert.Equal(
            "I've dictated 31,000 words with Muesli. It's fast, open source, on-device, and free to use. Try it: https://muesli.works",
            ContributionShare.Message(31_000));
        Assert.StartsWith("https://x.com/intent/tweet?text=I%27ve%20dictated%2031%2C000", ContributionShare.TweetUrl(31_000).AbsoluteUri);
        Assert.EndsWith("&url=https%3A%2F%2Fmuesli.works&shareUrl=https%3A%2F%2Fmuesli.works&linkOrigin=LI_BADGE",
            ContributionShare.LinkedInUrl(31_000).AbsoluteUri);
    }
}
