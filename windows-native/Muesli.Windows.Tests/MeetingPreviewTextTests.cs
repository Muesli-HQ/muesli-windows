using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingPreviewTextTests
{
    [Fact]
    public void Snippet_strips_markdown_like_macOS()
    {
        var notes = "## Meeting Summary\r\n- **Owner:** [Ana](https://x.test) ships `v2`\n- [x] Done\n```\ncode\n```\n---\n> quoted _note_";

        Assert.Equal("Meeting Summary Owner: Ana ships v2 Done quoted note", MeetingPreviewText.Snippet(notes));
    }

    [Fact]
    public void Snippet_truncates_to_88_characters_and_reports_empty_notes()
    {
        Assert.Equal(new string('a', 85) + "...", MeetingPreviewText.Snippet(new string('a', 120)));
        Assert.Equal("No notes yet", MeetingPreviewText.Snippet("  \n## \n"));
    }
}
