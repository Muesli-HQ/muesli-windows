namespace Muesli.Windows.Services;

/// <summary>
/// Setup steps in the macOS order (OnboardingFlow.Step): welcome, model, shortcut, then the
/// permission, the dictation test and meeting summaries. Windows ends on a Ready checklist where
/// macOS asks for calendar access.
/// </summary>
public static class OnboardingSteps
{
    public const int Welcome = 0;
    public const int Model = 1;
    public const int Shortcut = 2;
    public const int Microphone = 3;
    public const int Test = 4;
    public const int Summaries = 5;
    public const int Ready = 6;

    /// <summary>Schema 1 put Microphone at 1, Model at 2 and Shortcut at 3.</summary>
    public static int FromSchema1(int step) => step switch
    {
        1 => Microphone,
        2 => Model,
        3 => Shortcut,
        _ => step
    };
}

/// <summary>Shared setup-window copy, indexed by <see cref="OnboardingSteps"/>.</summary>
public static class OnboardingStepCatalog
{
    public static IReadOnlyList<OnboardingStepDefinition> Steps { get; } =
    [
        // User-facing copy in the macOS onboarding register: lowercase "muesli", no internals.
        new(OnboardingSteps.Welcome, "Welcome to muesli", "Local-first dictation and meeting transcription for Windows."),
        new(OnboardingSteps.Model, "Choose your transcription model", "Start with a fast local model. Larger models can download while you continue setup."),
        new(OnboardingSteps.Shortcut, "Dictation Shortcut", "Choose the key you'll hold to dictate. Press and hold the key to record, release to transcribe."),
        new(OnboardingSteps.Microphone, "Microphone", "muesli listens only while you dictate or take meeting notes. Check that Windows lets it hear you."),
        new(OnboardingSteps.Test, "Test Dictation", "Hold your shortcut and say something. Nothing is saved or pasted."),
        new(OnboardingSteps.Summaries, "Meeting Summaries", "Summaries run locally without a key. Remote providers may send transcripts off-device."),
        new(OnboardingSteps.Ready, "You're all set", "Everything below is ready. Finish to open muesli, or set up the rest later.")
    ];

    public static OnboardingStepDefinition Get(int step)
    {
        var index = Math.Clamp(step, 0, OnboardingProgress.LastStep);
        return Steps[index];
    }
}

public sealed record OnboardingStepDefinition(int Step, string Title, string Description);
