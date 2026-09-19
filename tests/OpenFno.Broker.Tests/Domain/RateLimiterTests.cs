using OpenFno.Broker.Domain.Limits;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Tests.Support;

namespace OpenFno.Broker.Tests.Domain;

public class RateLimiterTests
{
    private static readonly RateLimitPolicy Policy = BrokerProfiles.Fyers.RateLimits;
    private readonly RateLimiter _limiter = new();
    private DateTimeOffset _now = Fixtures.TradingMorning;

    private RateDecision Ask(bool order = false) => _limiter.TryAcquire("OFB00001", Policy, order, _now);

    [Fact]
    public void Ten_order_operations_fit_in_one_second_and_the_eleventh_waits_for_the_next()
    {
        for (var i = 0; i < 10; i++) Assert.True(Ask(order: true).Allowed);

        var refused = Ask(order: true);
        Assert.False(refused.Allowed);
        Assert.Equal("order_ops_per_second", refused.Limit);
        Assert.True(refused.RetryAfter <= TimeSpan.FromSeconds(1));

        _now = _now.AddSeconds(1);
        Assert.True(Ask(order: true).Allowed);
    }

    [Fact]
    public void The_window_is_the_calendar_second_not_a_rolling_one()
    {
        _now = _now.AddMilliseconds(900);
        for (var i = 0; i < 10; i++) Assert.True(Ask(order: true).Allowed);
        _now = _now.AddMilliseconds(200); // next calendar second, 200 ms later
        Assert.True(Ask(order: true).Allowed);
    }

    [Fact]
    public void Refused_requests_do_not_count()
    {
        for (var i = 0; i < 10; i++) Ask(order: true);
        for (var i = 0; i < 5; i++) Assert.False(Ask(order: true).Allowed);
        _now = _now.AddSeconds(1);
        for (var i = 0; i < 10; i++) Assert.True(Ask(order: true).Allowed);
    }

    [Fact]
    public void Clients_are_counted_separately()
    {
        for (var i = 0; i < 10; i++) Ask(order: true);
        Assert.True(_limiter.TryAcquire("OFB00002", Policy, true, _now).Allowed);
    }

    [Fact]
    public void The_minute_limit_applies_across_seconds()
    {
        for (var second = 0; second < 20; second++, _now = _now.AddSeconds(1))
            for (var i = 0; i < 10; i++) Assert.True(Ask().Allowed);

        var refused = Ask();
        Assert.False(refused.Allowed);
        Assert.Equal("requests_per_minute", refused.Limit);
        Assert.False(refused.DayBlocked);
    }

    [Fact]
    public void Breaking_the_minute_limit_in_too_many_minutes_blocks_the_rest_of_the_day()
    {
        // Start each minute on a boundary so every burst lands in one calendar minute.
        _now = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(10, 0));
        for (var minute = 0; minute < Policy.MinuteBreachesAllowedPerDay; minute++)
        {
            FillMinute();
            var refused = Ask();
            Assert.Equal("requests_per_minute", refused.Limit);
            _now = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(10, minute + 1));
        }

        FillMinute();
        var blocked = Ask();
        Assert.True(blocked.DayBlocked);

        _now = _now.AddMinutes(5);
        Assert.True(Ask().DayBlocked);

        _now = Ist.At(new DateOnly(2026, 9, 19), new TimeOnly(0, 0, 1));
        Assert.True(Ask().Allowed);
    }

    private void FillMinute()
    {
        var start = _now;
        for (var second = 0; second < 20; second++)
        {
            _now = start.AddSeconds(second);
            for (var i = 0; i < 10; i++) Assert.True(Ask().Allowed);
        }
        // Still inside the minute, but in a second with room left.
        _now = start.AddSeconds(20);
    }
}
