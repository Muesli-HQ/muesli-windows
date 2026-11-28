using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>Runs against the real Windows Spell Checking API on this machine.</summary>
public sealed class WindowsSpellCheckerTests
{
    [EnglishSpellCheckerFact]
    public void Knows_english_words_and_not_misheard_names()
    {
        var recognized = new WindowsSpellChecker().RecognizedWords(["Usually", "usual", "musley", "thoraipakkam"]);

        // Skipped on a PC without an English checker, where the API returns nothing by design and
        // the detector treats every word as unknown.
        Assert.Contains("usually", recognized);
        Assert.Contains("usual", recognized);
        Assert.DoesNotContain("musley", recognized);
        Assert.DoesNotContain("thoraipakkam", recognized);
    }
}
