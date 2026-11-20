using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-25: the v12 cleanup contract, output guard and Lite migration (macOS MuesliCleanupV2Prompt).</summary>
public sealed class MuesliCleanupV2PromptTests
{
    [Fact]
    public void Gguf_requests_match_the_trained_chat_shape()
    {
        var qwen = MuesliCleanupV2Prompt.GgufRequest("um hello", CleanupPromptContract.TrainedV12Qwen);
        Assert.StartsWith("<|im_start|>user\n" + MuesliCleanupV2Prompt.SystemPrompt + "\n\num hello<|im_end|>\n", qwen);
        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", qwen);

        var lfm = MuesliCleanupV2Prompt.GgufRequest("um hello", CleanupPromptContract.TrainedV12Lfm);
        Assert.StartsWith("<|startoftext|><|im_start|>user\n", lfm);
        Assert.EndsWith("<|im_start|>assistant\n", lfm);
        // llama.cpp inserting BOS itself must not double it.
        Assert.StartsWith("<|im_start|>", MuesliCleanupV2Prompt.GgufRequest("x", CleanupPromptContract.TrainedV12Lfm, addsBos: true));

        var context = MuesliCleanupV2Prompt.Request("hi", "Document context:\nSlack\nOCR screen text:");
        Assert.Contains("<APP-CONTEXT>\nSlack\n</APP-CONTEXT>\n\nhi", context);
    }

    [Fact]
    public void Guard_keeps_the_dictation_when_output_is_truncated_empty_or_runaway()
    {
        Assert.Equal(("Hello there.", false), MuesliCleanupV2Prompt.Guard("Hello there .<|im_end|>", "um hello there", true));
        Assert.Equal(("um hello there", true), MuesliCleanupV2Prompt.Guard("Hello", "um hello there", generationCompleted: false));
        Assert.Equal(("um hi", true), MuesliCleanupV2Prompt.Guard("", "um hi", true));
        Assert.Equal(("hi", true), MuesliCleanupV2Prompt.Guard(new string('x', 10), "hi", true));
    }

    [Fact]
    public void Clean_output_strips_leaked_markers_but_keeps_dictated_words()
    {
        Assert.Equal("Ship it.", MuesliCleanupV2Prompt.CleanOutput("model\nShip it."));
        Assert.Equal("model\nShip it.", MuesliCleanupV2Prompt.CleanOutput("model\nShip it.", input: "model ship it"));
        Assert.Equal("Ship it.", MuesliCleanupV2Prompt.CleanOutput("<APP-CONTEXT>\nShip it."));
        var leaked = MuesliCleanupV2Prompt.SystemPrompt.Split("\n\n")[0] + "\nShip it.";
        Assert.Equal("Ship it.", MuesliCleanupV2Prompt.CleanOutput(leaked));
    }

    [Theory]
    [InlineData("muesli-cleanup-qwen35-postproc-v3")]
    [InlineData("qwen35-postproc-v3")]
    [InlineData("qwen3-postproc-v2")]
    public void Retired_lite_choices_migrate_to_cleanup_lite(string retired)
    {
        Assert.Equal(CleanupModelCatalog.MuesliCleanupId, CleanupModelCatalog.Normalize(retired));
        Assert.Equal("Muesli Cleanup Lite", CleanupModelCatalog.Get(CleanupModelCatalog.Normalize(retired))!.DisplayName);
    }
}
