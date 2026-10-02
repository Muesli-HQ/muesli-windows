using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingUiParityTests
{
    [Fact]
    public void RichNotesAndLiveCopyMatchTheMacEditingContract()
    {
        foreach (var markdown in new[] { "# Heading", "- [ ] Task", "- [x] Done", "- **Bold** and _italic_",
            "Plain `code` and [link](https://example.com)", "  indented  ", "", "# ", @"Literal \*marker\*" })
        {
            var rendered = MeetingNotesRichText.ParseLine(markdown);
            Assert.Equal(markdown, MeetingNotesRichText.SerializeLine(rendered));
        }
        Assert.Equal("Heading", string.Concat(MeetingNotesRichText.ParseLine("# Heading").Runs.Select(run => run.Text)));
        Assert.Equal("☐ Task", string.Concat(MeetingNotesRichText.ParseLine("- [ ] Task").Runs.Select(run => run.Text)));
        Assert.Equal("You: saved\nOthers: pending\nYou: mine", LiveTranscriptCopyContent.Text(" You: saved ", " mine ", " pending "));
        Assert.Equal("", LiveTranscriptCopyContent.Text(" ", "\n", ""));
        var messages = TranscriptChatMessage.Parse("[00:04] You: First\r\nOthers: Second\n\nhttps://example.com\nSpeaker 2: Third");
        Assert.Equal(4, messages.Length);
        Assert.True(messages[0].IsYou);
        Assert.Equal("00:04", messages[0].Timestamp);
        Assert.Equal("https://example.com", messages[2].Text);
        Assert.Null(messages[2].Speaker);
        Assert.Equal(2, TranscriptChatMessage.Parse("You: First\rOthers: Second").Length);
    }
}
