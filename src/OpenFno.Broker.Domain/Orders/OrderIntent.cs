namespace OpenFno.Broker.Domain.Orders;

/// <summary>What an order asks for: the part of it the broker's rules judge.</summary>
public sealed record OrderIntent(
    OrderSide Side,
    int Quantity,
    OrderType Type,
    ProductType Product,
    Validity Validity,
    decimal? LimitPrice,
    decimal? TriggerPrice)
{
    public bool IsStop => Type is OrderType.StopLimit or OrderType.StopMarket;
    public bool IsMarketPriced => Type is OrderType.Market or OrderType.StopMarket;
}
