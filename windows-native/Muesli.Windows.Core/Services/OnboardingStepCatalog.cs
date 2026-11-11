namespace Muesli.Windows.Services;

/// <summary>Shared setup-window copy. Step indices match <see cref="OnboardingProgress.LastStep"/>.</summary>
public static class OnboardingStepCatalog
{
    public static IReadOnlyList<OnboardingStepDefinition> Steps { get; } =
    [
        // User-facing copy in the macOS onboarding register: lowercase "muesli", no internals.
        new(0, "Welcome to muesli", "Local-first dictation and meeting transcription for Windows."),
        new(1, "Microphone", "muesli listens only while you dictate or take meeting notes. Check that Windows lets it hear you."),
        new(2, "Choose your transcription model", "Start with a fast local model. Larger models can download while you continue setup."),
        new(3, "Dictation Shortcut", "Choose the key you'll press to dictate. You can change it later in Shortcuts."),
        new(4, "Test Dictation", "Record a short test and your words appear below. Nothing is saved or pasted."),
        new(5, "Meeting Summaries", "Summaries run locally without a key. Remote providers may send transcripts off-device."),
        new(6, "You're all set", "Everything below is ready. Finish to open muesli, or set up the rest later.")
    ];

    public static OnboardingStepDefinition Get(int step)
    {
        var index = Math.Clamp(step, 0, OnboardingProgress.LastStep);
        return Steps[index];
    }
}

public sealed record OnboardingStepDefinition(int Step, string Title, string Description);
