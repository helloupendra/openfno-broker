using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Domain.Orders;

/// <summary>
/// An order as the broker accepted it for checking: who sent it, from where,
/// and exactly what it asked for. Recorded once, in the event that creates the
/// order, and never changed afterwards; modifications are separate events.
/// </summary>
public sealed record OrderTicket
{
    public required string OrderId { get; init; }
    public required string ClientId { get; init; }
    public required string AppId { get; init; }
    public required string Symbol { get; init; }
    public required Exchange Exchange { get; init; }
    public required Segment Segment { get; init; }
    public required OrderSide Side { get; init; }
    public required int Quantity { get; init; }
    public required OrderType Type { get; init; }
    public required ProductType Product { get; init; }
    public required Validity Validity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? TriggerPrice { get; init; }

    /// <summary>The lot size when the order was placed, so the order still reads correctly after a lot-size revision.</summary>
    public required int LotSize { get; init; }

    /// <summary>The exchange algo identifier the broker tags the order with (99999 for unregistered client algos).</summary>
    public required string AlgoId { get; init; }

    /// <summary>The client's own label for the order, echoed back in the order book.</summary>
    public string? Tag { get; init; }

    public required DateOnly TradingDate { get; init; }
    public string? ClientIp { get; init; }

    public OrderIntent ToIntent() => new(Side, Quantity, Type, Product, Validity, LimitPrice, TriggerPrice);
}
