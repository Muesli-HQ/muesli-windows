namespace Muesli.Windows.Services;

/// <summary>Computer Use planner choice and readiness, shared by Settings and Shortcuts.</summary>
public static class ComputerUseConfiguration
{
    public const string DefaultHotkey = "Ctrl+Shift+F8";

    /// <summary>The dictation-history model label that marks a Computer Use run.</summary>
    public const string HistoryLabel = "Computer Use";

    public sealed record PlannerModel(string Id, string Label, IReadOnlyList<string> Efforts, string DefaultEffort);

    private static readonly string[] SolEfforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] AllEfforts = ["none", "low", "medium", "high", "xhigh", "max"];

    /// <summary>macOS <c>Models.swift</c> planner presets and <c>ReasoningEffortPolicy</c>.</summary>
    public static IReadOnlyList<PlannerModel> PlannerModels { get; } =
    [
        new("gpt-6.1-sol", "GPT-6.1 Sol (default)", SolEfforts, "medium"),
        new("gpt-6-astra", "GPT-6 Astra", SolEfforts, "high"),
        new("gpt-6-sol", "GPT-6 Sol", AllEfforts, "medium"),
        new("gpt-6-luna", "GPT-6 Luna", AllEfforts, "medium"),
    ];

    /// <summary>The saved model, or the default when it is empty or not a preset.</summary>
    public static PlannerModel Model(string? id) =>
        PlannerModels.FirstOrDefault(model => model.Id == id?.Trim()) ?? PlannerModels[0];

    /// <summary>The saved effort when the model supports it, otherwise the model's default.</summary>
    public static string Effort(PlannerModel model, string? effort) =>
        model.Efforts.Contains(effort) ? effort! : model.DefaultEffort;

    public static string EffortLabel(string effort) => effort switch
    {
        "none" => "None",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "Extra High",
        "max" => "Maximum",
        _ => effort,
    };

    public static bool TryValidate(MuesliSettings settings, bool chatGptSignedIn, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        error = chatGptSignedIn ? "" : "Sign in to ChatGPT in Settings > Computer Use first.";
        return error.Length == 0;
    }

    /// <summary>The steps of one run, kept beside its dictation-history record (screenshots never).</summary>
    public static string RunStepsPath(string profileRoot, string dictationId) =>
        Path.Combine(Path.GetFullPath(profileRoot), "computer-use", "runs", Path.GetFileName(dictationId) + ".txt");

    public const string Notice =
        "Computer Use sends the screen of the app it works in, and your spoken command, to ChatGPT. " +
        "It asks before sending, deleting, buying or closing, and never touches terminals, password managers or sign-in prompts.";
}
