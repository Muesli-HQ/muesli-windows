using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class HostedTranscriptCleanupTests
{
    [Fact]
    public async Task CleansWithTheMacPromptAndAppContextButNeverPastesABadReply()
    {
        string? sent = null;
        var cleaned = await HostedTranscriptCleanup.CleanAsync("um send it to jon smyth", "App: slack (Jon Smith - DM)",
            (system, user, _) =>
            {
                Assert.Equal(HostedTranscriptCleanup.DefaultSystemPrompt, system);
                sent = user;
                return Task.FromResult("```\nSend it to Jon Smith.\n```");
            });
        Assert.Equal("Send it to Jon Smith.", cleaned);
        Assert.Equal("<APP-CONTEXT>\nApp: slack (Jon Smith - DM)\n</APP-CONTEXT>\n\n<USER-INPUT>\num send it to jon smyth\n</USER-INPUT>", sent);

        // Failures and non-transcripts keep the original words.
        Assert.Equal("hello there", await HostedTranscriptCleanup.CleanAsync("hello there", null,
            (_, _, _) => throw new SummaryProviderException("http-500")));
        Assert.Equal("hello there", await HostedTranscriptCleanup.CleanAsync("hello there", null,
            (_, _, _) => Task.FromResult("The user is asking to greet someone.")));
        Assert.Equal("hi", await HostedTranscriptCleanup.CleanAsync("hi", null,
            (_, _, _) => Task.FromResult(new string('x', 200))));
        Assert.Equal("hello", await HostedTranscriptCleanup.CleanAsync("hello", null, (_, _, _) => Task.FromResult("...")));

        // An empty reply is right only when the speaker deleted everything.
        Assert.Equal("", await HostedTranscriptCleanup.CleanAsync("buy milk scratch that", null, (_, _, _) => Task.FromResult("")));
        Assert.Equal("buy milk", await HostedTranscriptCleanup.CleanAsync("buy milk", null, (_, _, _) => Task.FromResult("")));
        Assert.Equal("1. Milk\n2. Eggs", HostedTranscriptCleanup.CleanOutput("Output: 1. Milk \n2. Eggs"));
    }
}
