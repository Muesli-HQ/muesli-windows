namespace Muesli.Windows.Services;

/// <summary>Shared setup-window copy. Step indices match <see cref="OnboardingProgress.LastStep"/>.</summary>
public static class OnboardingStepCatalog
{
    public static IReadOnlyList<OnboardingStepDefinition> Steps { get; } =
    [
        new(0, "Welcome to Muesli", "Choose a name and review the local-first setup. You can pause safely at any time."),
        new(1, "Microphone and permissions", "Windows policy is a hint. A short non-retained WASAPI capture is the real microphone check."),
        new(2, "Separate model roles", "Choosing a model never starts a download. Prepare or verify each selected role when you are ready."),
        new(3, "Dictation shortcut", "This advisory check detects common conflicts. The actual low-level dictation hook remains decisive."),
        new(4, "Try live dictation", "Record a short local test. It does not paste or add a history item; setup needs a non-empty transcript."),
        new(5, "Optional product choices", "Local summaries work without a key. Cloud providers disclose where transcript text may be sent."),
        new(6, "You are ready", "Finish once all required checks are green, or pause and resume from the dashboard or tray.")
    ];

    public static OnboardingStepDefinition Get(int step)
    {
        var index = Math.Clamp(step, 0, OnboardingProgress.LastStep);
        return Steps[index];
    }
}

public sealed record OnboardingStepDefinition(int Step, string Title, string Description);
