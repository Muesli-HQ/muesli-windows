namespace Muesli.Windows.Services;

/// <summary>macOS OnboardingCapability: what the user said they will use Muesli for.</summary>
[Flags]
public enum OnboardingCapabilities
{
    None = 0,
    VoiceNotes = 1,
    Dictation = 2,
    Meetings = 4,
    Everything = VoiceNotes | Dictation | Meetings
}

/// <summary>
/// macOS OnboardingUseCase and OnboardingFlow. Stored with the macOS raw values
/// ("voice_notes", "dictation_and_meetings", …) so the two apps read the same choice.
/// </summary>
public static class OnboardingFlow
{
    public const int Welcome = 0;
    public const int Model = 1;
    public const int Hotkey = 2;
    public const int Permissions = 3;
    public const int DictationTest = 4;
    public const int MeetingSummary = 5;

    public const string DefaultUseCase = "dictation";

    public static OnboardingCapabilities Parse(string? useCase) => useCase?.Trim().ToLowerInvariant() switch
    {
        "voice_notes" => OnboardingCapabilities.VoiceNotes,
        "meetings" => OnboardingCapabilities.Meetings,
        "voice_notes_and_dictation" => OnboardingCapabilities.VoiceNotes | OnboardingCapabilities.Dictation,
        "voice_notes_and_meetings" => OnboardingCapabilities.VoiceNotes | OnboardingCapabilities.Meetings,
        "dictation_and_meetings" => OnboardingCapabilities.Dictation | OnboardingCapabilities.Meetings,
        "everything" => OnboardingCapabilities.Everything,
        _ => OnboardingCapabilities.Dictation
    };

    public static string Format(OnboardingCapabilities capabilities) => capabilities switch
    {
        OnboardingCapabilities.VoiceNotes => "voice_notes",
        OnboardingCapabilities.Meetings => "meetings",
        OnboardingCapabilities.VoiceNotes | OnboardingCapabilities.Dictation => "voice_notes_and_dictation",
        OnboardingCapabilities.VoiceNotes | OnboardingCapabilities.Meetings => "voice_notes_and_meetings",
        OnboardingCapabilities.Dictation | OnboardingCapabilities.Meetings => "dictation_and_meetings",
        OnboardingCapabilities.Everything => "everything",
        _ => DefaultUseCase
    };

    public static bool IncludesPushToTalk(OnboardingCapabilities use) =>
        (use & (OnboardingCapabilities.VoiceNotes | OnboardingCapabilities.Dictation)) != 0;

    /// <summary>macOS defaultDictationOutputMode: voice notes without dictation keep the text in Muesli.</summary>
    public static bool IsVoiceNotesOnly(string? useCase)
    {
        var use = Parse(useCase);
        return use.HasFlag(OnboardingCapabilities.VoiceNotes) && !use.HasFlag(OnboardingCapabilities.Dictation);
    }

    /// <summary>A card toggles its capability; the last remaining one cannot be turned off.</summary>
    public static OnboardingCapabilities Toggle(OnboardingCapabilities use, OnboardingCapabilities capability)
    {
        var updated = use ^ capability;
        return updated == OnboardingCapabilities.None ? use : updated;
    }

    /// <summary>
    /// macOS orderedSteps without the calendar step: Windows has no calendar source, so Muesli
    /// never asks for one.
    /// </summary>
    public static List<int> OrderedSteps(OnboardingCapabilities use)
    {
        var steps = new List<int> { Welcome, Model };
        if (IncludesPushToTalk(use)) steps.AddRange([Hotkey, Permissions, DictationTest]);
        else if (use.HasFlag(OnboardingCapabilities.Meetings)) steps.Add(Permissions);
        if (use.HasFlag(OnboardingCapabilities.Meetings)) steps.Add(MeetingSummary);
        return steps;
    }

    public static int NormalizedStep(int step, OnboardingCapabilities use)
    {
        var steps = OrderedSteps(use);
        if (steps.Contains(step)) return step;
        return steps.FirstOrDefault(candidate => candidate > step, steps[^1]);
    }

    public static bool CanGoBack(int step, OnboardingCapabilities use, bool dictationTestSucceeded) =>
        OrderedSteps(use).IndexOf(step) > 0 && !(step == DictationTest && dictationTestSucceeded);
}
