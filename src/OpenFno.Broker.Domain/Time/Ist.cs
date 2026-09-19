namespace OpenFno.Broker.Domain.Time;

/// <summary>
/// India Standard Time. IST is a fixed UTC+05:30 with no daylight saving, so a
/// constant offset is exact and needs no time-zone database.
/// </summary>
public static class Ist
{
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

    public static DateTimeOffset ToIst(DateTimeOffset at) => at.ToOffset(Offset);

    /// <summary>The IST calendar date of an instant: the trading date an order belongs to.</summary>
    public static DateOnly DateOf(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Offset).DateTime);

    public static TimeOnly TimeOf(DateTimeOffset at) => TimeOnly.FromDateTime(at.ToOffset(Offset).DateTime);

    public static DateTimeOffset At(DateOnly date, TimeOnly time) => new(date.ToDateTime(time), Offset);

    /// <summary>The first instant at or after <paramref name="from"/> whose IST clock reads <paramref name="time"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset from, TimeOnly time)
    {
        var candidate = At(DateOf(from), time);
        return candidate > from ? candidate : candidate.AddDays(1);
    }

    /// <summary>Monday of the IST calendar week that contains <paramref name="at"/>.</summary>
    public static DateOnly WeekStart(DateTimeOffset at)
    {
        var date = DateOf(at);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
