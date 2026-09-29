namespace Muesli.Windows.UITests;

/// <summary>
/// Explicit opt-in gate for out-of-process UI Automation.
/// Set <c>MUESLI_UI_AUTOMATION=1</c> to run; <c>0</c> or an unset value skips.
/// The harness temporarily swaps the production APPDATA profile, so an explicit
/// opt-in is required even on an interactive desktop.
/// </summary>
internal static class UiAutomationEnvironment
{
    public const string EnableVariable = "MUESLI_UI_AUTOMATION";
    public const string TraitName = "Category";
    public const string TraitValue = "UiAutomation";

    public static bool ShouldRun => SkipReason is null;

    public static string? SkipReason
    {
        get
        {
            var flag = Environment.GetEnvironmentVariable(EnableVariable)?.Trim();
            if (flag is "0" or "false" or "FALSE" or "no" or "NO")
                return $"Skipped because {EnableVariable}={flag}.";

            var forced = flag is "1" or "true" or "TRUE" or "yes" or "YES";
            if (!forced)
                return $"Skipped because {EnableVariable} is not enabled. Set {EnableVariable}=1 on an interactive desktop to run L09 UI Automation.";

            if (!Environment.UserInteractive)
                return "Skipped because the test host is not user-interactive.";

            var session = Environment.GetEnvironmentVariable("SESSIONNAME");
            if (string.Equals(session, "Services", StringComparison.OrdinalIgnoreCase))
                return "Skipped because there is no interactive desktop (session 0).";

            return null;
        }
    }

}
