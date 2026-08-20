namespace Muesli.Windows;

public sealed record DictationItem(
    string Id,
    DateTime Timestamp,
    string Time,
    string Text,
    string ModelProfile,
    int DurationMs)
{
    private DateTime LocalTimestamp => Timestamp.Kind == DateTimeKind.Utc ? Timestamp.ToLocalTime() : Timestamp;

    public string DateGroupLabel => LocalTimestamp.Date switch
    {
        var date when date == DateTime.Today => "TODAY",
        var date when date == DateTime.Today.AddDays(-1) => "YESTERDAY",
        _ => LocalTimestamp.ToString("MMMM d, yyyy")
    };
}
