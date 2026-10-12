using System.Globalization;

namespace Muesli.Windows.Core.Insights;

/// <summary>
/// macOS <c>ContributionSocialShare</c>: sharing posts the last completed 1,000-word dictation
/// milestone, and only once one has been reached.
/// </summary>
public static class ContributionShare
{
    public const int DictationWordInterval = 1_000;
    public const string MuesliUrl = "https://muesli.works";
    public const string GitHubUrl = "https://github.com/Muesli-HQ/muesli";
    public const string DonateUrl = "https://buymeacoffee.com/phequals7";

    public static int? CompletedWordMilestone(int totalWords) =>
        totalWords < DictationWordInterval ? null : totalWords / DictationWordInterval * DictationWordInterval;

    /// <summary>Counts read "31,000" in the English copy whatever the system locale.</summary>
    public static string Message(int wordCount) =>
        $"I've dictated {wordCount.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} words with Muesli. " +
        $"It's fast, open source, on-device, and free to use. Try it: {MuesliUrl}";

    public static Uri TweetUrl(int wordCount) =>
        new($"https://x.com/intent/tweet?text={Uri.EscapeDataString(Message(wordCount))}");

    /// <summary>LinkedIn ignores prefilled text, so macOS also copies the message to the clipboard.</summary>
    public static Uri LinkedInUrl(int wordCount) =>
        new("https://www.linkedin.com/feed/?shareActive=true" +
            $"&text={Uri.EscapeDataString(Message(wordCount))}" +
            $"&url={Uri.EscapeDataString(MuesliUrl)}&shareUrl={Uri.EscapeDataString(MuesliUrl)}&linkOrigin=LI_BADGE");
}
