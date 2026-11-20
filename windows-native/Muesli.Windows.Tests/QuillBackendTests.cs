using System;
using System.Threading.Tasks;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-27: Quill's writing backend (macOS quil_backend), gated on its own model, not on hosted credentials.</summary>
public sealed class QuillBackendTests
{
    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData("gemma4LiteRT", "local")]
    [InlineData("ChatGPT", "chatgpt")]
    public void Unknown_or_missing_backends_resolve_to_local(string? saved, string expected)
    {
        Assert.Equal(expected, QuillBackends.Normalize(saved));
        Assert.Equal(expected, SettingsStore.NormalizeAfterDeserialization(new MuesliSettings { QuillBackend = saved! }).QuillBackend);
        Assert.Equal("local", new MuesliSettings().QuillBackend);
    }

    [Fact]
    public void Only_the_general_model_follows_quill_instructions()
    {
        Assert.Equal(CleanupModelCatalog.QwenBasicId, QuillBackends.LocalModelId);
        Assert.Equal(2_500, QuillBackends.LocalMaximumInputCharacters);
    }

    [Fact]
    public async Task Local_limit_rejects_long_selections_before_generation()
    {
        var called = false;
        var error = await Assert.ThrowsAsync<QuillException>(() => QuillTransformation.TransformAsync(
            new string('a', 2_501), "shorten",
            (_, _, _) => { called = true; return Task.FromResult("x"); },
            default, QuillBackends.LocalMaximumInputCharacters));
        Assert.Contains("2,500", error.Message);
        Assert.False(called);
    }

    [Fact]
    public void Local_prompt_is_one_chatml_turn_with_thinking_suppressed()
    {
        var prompt = NativeTextCleanupService.QuillPrompt("SYS", "USER");
        Assert.Equal("<|im_start|>system\nSYS<|im_end|>\n<|im_start|>user\nUSER<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);
    }
}
