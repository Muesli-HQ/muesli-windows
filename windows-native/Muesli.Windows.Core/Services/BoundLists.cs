namespace Muesli.Windows.Services;

/// <summary>
/// MAC-37 (macOS #632): a refresh writes bound state only when it changes. Returning the current
/// instance for an equal list lets SetProperty no-op, so an unchanged reload raises no
/// PropertyChanged and doesn't reset ComboBox selections.
/// </summary>
public static class BoundLists
{
    public static IReadOnlyList<T> Same<T>(IReadOnlyList<T> current, IReadOnlyList<T> next) =>
        current.SequenceEqual(next) ? current : next;
}
