using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Domain.Market;

/// <summary>How much of the day a holiday closes.</summary>
public enum Closure
{
    /// <summary>No trading that day.</summary>
    FullDay,

    /// <summary>MCX only: closed until 17:00, the evening session trades.</summary>
    MorningSession,

    /// <summary>MCX only: the morning session trades, closed from 17:00.</summary>
    EveningSession,
}

public sealed record Holiday(Exchange Exchange, DateOnly Date, string Name, Closure Closure, string Source);

/// <summary>A trading session on a day that is normally closed, such as a Sunday budget session.</summary>
public sealed record SpecialSession(Exchange Exchange, DateOnly Date, string Name, TimeOnly Opens, TimeOnly Closes, string Source);

/// <summary>
/// Which days and hours each exchange trades. An instrument's own session gives
/// the normal hours; weekends, holidays and special sessions change them.
/// </summary>
public sealed class ExchangeCalendar
{
    /// <summary>Where MCX splits its day when a holiday closes only one half.</summary>
    public static readonly TimeOnly McxEveningStarts = new(17, 0);

    public static readonly ExchangeCalendar Empty = new([], []);

    private readonly Dictionary<(Exchange, DateOnly), Holiday> _holidays;
    private readonly Dictionary<(Exchange, DateOnly), SpecialSession> _specialSessions;
    private readonly HashSet<(Exchange, int)> _years;

    public ExchangeCalendar(IEnumerable<Holiday> holidays, IEnumerable<SpecialSession> specialSessions)
    {
        _holidays = holidays.ToDictionary(h => (h.Exchange, h.Date));
        _specialSessions = specialSessions.ToDictionary(s => (s.Exchange, s.Date));
        _years = _holidays.Keys.Select(k => (k.Item1, k.Item2.Year))
            .Concat(_specialSessions.Keys.Select(k => (k.Item1, k.Item2.Year)))
            .ToHashSet();
    }

    public int HolidayCount => _holidays.Count;

    /// <summary>Whether the calendar was given any dates for that exchange and year.</summary>
    public bool Covers(Exchange exchange, int year) => _years.Contains((exchange, year));

    public Holiday? HolidayOn(Exchange exchange, DateOnly date) => _holidays.GetValueOrDefault((exchange, date));

    public IEnumerable<Holiday> Holidays => _holidays.Values.OrderBy(h => h.Date).ThenBy(h => h.Exchange);

    public IEnumerable<SpecialSession> SpecialSessions => _specialSessions.Values.OrderBy(s => s.Date).ThenBy(s => s.Exchange);

    /// <summary>The hours the instrument trades on <paramref name="date"/>, or null when it does not trade that day.</summary>
    public TradingSession? WindowFor(Instrument instrument, DateOnly date)
        => WindowFor(instrument.Exchange, instrument.Session, date);

    /// <summary>The hours a contract with the <paramref name="normal"/> session trades on the exchange that day.</summary>
    public TradingSession? WindowFor(Exchange exchange, TradingSession normal, DateOnly date)
    {
        if (_specialSessions.TryGetValue((exchange, date), out var special))
            return new TradingSession(special.Opens, special.Closes);

        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;

        if (!_holidays.TryGetValue((exchange, date), out var holiday)) return normal;

        return holiday.Closure switch
        {
            Closure.MorningSession when normal.Closes > McxEveningStarts =>
                new TradingSession(normal.Opens > McxEveningStarts ? normal.Opens : McxEveningStarts, normal.Closes),
            Closure.EveningSession when normal.Opens < McxEveningStarts =>
                new TradingSession(normal.Opens, normal.Closes < McxEveningStarts ? normal.Closes : McxEveningStarts),
            _ => null,
        };
    }

    public bool IsOpen(Instrument instrument, DateTimeOffset at)
        => WindowFor(instrument, Ist.DateOf(at)) is { } window && window.Contains(Ist.TimeOf(at));

    /// <summary>When today's session for the instrument ends, or null when it does not trade today.</summary>
    public DateTimeOffset? SessionEnd(Instrument instrument, DateOnly date)
        => WindowFor(instrument, date) is { } window ? Ist.At(date, window.Closes) : null;
}
