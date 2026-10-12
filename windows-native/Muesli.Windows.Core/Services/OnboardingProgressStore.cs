using System.IO;

namespace Muesli.Windows.Services;

/// <summary>Durable, non-secret resume metadata for the setup window.</summary>
public sealed class OnboardingProgressStore
{
    public const int CurrentSchemaVersion = 2;
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
            // Schema 1 numbered the old checklist's steps; resuming it starts the new flow over.
            Step = loaded.Value.SchemaVersion < CurrentSchemaVersion ? 0 : Math.Clamp(loaded.Value.Step, 0, OnboardingProgress.LastStep),
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

/// <summary>macOS OnboardingProgress: the step to resume at. Choices live in settings as they are made.</summary>
public sealed record OnboardingProgress
{
    public const int LastStep = OnboardingFlow.MeetingSummary;
    public int SchemaVersion { get; init; } = OnboardingProgressStore.CurrentSchemaVersion;
    public int Step { get; init; }
    public bool Deferred { get; init; }
    public bool Completed { get; init; }
    public string LastStatus { get; init; } = "";
}
