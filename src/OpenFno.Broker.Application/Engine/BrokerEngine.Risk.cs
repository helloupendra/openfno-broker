using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Risk;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

/// <summary>Money and exposure: what an account can spend, and what an order or position holds back.</summary>
public sealed partial class BrokerEngine
{
    /// <summary>
    /// Cash less the margin held by orders and positions, less any unrealised
    /// loss (an unrealised profit is not spendable until it is booked).
    /// </summary>
    private decimal Available(AccountState account)
        => account.Cash - account.BlockedMargin - account.PositionMargin + Math.Min(0m, Unrealised(account));

    private decimal Unrealised(AccountState account)
        => account.Positions.Values
            .Where(p => p.Quantity != 0)
            .Sum(p => PositionMath.Unrealised(p.Quantity, p.AveragePrice, _quotes.Find(p.Symbol)?.LastPrice ?? p.AveragePrice));

    /// <summary>
    /// How much of an order adds exposure. The part that closes an open position
    /// in the same instrument and product needs no margin, after allowing for
    /// other working orders already closing it.
    /// </summary>
    private int IncreasingQuantity(
        AccountState account, string symbol, ProductType product, OrderSide side, int quantity, string? excludeOrderId, int? positionOverride = null)
    {
        var net = positionOverride ?? _state.PositionOf(account, symbol, product)?.Quantity ?? 0;
        var opposite = side == OrderSide.Buy ? Math.Max(0, -net) : Math.Max(0, net);
        var alreadyClosing = WorkingOrders(account, symbol)
            .Where(o => o.OrderId != excludeOrderId && o.Ticket.Product == product && o.Ticket.Side == side)
            .Sum(o => o.Unfilled);
        var offset = Math.Max(0, opposite - alreadyClosing);
        return Math.Max(0, quantity - offset);
    }

    /// <summary>The margin an order blocks: its increasing part, priced at its limit (or the given price).</summary>
    private decimal OrderMargin(
        AccountState account, Instrument instrument, OrderIntent intent, decimal price, string? excludeOrderId, int? positionOverride = null)
    {
        var increasing = IncreasingQuantity(account, instrument.Symbol, intent.Product, intent.Side, intent.Quantity, excludeOrderId, positionOverride);
        return increasing == 0 ? 0m : Margin.Required(instrument, intent with { Quantity = increasing }, price, account.Profile.Margins);
    }

    /// <summary>The margin an open position holds, priced at its average.</summary>
    private static decimal PositionMargin(AccountState account, Instrument instrument, ProductType product, int quantity, decimal averagePrice)
    {
        if (quantity == 0) return 0m;
        var intent = new OrderIntent(quantity > 0 ? OrderSide.Buy : OrderSide.Sell, Math.Abs(quantity), OrderType.Limit,
            product, Validity.Day, averagePrice, null);
        return Margin.Required(instrument, intent, averagePrice, account.Profile.Margins);
    }

    /// <summary>
    /// Shares a delivery sell may still take: settled holdings, plus shares
    /// bought for delivery today, less delivery sells already made or working.
    /// </summary>
    private int SellableShares(AccountState account, string symbol, string? excludeOrderId)
    {
        var held = account.Holdings.TryGetValue(symbol, out var holding) ? holding.Quantity : 0;
        var today = _state.PositionOf(account, symbol, ProductType.Cnc)?.Quantity ?? 0;
        var working = WorkingOrders(account, symbol)
            .Where(o => o.OrderId != excludeOrderId && o.Ticket.Product == ProductType.Cnc && o.Ticket.Side == OrderSide.Sell)
            .Sum(o => o.Unfilled);
        return held + today - working;
    }

    private IEnumerable<OrderState> WorkingOrders(AccountState account, string symbol)
        => _state.LiveOrdersBySymbol.TryGetValue(symbol, out var ids)
            ? ids.Select(id => _state.Orders[id]).Where(o => o.Ticket.ClientId == account.ClientId)
            : [];
}
