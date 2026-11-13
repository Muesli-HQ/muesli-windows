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

/// <summary>
/// macOS OnboardingUseCase, limited to what Windows v1 ships: dictation and meetings. The answer
/// on the Welcome screen decides which steps follow (macOS OnboardingFlow.orderedSteps).
/// </summary>
public static class OnboardingUseCases
{
    public const string Dictation = "dictation";
    public const string Meetings = "meetings";
    public const string Everything = "dictation_and_meetings";

    public static string Resolve(string? value) => value is Dictation or Meetings ? value : Everything;

    public static bool IncludesDictation(string? useCase) => Resolve(useCase) != Meetings;

    public static bool IncludesMeetings(string? useCase) => Resolve(useCase) != Dictation;

    /// <summary>Turns one workflow on or off; at least one always stays on, as on macOS.</summary>
    public static string Toggle(string? useCase, string workflow)
    {
        var dictation = IncludesDictation(useCase);
        var meetings = IncludesMeetings(useCase);
        if (workflow == Dictation) dictation = !dictation;
        else if (workflow == Meetings) meetings = !meetings;
        return (dictation, meetings) switch
        {
            (true, false) => Dictation,
            (false, true) => Meetings,
            (false, false) => Resolve(useCase),
            _ => Everything
        };
    }

    /// <summary>
    /// Dictation needs the shortcut and the dictation test; meetings need the summary choice.
    /// Every path keeps the model, the microphone and the closing checklist.
    /// </summary>
    public static IReadOnlyList<int> Steps(string? useCase)
    {
        var steps = new List<int> { OnboardingSteps.Welcome, OnboardingSteps.Model };
        if (IncludesDictation(useCase)) steps.Add(OnboardingSteps.Shortcut);
        steps.Add(OnboardingSteps.Microphone);
        if (IncludesDictation(useCase)) steps.Add(OnboardingSteps.Test);
        if (IncludesMeetings(useCase)) steps.Add(OnboardingSteps.Summaries);
        steps.Add(OnboardingSteps.Ready);
        return steps;
    }

    /// <summary>A step the use case skips resolves to the next one it keeps.</summary>
    public static int Normalize(int step, string? useCase)
    {
        var steps = Steps(useCase);
        return steps.Contains(step) ? step : steps.FirstOrDefault(candidate => candidate > step, OnboardingSteps.Ready);
    }
}
