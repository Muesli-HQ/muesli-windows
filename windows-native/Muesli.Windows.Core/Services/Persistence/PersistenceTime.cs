namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Converts between the app's timestamps and the INTEGER columns that hold them.
/// <para>
/// Storage is UTC ticks. The app's records carry full <see cref="DateTime"/> precision, so rounding
/// to seconds or milliseconds here would silently shift a timestamp on its way into the store.
/// </para>
/// </summary>
public static class PersistenceTime
{
    public static long ToStorage(DateTimeOffset value) => value.UtcTicks;

    public static DateTimeOffset FromStorage(long ticks) => new(ticks, TimeSpan.Zero);

    public static long? ToStorage(DateTimeOffset? value) => value is null ? null : ToStorage(value.Value);

    public static DateTimeOffset? FromStorage(long? ticks) => ticks is null ? null : FromStorage(ticks.Value);

    /// <summary>
    /// Reads a record's <see cref="DateTime"/> as an absolute instant. An unspecified kind is read
    /// as UTC because history timestamps come from <see cref="DateTime.UtcNow"/>, and a value that
    /// lost its kind must not be shifted by the reader's time zone.
    /// </summary>
    public static DateTimeOffset Normalize(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
        };

    /// <summary>The UTC <see cref="DateTime"/> the app's records expect.</summary>
    public static DateTime ToDateTime(DateTimeOffset value) => value.UtcDateTime;
}
