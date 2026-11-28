using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>Runs against the real Windows Spell Checking API on this machine.</summary>
public sealed class WindowsSpellCheckerTests
{
    [Fact]
    public void Knows_english_words_and_not_misheard_names()
    {
        var recognized = new WindowsSpellChecker().RecognizedWords(["Usually", "usual", "musley", "thoraipakkam"]);

        // Every Windows 10 and 11 install ships the English dictionary; an empty result means the
        // API could not be reached, which the detector treats as "every word unknown".
        Assert.Contains("usually", recognized);
        Assert.Contains("usual", recognized);
        Assert.DoesNotContain("musley", recognized);
        Assert.DoesNotContain("thoraipakkam", recognized);
    }
}
