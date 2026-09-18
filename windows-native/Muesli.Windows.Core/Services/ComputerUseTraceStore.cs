using System.Text.Json;

namespace Muesli.Windows.Services;

/// <summary>
/// A deliberately narrow local diagnostic history. It persists no commands, titles, AutomationIds,
/// values, URLs, screenshots, provider bodies, or credential material. The caller owns its path.
/// </summary>
public sealed class ComputerUseTraceStore
{
    public const int SchemaVersion = 1;
    private const int MaximumRuns = 50;
    private const int MaximumActionsPerRun = 20;
    private readonly AtomicJsonFile _file;

    public ComputerUseTraceStore(AtomicJsonFile? file = null) => _file = file ?? new AtomicJsonFile();

    public void Append(string path, ComputerUseRunResult run)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A trace path is required.", nameof(path));
        var current = Load(path);
        var safe = new ComputerUseTraceRun(
            run.RunId,
            run.Status,
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.Trace.Take(MaximumActionsPerRun)
                .Select(entry => new ComputerUseTraceAction(entry.Index, entry.Kind, entry.ApplicationId, entry.Risk, entry.Outcome, entry.AtUtc))
                .ToArray());
        var all = current.Runs.Append(safe).TakeLast(MaximumRuns).ToArray();
        _file.Save(path, new ComputerUseTraceDocument(SchemaVersion, all));
    }

    public ComputerUseTraceDocument Load(string path)
    {
        var fallback = new ComputerUseTraceDocument(SchemaVersion, Array.Empty<ComputerUseTraceRun>());
        var document = _file.Load(path, fallback).Value;
        return document.SchemaVersion == SchemaVersion ? document : fallback;
    }
}

public sealed record ComputerUseTraceDocument(int SchemaVersion, IReadOnlyList<ComputerUseTraceRun> Runs);
public sealed record ComputerUseTraceRun(Guid RunId, ComputerUseRunStatus Status, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<ComputerUseTraceAction> Actions);
public sealed record ComputerUseTraceAction(int Index, ComputerUseActionKind Kind, string ApplicationId, ComputerUseRisk Risk, string Outcome, DateTimeOffset AtUtc);
