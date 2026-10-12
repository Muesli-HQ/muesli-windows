namespace Muesli.Windows.Services;

/// <summary>
/// One beat of the "What's New" walkthrough (macOS FeatureTourStep). <see cref="Route"/> is the
/// shell destination to open first and <see cref="TargetId"/> the AutomationId to spotlight there;
/// a step without a target shows a centred callout.
/// </summary>
public sealed record FeatureTourStep(
    string Id,
    string Eyebrow,
    string Title,
    string Message,
    string Glyph,
    string? Route,
    string? TargetId);

public sealed record FeatureTour(string Version, IReadOnlyList<FeatureTourStep> Steps)
{
    /// <summary>"0.3.0" reads as "0.3", like macOS.</summary>
    public string DisplayVersion
    {
        get
        {
            if (MarketingVersion.Parse(Version) is not { } parts) return Version;
            var trimmed = parts.ToList();
            while (trimmed.Count > 2 && trimmed[^1] == 0) trimmed.RemoveAt(trimmed.Count - 1);
            return string.Join('.', trimmed);
        }
    }
}

/// <summary>
/// macOS FeatureTourCatalog, with the beats that are true on Windows. Bump <see cref="FeatureTour.Version"/>
/// to the release that ships a new set of beats so existing installs are offered it once.
/// </summary>
public static class FeatureTourCatalog
{
    public static FeatureTour Latest { get; } = new("0.3.0",
    [
        new("voice-settings", "COMPUTER USE COMMAND", "Change settings with your voice",
            "Turn on Computer Use Command in Shortcuts, press the shortcut and ask Muesli to change a setting or do something on your PC, then press it again to run. It plans with your connected ChatGPT account.",
            "", "shortcuts", "ChangeComputerUseShortcutButton"),
        new("dictionary-suggestions", "DICTIONARY", "Fix a word once",
            "When you correct a dictated word, Muesli can offer to add it to your Dictionary so it is spelled right next time. Turn suggestions on or off here.",
            "", "dictionary", "DictionarySuggestionsToggle"),
        new("retranscribe", "SAVED RECORDINGS", "Re-transcribe an existing meeting",
            "Open a meeting with saved audio, then choose Re-transcribe in More actions. Pick a downloaded meeting model to give the recording another pass without recording it again.",
            "", "retranscribe", "MeetingMoreActionsButton"),
        new("hands-free", "PRESS TO TOGGLE", "No need to hold all the time",
            "Turn on Hands-Free Mode in Shortcuts. Double-tap your dictation shortcut to start, then tap it again to stop.",
            "", "shortcuts", "HandsFreeToggle"),
        new("chatgpt-summaries", "MEETING NOTES", "Meeting notes with ChatGPT",
            "Choose ChatGPT in the highlighted Summary provider menu to write meeting notes with your ChatGPT subscription, no API key needed. Meeting transcripts are sent to ChatGPT for summaries.",
            "", "settings:meetings", "SummaryProviderBox"),
        new("indicator-style", "A NEW LOOK", "Pick your recording indicator",
            "Choose Classic or Minimal under Settings → Appearance → Indicator style. Minimal stays a quiet bar until you hover it.",
            "", "settings:appearance", "IndicatorStylePicker")
    ]);
}

/// <summary>Numeric dotted version ("0.8.5", "0.8.5-beta"); missing parts count as zero.</summary>
public static class MarketingVersion
{
    public static IReadOnlyList<int>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split('-', 2)[0].Split('.');
        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || !part.All(char.IsAsciiDigit) || !int.TryParse(part, out var number)) return null;
            parsed.Add(number);
        }
        return parsed;
    }

    public static int Compare(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var difference = (index < left.Count ? left[index] : 0).CompareTo(index < right.Count ? right[index] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }
}

/// <summary>macOS FeatureTourPresentationPolicy and FeatureTourStore.automaticTour.</summary>
public static class FeatureTourPresentationPolicy
{
    public static bool ShouldPresentAutomatically(
        string currentVersion,
        string? previousVersion,
        string? lastPresentedTourVersion,
        bool hasCompletedOnboarding,
        FeatureTour tour)
    {
        if (!hasCompletedOnboarding ||
            MarketingVersion.Parse(currentVersion) is not { } current ||
            MarketingVersion.Parse(tour.Version) is not { } target ||
            MarketingVersion.Compare(current, target) < 0)
        {
            return false;
        }

        var lastPresented = MarketingVersion.Parse(lastPresentedTourVersion);
        if (lastPresented is not null && MarketingVersion.Compare(lastPresented, target) >= 0) return false;

        // A completed setup with no launch marker is an existing install, not a fresh one.
        if (MarketingVersion.Parse(previousVersion) is not { } previous) return true;
        var order = MarketingVersion.Compare(previous, target);
        // Prerelease builds can share a version; a prior tour marker tells that upgrade from a fresh install.
        return order < 0 || order == 0 && lastPresented is not null;
    }

    /// <summary>
    /// Decides whether this launch offers the tour and records the launch. A fresh install (setup
    /// not finished) only records the version, so a new user is never toured on top of setup.
    /// </summary>
    public static (bool Present, MuesliSettings Settings) AutomaticTour(
        MuesliSettings settings,
        string currentVersion,
        FeatureTour tour)
    {
        var present = settings.OnboardingCompleted && ShouldPresentAutomatically(
            currentVersion,
            settings.FeatureTourLastLaunchedVersion,
            settings.FeatureTourLastPresentedVersion,
            hasCompletedOnboarding: true,
            tour);
        return (present, settings with { FeatureTourLastLaunchedVersion = currentVersion });
    }
}
