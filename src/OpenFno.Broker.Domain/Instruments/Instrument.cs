namespace OpenFno.Broker.Domain.Instruments;

/// <summary>
/// One tradable (or quotable) instrument, keyed by its exchange-prefixed ticker
/// such as <c>NSE:NIFTY26SEP25000CE</c> or <c>NSE:SBIN-EQ</c>.
/// </summary>
public sealed record Instrument
{
    public required string Symbol { get; init; }
    public required Exchange Exchange { get; init; }
    public required Segment Segment { get; init; }
    public required InstrumentKind Kind { get; init; }

    /// <summary>The underlying's name for a derivative (NIFTY, RELIANCE, CRUDEOIL); the stock's own name for equity.</summary>
    public string Underlying { get; init; } = string.Empty;

    public UnderlyingClass UnderlyingClass { get; init; }

    /// <summary>The exchange's numeric token for the contract.</summary>
    public string? ExchangeToken { get; init; }

    /// <summary>
    /// Units per lot. Zero when no master states it; such an instrument is refused
    /// rather than sized wrong (a commodity lot of 1 instead of 100 would make
    /// every rupee figure a hundredth of the truth).
    /// </summary>
    public int LotSize { get; init; }

    public decimal TickSize { get; init; }

    /// <summary>
    /// The exchange's quantity freeze for one order, or null when none applies.
    /// Treated as the smallest quantity refused, which is the conservative reading.
    /// </summary>
    public int? FreezeQuantity { get; init; }

    public DateOnly? Expiry { get; init; }
    public decimal? Strike { get; init; }
    public OptionRight? Right { get; init; }

    public TradingSession Session { get; init; } = TradingSession.NseCash;

    public bool IsDerivative => Kind is InstrumentKind.Future or InstrumentKind.Option;
    public bool IsTradable => Kind is not InstrumentKind.Index;
}
