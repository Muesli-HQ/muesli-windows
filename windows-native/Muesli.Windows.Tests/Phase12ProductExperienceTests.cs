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
    public void Selection_transitions_clear_only_the_verification_evidence_they_invalidate()
    {
        var progress = new OnboardingProgress
        {
            MicrophoneVerified = true, ModelsVerified = true, HotkeyVerified = true, PipelineVerified = true,
            VerifiedMicrophone = "mic-a", VerifiedDictationModelId = "dictation-a", VerifiedFinalModelId = "final-a",
            VerifiedLiveModelId = "live-a", VerifiedHotkey = "F8"
        };
        var original = new OnboardingSelection("mic-a", "dictation-a", "final-a", "live-a", "F8");

        var microphoneChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { Microphone = "mic-b" });
        Assert.False(microphoneChanged.MicrophoneVerified);
        Assert.False(microphoneChanged.PipelineVerified);
        Assert.True(microphoneChanged.ModelsVerified);
        Assert.True(microphoneChanged.HotkeyVerified);
        Assert.Equal("", microphoneChanged.VerifiedMicrophone);

        var modelChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { LiveModelId = null });
        Assert.False(modelChanged.ModelsVerified);
        Assert.False(modelChanged.PipelineVerified);
        Assert.Equal("", modelChanged.VerifiedDictationModelId);
        Assert.Equal("", modelChanged.VerifiedFinalModelId);
        Assert.Null(modelChanged.VerifiedLiveModelId);

        var hotkeyChanged = OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original with { Hotkey = "F9" });
        Assert.False(hotkeyChanged.HotkeyVerified);
        Assert.Equal("", hotkeyChanged.VerifiedHotkey);
        Assert.True(hotkeyChanged.PipelineVerified);
        Assert.Equal(progress, OnboardingProgressReconciler.InvalidateForSelectionChange(progress, original, original));
    }

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
    [InlineData("F8", "F8", true)]
    [InlineData("F8", "F9", false)]
    public void Reconciliation_invalidates_hotkey_when_selection_changes(string verified, string selected, bool expected)
    {
        var progress = new OnboardingProgress { HotkeyVerified = true, VerifiedHotkey = verified };
        var result = OnboardingProgressReconciler.Reconcile(progress, new("mic", "d", "f", null, selected), _ => true, _ => true);
        Assert.Equal(expected, result.HotkeyVerified);
    }

    [Fact]
    public void Reconciliation_requires_current_model_lifecycle_readiness()
    {
        var progress = new OnboardingProgress { ModelsVerified = true, PipelineVerified = true, VerifiedMicrophone = "mic", MicrophoneVerified = true, VerifiedDictationModelId = "d", VerifiedFinalModelId = "f" };
        var result = OnboardingProgressReconciler.Reconcile(progress, new("mic", "d", "f", null, "F8"), id => id == "d", _ => true);
        Assert.False(result.ModelsVerified);
        Assert.False(result.PipelineVerified);
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
        var progress = new OnboardingProgress { LastStatus = "Pipeline test passed", VerifiedMicrophone = "mic" };
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
