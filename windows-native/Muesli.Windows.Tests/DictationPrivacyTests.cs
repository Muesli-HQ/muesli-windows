using Muesli.Windows.Core.Services;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>macOS "Privacy mode" for dictation: pasted, not saved, no audio, no suggestions.</summary>
public sealed class DictationPrivacyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]   // turned off mid-dictation: still private
    [InlineData(false, true, true)]   // turned on mid-dictation: revoked at once
    [InlineData(true, true, true)]
    public void A_dictation_is_private_if_the_mode_was_on_at_the_start_or_is_on_now(bool atStart, bool now, bool expected) =>
        Assert.Equal(expected, DictationPrivacy.IsPrivate(atStart, now));

    [Fact]
    public void Off_by_default()
    {
        Assert.False(new MuesliSettings().PrivateMode);
        Assert.False(SettingsStore.NormalizeAfterDeserialization(new MuesliSettings()).PrivateMode);
    }

    [Fact]
    public void A_private_paste_returns_the_pill_to_idle_like_any_inserted_dictation()
    {
        Assert.Equal(FloatingIndicatorState.Idle,
            FloatingIndicatorStateClassifier.Classify(DictationPrivacy.InsertedStatus, isRecording: false, isTranscribing: false));
        Assert.Equal(FloatingIndicatorStateClassifier.Classify("Dictation inserted", false, false),
            FloatingIndicatorStateClassifier.Classify(DictationPrivacy.InsertedStatus, false, false));
    }

    [Fact]
    public void The_switch_lives_on_the_timeline_as_on_macos()
    {
        var timeline = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Pages", "TimelinePage.xaml"));
        var settings = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Pages", "SettingsPage.xaml"));
        Assert.Contains("PrivacyModeToggle", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivacyModeToggle", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dictation_pipeline_skips_history_audio_and_suggestions_when_private()
    {
        var context = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Services", "WinUiDictationContext.cs"));
        var prompts = File.ReadAllText(TestRepositoryLayout.Combine("windows-native", "Muesli.Windows.WinUI", "Services", "DictionarySuggestionPrompts.cs"));

        Assert.Contains("keepLatestDictationAlias: !isPrivate", context, StringComparison.Ordinal);
        Assert.Contains("if (!isPrivate)\r\n            {\r\n                _library.History.AppendDictation(", context.Replace("\r\n", "\n").Replace("\n", "\r\n"), StringComparison.Ordinal);
        Assert.Contains("if (!isPrivate) DictationPasted?.Invoke(", context, StringComparison.Ordinal);
        Assert.Contains("if (settings.PrivateMode) _monitor.Cancel();", prompts, StringComparison.Ordinal);
    }
}
