namespace OpenFno.Broker.Application.Engine;

/// <summary>How the simulated exchange behaves.</summary>
public sealed class ExchangeSimulationOptions
{
    public const string Section = "Exchange";

    /// <summary>The least time between the broker sending an order and the exchange acknowledging it.</summary>
    public int AckLatencyMs { get; set; } = 20;

    /// <summary>Up to this much is added at random to each acknowledgement.</summary>
    public int AckJitterMs { get; set; } = 30;

    /// <summary>
    /// Ignore trading hours and holidays: for local development outside market
    /// hours. Day orders then expire when the IST date changes.
    /// </summary>
    public bool AlwaysOpen { get; set; }

    /// <summary>
    /// Fill a resting order when a trade prints exactly at its price. Off by
    /// default: the order's place in the queue is unknown, so the pessimistic
    /// choice is to wait for the market to trade through it.
    /// </summary>
    public bool FillOnTouch { get; set; }

    /// <summary>A quote older than this is not matched against: the market data has stopped.</summary>
    public int MaxQuoteAgeSeconds { get; set; } = 120;

    /// <summary>When the trading day is settled (IST), after the last exchange has closed.</summary>
    public TimeOnly SettlementTime { get; set; } = new(23, 58);

    public TimeSpan NextAckDelay()
        => TimeSpan.FromMilliseconds(Math.Max(0, AckLatencyMs) + (AckJitterMs > 0 ? Random.Shared.Next(AckJitterMs + 1) : 0));
}
