namespace OpenFno.Broker.Domain.Market;

/// <summary>The latest market picture for one symbol, as the simulated exchange last saw it.</summary>
public sealed record Quote
{
    public required string Symbol { get; init; }
    public required decimal LastPrice { get; init; }

    /// <summary>When the exchange stamped the tick (falls back to receipt time when the feed has no stamp).</summary>
    public required DateTimeOffset At { get; init; }

    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public long? BidQuantity { get; init; }
    public long? AskQuantity { get; init; }
    public decimal? PreviousClose { get; init; }
}
