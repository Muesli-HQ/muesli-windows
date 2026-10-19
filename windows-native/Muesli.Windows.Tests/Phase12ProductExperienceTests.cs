using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Restores the portable Phase 12 onboarding / lifecycle / microphone coverage that was retired
/// with the WPF test project. The WPF visual-preview, window-placement, and XAML source-scan cases
/// stay retired with the WPF shell; these cover the shared Core/Platform behavior the WinUI shell
/// also depends on.
/// </summary>
public sealed class Phase12ProductExperienceTests
{
    [Fact]
    public void Candidate_hook_result_remains_decisive_when_previous_hook_restoration_fails()
    {
        var restored = false;
        var result = HotkeyCandidateHookTest.Run(() => true, () => { restored = true; return false; });
        Assert.True(restored);
        Assert.True(result.CandidateRegistered);
        Assert.False(result.PreviousHookRestored);
    }

    [Theory]
    [InlineData(unchecked((int)0x8889000A), MicrophoneProbeFailure.Busy)]
    [InlineData(unchecked((int)0x88890004), MicrophoneProbeFailure.Disconnected)]
    [InlineData(unchecked((int)0x80070005), MicrophoneProbeFailure.Denied)]
    [InlineData(unchecked((int)0x80004005), MicrophoneProbeFailure.Unknown)]
    public void Microphone_hresult_mapping_is_pure(int hresult, MicrophoneProbeFailure expected) =>
        Assert.Equal(expected, WindowsMicrophoneAccessService.ClassifyFailure(new System.Runtime.InteropServices.COMException("test", hresult)));

    [Fact]
    public void Progress_serialization_never_includes_transcript_or_secrets()
    {
        var progress = new OnboardingProgress { LastStatus = "Dictation is working!" };
        var json = System.Text.Json.JsonSerializer.Serialize(progress);
        Assert.DoesNotContain("transcript", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resume_progress_clamps_steps_and_never_carries_secret_fields()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"muesli-phase12-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(directory, "onboarding-progress.json");
            var store = new OnboardingProgressStore(path);
            store.Save(new OnboardingProgress { Step = 999, Deferred = true, LastStatus = "model download paused" });
            var loaded = store.Load();
            Assert.Equal(OnboardingProgress.LastStep, loaded.Step);
            Assert.True(loaded.Deferred);
            Assert.Equal("model download paused", loaded.LastStatus);
            var persisted = File.ReadAllText(path);
            Assert.DoesNotContain("apiKey", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcript", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
