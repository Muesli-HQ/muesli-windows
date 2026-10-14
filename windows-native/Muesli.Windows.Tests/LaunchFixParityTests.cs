using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class LaunchFixParityTests
{
    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(">> [BLANK_AUDIO]", "")]
    [InlineData("Hello [inaudible] there.", "Hello there.")]
    [InlineData("<|endoftext|>Ship it.", "Ship it.")]
    [InlineData("Transcribe the spoken audio accurately. Buy milk.", "Buy milk.")]
    [InlineData("Keep this sentence.", "Keep this sentence.")]
    public void Engine_artifacts_are_removed_and_speech_is_kept(string input, string expected) =>
        Assert.Equal(expected, TranscriptionEngineArtifactsFilter.Apply(input));

    [Theory]
    [InlineData("Done.", " ")]
    [InlineData("Is it \"done?\"", " ")]
    [InlineData("no punctuation", "")]
    [InlineData("trailing space. ", "")]
    [InlineData("完成。", "")]
    public void Paste_spacing_matches_macos(string text, string expected) =>
        Assert.Equal(expected, DictationPasteSpacing.TrailingSeparator(text));

    [Theory]
    [InlineData("You know, it works.", "It works.")]
    [InlineData("Do you know him?", "Do you know him?")]
    [InlineData("I mean what I say.", "I mean what I say.")]
    [InlineData("It is, I mean, fine.", "It is, fine.")]
    public void Comma_phrase_fillers_need_a_comma(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Apply(input));

    [Theory]
    [InlineData("Microphone blocked for Muesli. Open Windows microphone privacy settings to allow it.")]
    [InlineData("Could not start microphone: device gone")]
    [InlineData("Download Parakeet in Models to dictate")]
    public void Dictation_start_failures_show_as_pill_errors(string status) =>
        Assert.Equal(FloatingIndicatorState.Error, FloatingIndicatorStateClassifier.Classify(status, false, false));
}
