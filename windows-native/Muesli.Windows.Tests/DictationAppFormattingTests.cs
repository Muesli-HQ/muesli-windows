using System.Text.Json;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-15 (macOS 28adabba, 64fdb1c0, DictationAppFormattingTests.swift): per-app output formatting.</summary>
public sealed class DictationAppFormattingTests
{
    private static readonly IReadOnlyList<DictationAppFormattingRule> Rules =
        [new("WhatsApp", "WhatsApp", DictationAppFormatting.LowercaseChat), new("slack", "Slack", DictationAppFormatting.NoFinalPeriod)];

    [Theory]
    [InlineData("See you soon.", "see you soon")]
    [InlineData("Are you coming?", "are you coming?")]
    [InlineData("Wait for it...", "wait for it...")]
    [InlineData("Done.  ", "done  ")]
    [InlineData("Version 1.2 ships today", "version 1.2 ships today")]
    public void LowercaseChatKeepsOtherPunctuation(string input, string expected) =>
        Assert.Equal(expected, DictationAppFormatting.LowercaseChat.Apply(input));

    [Fact]
    public void TheReceivingAppsRuleAppliesAndOtherAppsAreUntouched()
    {
        Assert.Equal("Hey Sam", DictationAppFormattingRule.Apply("Hey Sam.", "SLACK", Rules));
        Assert.Equal("Hey Sam.", DictationAppFormattingRule.Apply("Hey Sam.", "notepad", Rules));
        Assert.Equal("Hey Sam.", DictationAppFormattingRule.Apply("Hey Sam.", null, Rules));
    }

    [Fact]
    public void OnlyAnAppWithAnActiveRuleBuffersLiveDictation()
    {
        Assert.True(DictationAppFormattingRule.ShouldBufferStreaming("WhatsApp", Rules));
        Assert.False(DictationAppFormattingRule.ShouldBufferStreaming("notepad", Rules));
        Assert.False(DictationAppFormattingRule.ShouldBufferStreaming("pending", [new("pending", "Pending")]));
        Assert.Empty(DictationAppFormattingRule.ActiveRules([new("pending", "Pending"), new("", "Blank", DictationAppFormatting.LowercaseChat)]));
    }

    [Fact]
    public void PresetsAndTemplateSelectionFollowTheFormatting()
    {
        Assert.Equal("lowercase_chat", DictationAppFormatting.LowercaseChat.Preset);
        Assert.Equal("custom", new DictationAppFormatting(DictationAppFormatting.Lowercase).Preset);
        DictationFormattingTemplate mine = new("saved_1", "Mine", DictationAppFormatting.LowercaseChat);
        Assert.Equal("Mine", DictationFormattingTemplate.Selected(Rules[0] with { TemplateId = "saved_1" }, [mine])!.Name);
        // A custom edit detaches the saved template; the built-in that matches is shown instead.
        Assert.Equal("Lowercase chat", DictationFormattingTemplate.Selected(Rules[0], [mine])!.Name);
    }

    [Fact]
    public void EditsReturningToOriginalRemoveTheRule()
    {
        var updated = DictationFormattingEdits.Update(Rules, "slack", formatting => formatting with { FinalPeriod = DictationAppFormatting.Keep });
        Assert.DoesNotContain(updated, rule => rule.AppId == "slack");
        var custom = DictationFormattingEdits.Update(Rules, "WhatsApp", formatting => formatting with { FinalPeriod = DictationAppFormatting.Keep });
        Assert.Null(custom.Single(rule => rule.AppId == "WhatsApp").TemplateId);
        Assert.Single(DictationFormattingEdits.ApplyTemplate(Rules, "WhatsApp", DictationFormattingTemplate.BuiltIns[0]));
    }

    [Fact]
    public void SavingATemplateRejectsDuplicatesAndStaleFormatting()
    {
        Assert.Equal("Give this template a name.", DictationFormattingEdits.SaveTemplate("  ", Rules[0], Rules, []).Error);
        Assert.Equal("A template with that name already exists.", DictationFormattingEdits.SaveTemplate("casual", Rules[0], Rules, []).Error);
        var stale = Rules[0] with { Formatting = DictationAppFormatting.NoFinalPeriod };
        Assert.Equal("This app's formatting changed. Close this window and try again.", DictationFormattingEdits.SaveTemplate("Chat", stale, Rules, []).Error);
        var (template, error) = DictationFormattingEdits.SaveTemplate(" Chat ", Rules[0], Rules, []);
        Assert.Null(error);
        Assert.Equal("Chat", template!.Name);
        Assert.StartsWith("saved_", template.Id);
    }

    [Fact]
    public void SuggestionsRankByUseAndOnlyChatAppsSuggestLowercase()
    {
        var suggestions = DictationFormattingSuggestion.Ranked(
            [("chrome", "Chrome"), ("WhatsApp", "WhatsApp"), ("chrome", "Chrome"), ("slack", "Slack"), ("", "Unknown")],
            [Rules[1]]);
        Assert.Equal(["chrome", "WhatsApp"], suggestions.Select(item => item.AppId));
        Assert.Equal(2, suggestions[0].Sessions);
        Assert.False(suggestions[0].SuggestsLowercaseChat);
        Assert.True(suggestions[1].SuggestsLowercaseChat);
    }

    [Fact]
    public void AMalformedSavedEntryIsDroppedWithoutLosingTheOthers()
    {
        const string json = """
            { "DictationAppFormattingRules": [
                { "AppId": "WhatsApp", "AppName": "WhatsApp", "Formatting": { "LetterCase": "lowercase", "FinalPeriod": "remove" } },
                { "AppId": "future", "AppName": "Future", "Formatting": { "LetterCase": "titlecase" } },
                { "AppId": "slack", "AppName": "Slack" } ] }
            """;
        var settings = SettingsStore.NormalizeAfterDeserialization(JsonSerializer.Deserialize<MuesliSettings>(json)!);
        Assert.Equal(["WhatsApp", "slack"], settings.DictationAppFormattingRules.Select(rule => rule.AppId));
        Assert.Equal(DictationAppFormatting.LowercaseChat, settings.DictationAppFormattingRules[0].Formatting);
        Assert.True(settings.DictationAppFormattingRules[1].Formatting.IsOriginal);
    }
}
