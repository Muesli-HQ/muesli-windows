using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class CleanupPromptsTests
{
    [Fact]
    public async Task PresetsFollowMacRulesAndReachHostedCleanup()
    {
        var settings = new MuesliSettings();
        Assert.Equal("Enter a preset name.", CleanupPrompts.NameError(" ", null, settings.CustomCleanupPrompts));
        Assert.Equal("Use a unique preset name.", CleanupPrompts.NameError("default  CLEANUP", null, settings.CustomCleanupPrompts));
        Assert.Equal("Default Cleanup Copy", CleanupPrompts.SuggestUniqueName("Default Cleanup Copy", settings.CustomCleanupPrompts));

        // Creating makes the preset active; a duplicate name gets a numeric suffix.
        settings = CleanupPrompts.Create(settings, " Slack ", " Be casual. ");
        var slack = Assert.Single(settings.CustomCleanupPrompts);
        Assert.Equal(("Slack", "Be casual.", slack.Id), (slack.Name, slack.Prompt, settings.ActiveCleanupPromptId));
        Assert.Equal("Slack 2", CleanupPrompts.SuggestUniqueName("Slack", settings.CustomCleanupPrompts));
        Assert.Null(CleanupPrompts.NameError("Slack", slack.Id, settings.CustomCleanupPrompts));

        string? sent = null;
        await HostedTranscriptCleanup.CleanAsync("hi there", null, (system, _, _) => { sent = system; return Task.FromResult("Hi there."); },
            systemPrompt: CleanupPrompts.Resolve(settings.ActiveCleanupPromptId, settings.CustomCleanupPrompts).Prompt);
        Assert.Equal("Be casual.", sent);

        settings = CleanupPrompts.Update(settings, slack.Id, "Slack", "Be brief.");
        Assert.Equal("Be brief.", settings.CustomCleanupPrompts[0].Prompt);

        // Deleting the active preset falls back to the default, and a stale id resolves to it.
        settings = CleanupPrompts.Delete(settings, slack.Id);
        Assert.Empty(settings.CustomCleanupPrompts);
        Assert.Equal(CleanupPrompts.DefaultId, settings.ActiveCleanupPromptId);
        Assert.Equal(HostedTranscriptCleanup.DefaultSystemPrompt, CleanupPrompts.Resolve("gone", []).Prompt);
    }
}
