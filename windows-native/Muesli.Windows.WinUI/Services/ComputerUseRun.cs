using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Muesli.Windows.ComputerUse;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS <c>ComputerUseTraceRecord</c>: a run's final status, message and steps, kept in a file
/// beside its dictation-history record (typed text included, screenshots never). Timeline and
/// Dictations render it like macOS <c>DictationRowView</c>.
/// </summary>
public sealed class ComputerUseRun : INotifyPropertyChanged
{


    private sealed record Saved(string Status, string Message, IReadOnlyList<CuaTraceEvent> Events);

    /// <summary>macOS final status ids: done, failed, timed_out, confirm, cancelled.</summary>
    public required string Status { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<ComputerUseStep> Steps { get; init; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the row shows the step trail (macOS chevron).</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>macOS <c>DictationRowView.displayFinalStatus</c>.</summary>
    public string StatusLabel => Status switch
    {
        "done" => "Done",
        "failed" => "Failed",
        "timed_out" => "Timed out",
        "confirm" => "Confirm",
        "cancelled" => "Cancelled",
        _ => Status,
    };

    public static void Save(string profileRoot, string dictationId, CuaRunResult result)
    {
        var path = ComputerUseConfiguration.RunStepsPath(profileRoot, dictationId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Saved(StatusId(result.Status.ToString()), result.Message, result.Trace)));
    }

    public static ComputerUseRun? Load(string profileRoot, string dictationId)
    {
        var path = ComputerUseConfiguration.RunStepsPath(profileRoot, dictationId);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is { } saved)
            {
                return new ComputerUseRun
                {
                    Status = saved.Status,
                    Message = saved.Message,
                    Steps = saved.Events.Select(item => new ComputerUseStep(item.Step is { } step ? $"Step {step}" : "Run", item.Title, item.Body)).ToList(),
                };
            }

            // Runs saved before the steps were structured: a status line, then "[n] Title: Body".
            var legacy = Path.ChangeExtension(path, ".txt");
            if (!File.Exists(legacy)) return null;
            var lines = File.ReadAllLines(legacy);
            var steps = lines.Skip(1).Select(LegacyStep).ToList();
            return new ComputerUseRun { Status = StatusId(lines.FirstOrDefault() ?? ""), Message = steps.LastOrDefault()?.Body ?? "", Steps = steps };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Delete(string profileRoot, string dictationId)
    {
        var path = ComputerUseConfiguration.RunStepsPath(profileRoot, dictationId);
        try
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".txt"));
        }
        catch (IOException) { }
    }

    /// <summary>macOS <c>ComputerUseTraceFormatter.debugText</c>, the "Copy CUA trace" text.</summary>
    public string TraceText(string command)
    {
        var lines = new List<string> { "CUA Command", command, "", "Final Status", Status, "", "Final Message", Message, "", "Step Trail" };
        foreach (var step in Steps)
        {
            lines.Add($"{step.StepLabel} - {step.Title}");
            lines.Add(step.Body);
            lines.Add("");
        }
        return string.Join("\n", lines).Trim();
    }

    private static string StatusId(string status) => status.Trim().ToLowerInvariant() switch
    {
        "done" => "done",
        "failed" => "failed",
        "timedout" or "timed_out" => "timed_out",
        "needsconfirmation" or "confirm" => "confirm",
        "cancelled" or "canceled" => "cancelled",
        var other => other,
    };

    private static ComputerUseStep LegacyStep(string line)
    {
        var match = Regex.Match(line, @"^(?:\[(\d+)\] )?([^:]+): (.*)$");
        return match.Success
            ? new ComputerUseStep(match.Groups[1].Success ? $"Step {match.Groups[1].Value}" : "Run", match.Groups[2].Value, match.Groups[3].Value)
            : new ComputerUseStep("Run", "", line);
    }
}

/// <summary>One line of a run's step trail: "Step 3" (or "Run"), the event title and its body.</summary>
public sealed record ComputerUseStep(string StepLabel, string Title, string Body);
