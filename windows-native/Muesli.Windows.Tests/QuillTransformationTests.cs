using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class QuillTransformationTests
{
    [Fact]
    public async Task RewritesSelectionAndRetriesOnceWhenTheReplyIsCommentary()
    {
        var prompts = new List<string>();
        var replies = new Queue<string>(["Here are a few options:\n1. Hi\n2. Hello", "Hello there."]);
        var result = await QuillTransformation.TransformAsync("hey", "make it formal", (system, user, _) =>
        {
            Assert.Equal(QuillTransformation.SystemPrompt, system);
            prompts.Add(user);
            return Task.FromResult(replies.Dequeue());
        });

        Assert.Equal("Hello there.", result);
        Assert.Contains("\"mode\":\"rewrite_selection\"", prompts[0]);
        Assert.Contains("\"highlighted_text\":\"hey\"", prompts[0]);
        Assert.StartsWith("Your previous response was invalid", prompts[1]);
    }

    [Fact]
    public async Task WithoutSelectionItWritesAtTheCursorAndNeverPastesBadReplies()
    {
        string? prompt = null;
        await QuillTransformation.TransformAsync("", "a grocery list", (_, user, _) => Task.FromResult((prompt = user) is null ? "" : "- milk"));
        Assert.Contains("\"mode\":\"generate_at_cursor\"", prompt);
        Assert.DoesNotContain("highlighted_text", prompt);

        await Assert.ThrowsAsync<QuillException>(() => QuillTransformation.TransformAsync("x", "fix", (_, _, _) => Task.FromResult("  ")));
        await Assert.ThrowsAsync<QuillException>(() => QuillTransformation.TransformAsync("x", "fix",
            (_, _, _) => Task.FromResult("**Option 1** a\n**Option 2** b")));
        await Assert.ThrowsAsync<QuillException>(() => QuillTransformation.TransformAsync("x", " ", (_, _, _) => Task.FromResult("y")));
        await Assert.ThrowsAsync<QuillException>(() => QuillTransformation.TransformAsync(
            new string('a', QuillTransformation.MaximumInputCharacters + 1), "shorten", (_, _, _) => Task.FromResult("a")));
        Assert.Equal("a\nb", QuillTransformation.Validated("\r\n a\r\nb \r\n"));
    }
}
