using System.Collections.Concurrent;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Domain.Limits;

/// <summary>The outcome of asking for one request's worth of rate.</summary>
public readonly record struct RateDecision(bool Allowed, string? Limit, TimeSpan RetryAfter, bool DayBlocked)
{
    public static readonly RateDecision Allow = new(true, null, TimeSpan.Zero, false);
}

/// <summary>
/// Fixed-window request counters per client: calendar second, calendar minute
/// and IST day, measured on this server's clock as the exchanges specify. A
/// request that is refused is not counted. Hitting the per-minute limit in more
/// minutes than the policy tolerates blocks the client until the next IST day.
/// </summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Counters> _clients = new(StringComparer.Ordinal);

    public RateDecision TryAcquire(string client, RateLimitPolicy policy, bool isOrderOperation, DateTimeOffset now)
    {
        var counters = _clients.GetOrAdd(client, _ => new Counters());
        lock (counters)
        {
            counters.Roll(now);

            if (counters.BlockedUntil is { } blockedUntil && blockedUntil > now)
                return new RateDecision(false, "day_block", blockedUntil - now, true);

            var nextSecond = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() + 1);
            if (isOrderOperation && counters.OrderOpsThisSecond >= policy.OrderOpsPerSecond)
                return new RateDecision(false, "order_ops_per_second", nextSecond - now, false);
            if (counters.ThisSecond >= policy.RequestsPerSecond)
                return new RateDecision(false, "requests_per_second", nextSecond - now, false);

            if (counters.ThisMinute >= policy.RequestsPerMinute)
            {
                if (!counters.MinuteBreached)
                {
                    counters.MinuteBreached = true;
                    counters.MinuteBreachesToday++;
                    if (counters.MinuteBreachesToday > policy.MinuteBreachesAllowedPerDay)
                    {
                        var midnight = Ist.At(Ist.DateOf(now).AddDays(1), TimeOnly.MinValue);
                        counters.BlockedUntil = midnight;
                        return new RateDecision(false, "day_block", midnight - now, true);
                    }
                }
                var nextMinute = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 60 + 1) * 60);
                return new RateDecision(false, "requests_per_minute", nextMinute - now, false);
            }

            if (counters.Today >= policy.RequestsPerDay)
            {
                var midnight = Ist.At(Ist.DateOf(now).AddDays(1), TimeOnly.MinValue);
                return new RateDecision(false, "requests_per_day", midnight - now, false);
            }

            counters.ThisSecond++;
            counters.ThisMinute++;
            counters.Today++;
            if (isOrderOperation) counters.OrderOpsThisSecond++;
            return RateDecision.Allow;
        }
    }

    private sealed class Counters
    {
        private long _second = long.MinValue;
        private long _minute = long.MinValue;
        private DateOnly _day;

        public int ThisSecond;
        public int OrderOpsThisSecond;
        public int ThisMinute;
        public bool MinuteBreached;
        public int Today;
        public int MinuteBreachesToday;
        public DateTimeOffset? BlockedUntil;

        public void Roll(DateTimeOffset now)
        {
            var second = now.ToUnixTimeSeconds();
            if (second != _second)
            {
                _second = second;
                ThisSecond = 0;
                OrderOpsThisSecond = 0;
            }

            var minute = second / 60;
            if (minute != _minute)
            {
                _minute = minute;
                ThisMinute = 0;
                MinuteBreached = false;
            }

            var day = Ist.DateOf(now);
            if (day != _day)
            {
                _day = day;
                Today = 0;
                MinuteBreachesToday = 0;
            }
        }
    }
}
