namespace Muesli.Windows.Services;

/// <summary>
/// Fail-closed Computer Use settings mapping shared by WPF and WinUI. Window text, page text, and
/// screenshots remain unavailable until masking qualification passes.
/// </summary>
public static class ComputerUseConfiguration
{
    public static IReadOnlyList<string> PlannerProviderLabels { get; } = ["None", "OpenAI"];
    public static IReadOnlyList<string> BrowserInterfaceLabels { get; } = ["Disabled", "Loopback DevTools"];

    public static string ProviderLabel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        _ => "None"
    };

    public static string ProviderSetting(string? value) => value switch
    {
        "OpenAI" => "openai",
        _ => "none"
    };

    public static string BrowserInterfaceLabel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "loopback-devtools" => "Loopback DevTools",
        _ => "Disabled"
    };

    public static string BrowserInterfaceSetting(string? value) => value switch
    {
        "Loopback DevTools" => "loopback-devtools",
        _ => "none"
    };

    public static string NormalizeAllowlist(string? value) => string.Join("; ", ParseAllowlist(value));

    public static string[] ParseAllowlist(string? value) => (value ?? "")
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(entry => entry.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool TryValidate(MuesliSettings settings, string? openAiKey, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(ProviderLabel(settings.ComputerUsePlannerProvider), "OpenAI", StringComparison.Ordinal))
        {
            error = "Choose the OpenAI planner provider explicitly.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.ComputerUsePlannerModel))
        {
            error = "Choose a planner model explicitly.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) &&
            string.IsNullOrWhiteSpace(openAiKey) &&
            string.IsNullOrWhiteSpace(settings.ResolvedOpenAIApiKey))
        {
            error = "Configure the OpenAI key before enabling Computer Use.";
            return false;
        }
        if (ParseAllowlist(settings.ComputerUseAllowedApplications).Length == 0)
        {
            error = "Allow at least one application before enabling Computer Use.";
            return false;
        }
        if (settings.ComputerUseIncludeWindowText ||
            settings.ComputerUseIncludeBrowserPageText ||
            settings.ComputerUseIncludeScreenshots)
        {
            error = "Text, page-text, and screenshot observation remain unavailable until scoped masking is verified. Leave those privacy options off.";
            return false;
        }
        if (string.Equals(BrowserInterfaceLabel(settings.ComputerUseBrowserInterface), "Loopback DevTools", StringComparison.Ordinal) &&
            ParseAllowlist(settings.ComputerUseAllowedBrowserDomains).Length == 0)
        {
            error = "Allow at least one HTTPS origin before enabling loopback DevTools.";
            return false;
        }

        error = "";
        return true;
    }

    public static ComputerUseOptions BuildOptions(MuesliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plannerTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.ComputerUsePlannerTimeoutSeconds, 5, 120));
        var perActionTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.ComputerUsePerActionTimeoutSeconds, 1, 30));
        var maximumActions = Math.Clamp(settings.ComputerUseMaximumActionCount, 1, 20);
        var overallSeconds = Math.Clamp(
            (int)plannerTimeout.TotalSeconds + maximumActions * (int)perActionTimeout.TotalSeconds + 30,
            10,
            300);
        return new ComputerUseOptions
        {
            Enabled = settings.ComputerUseEnabled,
            Provider = ComputerUsePlannerProvider.OpenAI,
            Model = settings.ComputerUsePlannerModel,
            PlannerTimeout = plannerTimeout,
            OverallTimeout = TimeSpan.FromSeconds(overallSeconds),
            PerActionTimeout = perActionTimeout,
            MaximumActionCount = maximumActions,
            AllowedApplications = ParseAllowlist(settings.ComputerUseAllowedApplications),
            AllowedBrowserDomains = ParseAllowlist(settings.ComputerUseAllowedBrowserDomains),
            Privacy = new ComputerUsePrivacyOptions
            {
                IncludeWindowText = false,
                IncludeBrowserPageText = false,
                IncludeScreenshots = false,
                UserApprovedScreenCapture = false
            }
        };
    }

    public static string DescribeResult(ComputerUseRunResult result) => result.Status switch
    {
        ComputerUseRunStatus.Completed => $"Computer Use completed {result.Trace.Count} validated action(s).",
        ComputerUseRunStatus.Disabled => "Computer Use is disabled; nothing was planned or executed.",
        ComputerUseRunStatus.Rejected => result.Error ?? "The planner response or context was rejected.",
        ComputerUseRunStatus.Cancelled => "Computer Use was stopped or a required confirmation was declined.",
        ComputerUseRunStatus.TimedOut => "Computer Use timed out; no further actions were sent.",
        ComputerUseRunStatus.StaleObservation => "The target changed; no further actions were sent.",
        _ => result.Error ?? "Computer Use failed safely; no further actions were sent."
    };

    public static string FormatDiagnostics(ComputerUseTraceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rows = document.Runs.Count == 0
            ? "No Computer Use runs are recorded."
            : string.Join(
                Environment.NewLine,
                document.Runs.Reverse().Take(20).Select(run =>
                    $"{run.CompletedAtUtc.LocalDateTime:g}  {run.Status}  actions={run.Actions.Count}  run={run.RunId}"));
        return $"Schema: {document.SchemaVersion}\nStored runs: {document.Runs.Count}\n\n{rows}\n\nCommands, typed values, titles, URLs, element names, screenshots, provider bodies, and errors are never stored.";
    }

    public static string TracePath(string profileRoot) => Path.Combine(
        Path.GetFullPath(profileRoot),
        "computer-use",
        "trace.json");

    public const string WinUiExecutionHostNotice =
        "Computer Use runs through the shared Windows UI Automation adapters. Only allowed applications can be targeted; actions with additional impact require confirmation. Commands and typed values are never saved in diagnostics.";
}
