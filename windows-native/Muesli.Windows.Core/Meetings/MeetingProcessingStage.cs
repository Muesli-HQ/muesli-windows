namespace Muesli.Windows.Services;

/// <summary>The steps a stopped meeting goes through, in order, as macOS names them on its pill.</summary>
public enum MeetingProcessingStage
{
    StoppingAudio,
    Transcribing,
    IdentifyingSpeakers,
    Cleaning,
    Titling,
    Summarizing
}

public static class MeetingProcessingStages
{
    public static string Label(MeetingProcessingStage stage) => stage switch
    {
        MeetingProcessingStage.StoppingAudio => "Stopping Audio",
        MeetingProcessingStage.Transcribing => "Transcribing",
        MeetingProcessingStage.IdentifyingSpeakers => "Identifying Speakers",
        MeetingProcessingStage.Cleaning => "Cleaning",
        MeetingProcessingStage.Titling => "Titling",
        MeetingProcessingStage.Summarizing => "Summarizing",
        _ => "Transcribing"
    };
}