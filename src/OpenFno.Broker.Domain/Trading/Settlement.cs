using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Domain.Trading;

/// <summary>A position's state after something changed it. Stored in events so a replay never recomputes it.</summary>
public sealed record PositionSnapshot(int Quantity, decimal AveragePrice, decimal Margin);

public enum SettlementKind
{
    /// <summary>A carried futures position marked to the day's settlement price; the difference is paid or received.</summary>
    MarkToMarket,

    /// <summary>A contract that expired today, closed at its settlement price.</summary>
    Expiry,

    /// <summary>Shares bought for delivery today move into holdings; their cost leaves the cash balance.</summary>
    DeliveryIn,

    /// <summary>Shares sold from holdings today leave them; the proceeds reach the cash balance.</summary>
    DeliveryOut,

    /// <summary>An intraday position still open at the day's end, closed at the last price.</summary>
    IntradayClose,
}

/// <summary>
/// One line of an account's end-of-day settlement. <paramref name="CashAmount"/>
/// is what reaches the cash balance directly (delivery); <paramref name="Realised"/>
/// is profit or loss booked by marking or closing the position.
/// </summary>
public sealed record SettlementEntry(
    string Symbol,
    ProductType Product,
    SettlementKind Kind,
    int Quantity,
    decimal Price,
    decimal CashAmount,
    decimal Realised,
    PositionSnapshot PositionAfter);
