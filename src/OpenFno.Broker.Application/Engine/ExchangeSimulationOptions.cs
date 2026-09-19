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

    public TimeSpan NextAckDelay()
        => TimeSpan.FromMilliseconds(Math.Max(0, AckLatencyMs) + (AckJitterMs > 0 ? Random.Shared.Next(AckJitterMs + 1) : 0));
}
