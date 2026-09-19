using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Domain.Rules;

/// <summary>
/// The limits one Indian broker applies to API clients. Each account trades
/// under one profile, so the simulator can behave like the broker the engine
/// will later use for real. Sources for every value are in docs/rules.md.
/// </summary>
public sealed record BrokerProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    public required RateLimitPolicy RateLimits { get; init; }

    /// <summary>How many whitelisted static IPs one API app may have.</summary>
    public required int MaxStaticIpsPerApp { get; init; }

    /// <summary>How many times an app's static IPs may change in one calendar week (Monday to Sunday, IST).</summary>
    public required int StaticIpChangesPerWeek { get; init; }

    /// <summary>Every session ends at the next occurrence of this IST time, forcing a fresh 2FA login each trading day.</summary>
    public required TimeOnly SessionExpiresAt { get; init; }

    /// <summary>False: MARKET and STOP_MARKET orders are refused (see docs/decisions/0004).</summary>
    public required bool AllowMarketOrders { get; init; }

    /// <summary>False: IOC orders are refused in the commodity segment, as the exchanges require for algo orders.</summary>
    public required bool AllowIocInCommodities { get; init; }

    /// <summary>How many times one order may be modified; null when the broker publishes no limit.</summary>
    public int? MaxModificationsPerOrder { get; init; }

    /// <summary>After this IST time no new intraday (MIS) order is accepted on the exchange.</summary>
    public required IReadOnlyDictionary<Exchange, TimeOnly> IntradayCutoff { get; init; }

    public required MarginRates Margins { get; init; }

    /// <summary>A cash-market limit price further than this from the reference price is refused.</summary>
    public decimal CashPriceBandPercent { get; init; } = 20m;

    /// <summary>The exchange algo ID for orders from an unregistered client algo within the order-rate threshold.</summary>
    public string AlgoId { get; init; } = "99999";
}

/// <summary>Request-rate limits, counted per client on the broker server's clock.</summary>
public sealed record RateLimitPolicy
{
    /// <summary>Place, modify and cancel counted together, per calendar second.</summary>
    public required int OrderOpsPerSecond { get; init; }

    public required int RequestsPerSecond { get; init; }
    public required int RequestsPerMinute { get; init; }
    public required int RequestsPerDay { get; init; }

    /// <summary>Minutes in a day the per-minute limit may be hit; one more and the client is blocked until the next day.</summary>
    public required int MinuteBreachesAllowedPerDay { get; init; }
}

/// <summary>
/// Margin as a share of notional value. A simplification of the exchange's SPAN
/// and exposure margins, which the simulator does not compute yet; the rates are
/// deliberately on the high side so a strategy that fits here fits a real broker.
/// </summary>
public sealed record MarginRates
{
    /// <summary>Intraday equity: SEBI's peak-margin rules cap leverage at 5x, so at least 20%.</summary>
    public decimal EquityIntradayPercent { get; init; } = 20m;

    public decimal IndexDerivativePercent { get; init; } = 12m;
    public decimal StockDerivativePercent { get; init; } = 20m;
    public decimal CommodityDerivativePercent { get; init; } = 25m;
}

public static class BrokerProfiles
{
    /// <summary>FYERS API v3 as it stands after the SEBI retail-algo framework took effect on 1 April 2026.</summary>
    public static readonly BrokerProfile Fyers = new()
    {
        Id = "fyers",
        Name = "FYERS API v3",
        RateLimits = new RateLimitPolicy
        {
            OrderOpsPerSecond = 10,
            RequestsPerSecond = 10,
            RequestsPerMinute = 200,
            RequestsPerDay = 100_000,
            MinuteBreachesAllowedPerDay = 3,
        },
        MaxStaticIpsPerApp = 1,
        StaticIpChangesPerWeek = 1,
        SessionExpiresAt = new TimeOnly(6, 0),
        AllowMarketOrders = false,
        AllowIocInCommodities = false,
        MaxModificationsPerOrder = null,
        IntradayCutoff = new Dictionary<Exchange, TimeOnly>
        {
            [Exchange.Nse] = new(15, 15),
            [Exchange.Bse] = new(15, 15),
            [Exchange.Mcx] = new(23, 0),
        },
        Margins = new MarginRates(),
    };

    public static readonly IReadOnlyDictionary<string, BrokerProfile> All =
        new Dictionary<string, BrokerProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [Fyers.Id] = Fyers,
        };

    public static BrokerProfile? Find(string? id) => id is not null && All.TryGetValue(id, out var profile) ? profile : null;
}
