namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// Converts between the app's timestamps and the INTEGER columns that hold them.
/// <para>
/// Storage is UTC ticks. The JSON history this store imports serialises full <see cref="DateTime"/>
/// precision, and migration verification compares hashes of the values before and after the move, so
/// rounding to seconds or milliseconds here would turn every imported timestamp into a verification
/// failure or, worse, a silently shifted one.
/// </para>
/// </summary>
public static class PersistenceTime
{
    public static long ToStorage(DateTimeOffset value) => value.UtcTicks;

    public static DateTimeOffset FromStorage(long ticks) => new(ticks, TimeSpan.Zero);

    public static long? ToStorage(DateTimeOffset? value) => value is null ? null : ToStorage(value.Value);

    public static DateTimeOffset? FromStorage(long? ticks) => ticks is null ? null : FromStorage(ticks.Value);

    /// <summary>
    /// Reads a <see cref="DateTime"/> from the JSON history as an absolute instant. An unspecified
    /// kind is read as UTC because that is what the app wrote: history timestamps come from
    /// <see cref="DateTime.UtcNow"/>, and a legacy file that lost the trailing Z must not be shifted
    /// by the reader's time zone.
    /// </summary>
    public static DateTimeOffset Normalize(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
        };

    /// <summary>The UTC <see cref="DateTime"/> the JSON records expect.</summary>
    public static DateTime ToDateTime(DateTimeOffset value) => value.UtcDateTime;
}
