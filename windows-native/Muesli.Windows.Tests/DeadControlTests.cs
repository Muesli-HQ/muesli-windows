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

        Assert.DoesNotContain("OpenDictionaryPageButton", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Quill shortcut", shortcuts, StringComparison.Ordinal);
        Assert.DoesNotContain("QuillUnavailableReason", shortcuts, StringComparison.Ordinal);
        Assert.Contains("DictionarySuggestionList", dictionary, StringComparison.Ordinal);
    }

    [Fact]
    public void DictionarySuggestionSwitchesSaveTheRealSetting()
    {
        var dictionary = ReadPage("DictionaryPage.xaml");
        var settings = ReadPage("SettingsPage.xaml");
        var dictionaryViewModel = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "ViewModels", "DictionaryPageViewModel.cs"));
        var settingsViewModel = File.ReadAllText(TestRepositoryLayout.Combine(
            "windows-native", "Muesli.Windows.WinUI", "ViewModels", "SettingsPageViewModel.cs"));

        Assert.Contains("DictionarySuggestionsToggle", dictionary, StringComparison.Ordinal);
        Assert.Contains("EnableDictionaryCorrectionPrompts = value", dictionaryViewModel, StringComparison.Ordinal);
        Assert.Contains("ViewModel.EnableDictionaryCorrectionPrompts", settings, StringComparison.Ordinal);
        Assert.Contains("settings with { EnableDictionaryCorrectionPrompts = EnableDictionaryCorrectionPrompts }", settingsViewModel, StringComparison.Ordinal);
    }

    private static string ReadPage(string name) =>
        File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Pages", name));
}
