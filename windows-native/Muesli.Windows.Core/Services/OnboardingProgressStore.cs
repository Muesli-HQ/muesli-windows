using System.IO;

namespace Muesli.Windows.Services;

/// <summary>Durable, non-secret resume metadata for the setup window.</summary>
public sealed class OnboardingProgressStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly AtomicJsonFile _json;
    private readonly string _path;
    private bool _writeSuppressed;

    public OnboardingProgressStore(string? path = null, Action<string>? report = null)
    {
        _path = Path.GetFullPath(path ??
            Muesli.Windows.Core.Profiles.MuesliProfilePaths.Current().OnboardingProgressPath);
        _json = new AtomicJsonFile(report);
    }

    public OnboardingProgress Load()
    {
        _writeSuppressed = false;
        var loaded = _json.Load(_path, new OnboardingProgress());
        if (loaded.Value.SchemaVersion > CurrentSchemaVersion)
        {
            _writeSuppressed = true;
            return new OnboardingProgress();
        }
        return loaded.Value with
        {
            SchemaVersion = CurrentSchemaVersion,
            Step = Math.Clamp(loaded.Value.Step, 0, OnboardingProgress.LastStep),
            LastStatus = (loaded.Value.LastStatus ?? "").Trim()[..Math.Min((loaded.Value.LastStatus ?? "").Trim().Length, 300)]
        };
    }

    public void Save(OnboardingProgress progress)
    {
        if (!_writeSuppressed)
            _json.Save(_path, progress with { SchemaVersion = CurrentSchemaVersion, Step = Math.Clamp(progress.Step, 0, OnboardingProgress.LastStep) });
    }

    public void Clear()
    {
        // An empty durable record avoids a destructive delete while still clearing the resume gate.
        Save(new OnboardingProgress { Completed = true });
    }
}

public sealed record OnboardingProgress
{
    public const int LastStep = 6;
    public int SchemaVersion { get; init; } = OnboardingProgressStore.CurrentSchemaVersion;
    public int Step { get; init; }
    public bool Deferred { get; init; }
    public bool Completed { get; init; }
    public bool MicrophoneVerified { get; init; }
    public bool ModelsVerified { get; init; }
    public bool HotkeyVerified { get; init; }
    public bool PipelineVerified { get; init; }
    public string VerifiedMicrophone { get; init; } = "";
    public string VerifiedDictationModelId { get; init; } = "";
    public string VerifiedFinalModelId { get; init; } = "";
    public string? VerifiedLiveModelId { get; init; }
    public string VerifiedHotkey { get; init; } = "";
    public string LastStatus { get; init; } = "";
}

public sealed record OnboardingSelection(string? Microphone, string DictationModelId, string FinalModelId, string? LiveModelId, string Hotkey);

public static class OnboardingProgressReconciler
{
    /// <summary>
    /// Clears only the verification evidence invalidated by a user selection change.
    /// A successful pipeline test is evidence for both the microphone and selected model
    /// roles, so either change invalidates it as well.
    /// </summary>
    public static OnboardingProgress InvalidateForSelectionChange(OnboardingProgress progress, OnboardingSelection before, OnboardingSelection after)
    {
        var microphoneChanged = !Same(before.Microphone, after.Microphone);
        var modelsChanged = !Same(before.DictationModelId, after.DictationModelId) ||
                            !Same(before.FinalModelId, after.FinalModelId) ||
                            !Same(before.LiveModelId, after.LiveModelId);
        var hotkeyChanged = !Same(before.Hotkey, after.Hotkey);
        if (!microphoneChanged && !modelsChanged && !hotkeyChanged)
            return progress;

        var status = microphoneChanged || modelsChanged
            ? "A microphone or model role changed; repeat the affected verification and local dictation test."
            : "Shortcut changed; check the current shortcut again.";
        return progress with
        {
            MicrophoneVerified = microphoneChanged ? false : progress.MicrophoneVerified,
            VerifiedMicrophone = microphoneChanged ? "" : progress.VerifiedMicrophone,
            ModelsVerified = modelsChanged ? false : progress.ModelsVerified,
            VerifiedDictationModelId = modelsChanged ? "" : progress.VerifiedDictationModelId,
            VerifiedFinalModelId = modelsChanged ? "" : progress.VerifiedFinalModelId,
            VerifiedLiveModelId = modelsChanged ? null : progress.VerifiedLiveModelId,
            HotkeyVerified = hotkeyChanged ? false : progress.HotkeyVerified,
            VerifiedHotkey = hotkeyChanged ? "" : progress.VerifiedHotkey,
            PipelineVerified = microphoneChanged || modelsChanged ? false : progress.PipelineVerified,
            LastStatus = status
        };
    }

    public static OnboardingProgress Reconcile(OnboardingProgress progress, OnboardingSelection selection, Func<string, bool> offlineReady, Func<string, bool> liveReady)
    {
        var microphone = Same(progress.VerifiedMicrophone, selection.Microphone);
        var hotkey = Same(progress.VerifiedHotkey, selection.Hotkey);
        var models = Same(progress.VerifiedDictationModelId, selection.DictationModelId) &&
                     Same(progress.VerifiedFinalModelId, selection.FinalModelId) &&
                     Same(progress.VerifiedLiveModelId, selection.LiveModelId) &&
                     offlineReady(selection.DictationModelId) && offlineReady(selection.FinalModelId) &&
                     (selection.LiveModelId is null || liveReady(selection.LiveModelId));
        return progress with { MicrophoneVerified = progress.MicrophoneVerified && microphone, HotkeyVerified = progress.HotkeyVerified && hotkey, ModelsVerified = progress.ModelsVerified && models, PipelineVerified = progress.PipelineVerified && microphone && models };
    }
    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
