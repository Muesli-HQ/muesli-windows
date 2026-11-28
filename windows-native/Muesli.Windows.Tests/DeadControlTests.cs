namespace Muesli.Windows.Tests;

/// <summary>
/// Controls that had no working feature behind them are kept off the pages until the feature
/// exists, rather than shown doing nothing.
/// </summary>
public sealed class DeadControlTests
{
    [Fact]
    public void PagesDoNotOfferControlsWithoutAFeatureBehindThem()
    {
        var dictionary = ReadPage("DictionaryPage.xaml");
        var settings = ReadPage("SettingsPage.xaml");
        var shortcuts = ReadPage("ShortcutsPage.xaml");

        Assert.DoesNotContain("DictionarySuggestionsToggle", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("SuggestionsEnabled", dictionary, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenDictionaryPageButton", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Dictionary suggestions\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Quill shortcut", shortcuts, StringComparison.Ordinal);
        Assert.DoesNotContain("QuillUnavailableReason", shortcuts, StringComparison.Ordinal);

        // Stored suggestions stay actionable even without a switch.
        Assert.Contains("DictionarySuggestionList", dictionary, StringComparison.Ordinal);
    }

    private static string ReadPage(string name) =>
        File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Pages", name));
}
