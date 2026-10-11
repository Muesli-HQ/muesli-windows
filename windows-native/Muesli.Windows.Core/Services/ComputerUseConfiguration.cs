namespace Muesli.Windows.Services;

/// <summary>Computer Use planner choice and readiness, shared by Settings and Shortcuts.</summary>
public static class ComputerUseConfiguration
{
    public static IReadOnlyList<string> PlannerProviderLabels { get; } = ["ChatGPT account", "OpenAI API key"];

    /// <summary>"none" is the old default; the ChatGPT account is the default planner.</summary>
    public static bool UsesApiKey(MuesliSettings settings) => settings.ComputerUsePlannerProvider == "openai";

    public static int ProviderIndex(string? value) => value == "openai" ? 1 : 0;

    public static string ProviderSetting(int index) => index == 1 ? "openai" : "chatgpt";

    public static bool TryValidate(MuesliSettings settings, bool chatGptSignedIn, out string error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        error = UsesApiKey(settings)
            ? string.IsNullOrWhiteSpace(settings.ResolvedOpenAIApiKey) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                ? "Add an OpenAI API key in Settings, or choose the ChatGPT account planner."
                : ""
            : chatGptSignedIn ? "" : "Sign in to ChatGPT in Settings > Computer Use first.";
        return error.Length == 0;
    }

    public static string TracePath(string profileRoot) => Path.Combine(Path.GetFullPath(profileRoot), "computer-use", "trace.txt");

    public const string Notice =
        "Computer Use sends the screen of the app it works in, and your spoken command, to the planner you choose. " +
        "It asks before sending, deleting, buying or closing, and never touches terminals, password managers or sign-in prompts.";
}
