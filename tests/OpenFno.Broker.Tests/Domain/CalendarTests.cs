using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Tests.Support;

namespace OpenFno.Broker.Tests.Domain;

public class TradingSessionTests
{
    [Theory]
    [InlineData("0915-1530|1815-1915:", 9, 15, 15, 30)]
    [InlineData("0900-2330|1815-1915:", 9, 0, 23, 30)]
    [InlineData("0915-1540", 9, 15, 15, 40)]
    public void Reads_the_first_range_of_the_fyers_session_column(string text, int oh, int om, int ch, int cm)
    {
        Assert.True(TradingSession.TryParse(text, out var session));
        Assert.Equal(new TradingSession(new TimeOnly(oh, om), new TimeOnly(ch, cm)), session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0915")]
    [InlineData("1530-0915")]
    [InlineData("09:15-15:30")]
    public void Refuses_what_it_cannot_read(string? text) => Assert.False(TradingSession.TryParse(text, out _));

    [Fact]
    public void Closing_time_is_exclusive()
    {
        var session = TradingSession.NseCash;
        Assert.True(session.Contains(new TimeOnly(9, 15)));
        Assert.True(session.Contains(new TimeOnly(15, 29, 59)));
        Assert.False(session.Contains(new TimeOnly(15, 30)));
        Assert.False(session.Contains(new TimeOnly(9, 14, 59)));
    }
}

public class ExchangeCalendarTests
{
    private readonly ExchangeCalendar _calendar = Fixtures.Calendar();

    private static DateTimeOffset At(int month, int day, int hour, int minute = 0)
        => Ist.At(new DateOnly(2026, month, day), new TimeOnly(hour, minute));

    [Fact]
    public void Open_during_the_instruments_session_on_a_weekday()
    {
        Assert.True(_calendar.IsOpen(Fixtures.Sbin, At(9, 18, 10)));
        Assert.False(_calendar.IsOpen(Fixtures.Sbin, At(9, 18, 9, 10)));
        Assert.False(_calendar.IsOpen(Fixtures.Sbin, At(9, 18, 15, 30)));
        Assert.True(_calendar.IsOpen(Fixtures.CrudeFuture, At(9, 18, 22)));
    }

    [Fact]
    public void Closed_at_weekends_and_on_full_holidays()
    {
        Assert.False(_calendar.IsOpen(Fixtures.Sbin, At(9, 19, 11)));
        Assert.False(_calendar.IsOpen(Fixtures.Sbin, At(10, 2, 11)));
        Assert.Equal("Mahatma Gandhi Jayanti", _calendar.HolidayOn(Exchange.Nse, new DateOnly(2026, 10, 2))?.Name);
    }

    [Fact]
    public void A_holiday_on_one_exchange_leaves_the_others_open()
        => Assert.True(_calendar.IsOpen(Fixtures.CrudeFuture, At(10, 2, 11)));

    [Fact]
    public void Mcx_morning_closure_opens_the_evening_session_at_five()
    {
        Assert.False(_calendar.IsOpen(Fixtures.CrudeFuture, At(10, 20, 11)));
        Assert.True(_calendar.IsOpen(Fixtures.CrudeFuture, At(10, 20, 17)));
        Assert.True(_calendar.IsOpen(Fixtures.CrudeFuture, At(10, 20, 23, 29)));
    }

    [Fact]
    public void Mcx_evening_closure_ends_the_day_at_five()
    {
        Assert.True(_calendar.IsOpen(Fixtures.CrudeFuture, At(1, 1, 11)));
        Assert.False(_calendar.IsOpen(Fixtures.CrudeFuture, At(1, 1, 17)));
    }

    [Fact]
    public void A_special_session_opens_a_sunday()
    {
        Assert.True(_calendar.IsOpen(Fixtures.Sbin, At(2, 1, 11)));
        Assert.False(_calendar.IsOpen(Fixtures.CrudeFuture, At(2, 1, 11)));
    }

    [Fact]
    public void Session_end_is_the_close_of_the_day_window()
    {
        Assert.Equal(At(9, 18, 15, 30), _calendar.SessionEnd(Fixtures.Sbin, new DateOnly(2026, 9, 18)));
        Assert.Null(_calendar.SessionEnd(Fixtures.Sbin, new DateOnly(2026, 9, 19)));
    }
}

public class IstTests
{
    [Fact]
    public void Next_finds_the_coming_occurrence_of_a_clock_time()
    {
        var before = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(5, 0));
        var after = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(10, 0));
        Assert.Equal(Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(6, 0)), Ist.Next(before, new TimeOnly(6, 0)));
        Assert.Equal(Ist.At(new DateOnly(2026, 9, 19), new TimeOnly(6, 0)), Ist.Next(after, new TimeOnly(6, 0)));
    }

    [Fact]
    public void The_trading_date_is_the_ist_date_not_the_utc_date()
    {
        // 20:00 UTC on the 18th is 01:30 IST on the 19th.
        var lateUtc = new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 19), Ist.DateOf(lateUtc));
    }

    [Fact]
    public void Weeks_start_on_monday()
    {
        Assert.Equal(new DateOnly(2026, 9, 14), Ist.WeekStart(Ist.At(new DateOnly(2026, 9, 20), new TimeOnly(23, 0))));
        Assert.Equal(new DateOnly(2026, 9, 21), Ist.WeekStart(Ist.At(new DateOnly(2026, 9, 21), new TimeOnly(0, 1))));
    }
}
