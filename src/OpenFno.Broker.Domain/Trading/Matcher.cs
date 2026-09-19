using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Domain.Trading;

/// <summary>One fill the simulated exchange would give an order against a quote.</summary>
/// <param name="Maker">True when the order was resting and was filled at its own price.</param>
public readonly record struct MatchFill(int Quantity, decimal Price, bool Maker);

/// <summary>
/// The simulated exchange's matching, against the best bid, best ask and last
/// trade of a quote, since the simulator does not see the full order book.
/// It leans pessimistic, so a strategy's fills here are no better than it
/// would get:
/// <list type="bullet">
/// <item>An arriving order that crosses the spread takes the best opposite
/// price, for no more than the size shown there.</item>
/// <item>A resting order fills at its own price when the opposite side
/// reaches it, or when the market trades through it.</item>
/// <item>A trade exactly at a resting order's price does not fill it (its
/// place in the queue is unknown) unless fill-on-touch is on.</item>
/// </list>
/// </summary>
public static class Matcher
{
    /// <summary>Whether a stop order's trigger has been reached by the last trade.</summary>
    public static bool Triggers(OrderSide side, decimal trigger, decimal lastPrice)
        => side == OrderSide.Buy ? lastPrice >= trigger : lastPrice <= trigger;

    /// <param name="limit">The limit price; null for a market-priced order.</param>
    /// <param name="arriving">True on the order's first look at the market (just acknowledged, triggered or modified).</param>
    public static MatchFill? Match(OrderSide side, decimal? limit, int remaining, Quote quote, bool arriving, bool fillOnTouch)
    {
        if (remaining <= 0) return null;

        var opposite = side == OrderSide.Buy ? quote.Ask : quote.Bid;
        var oppositeSize = side == OrderSide.Buy ? quote.AskQuantity : quote.BidQuantity;

        if (limit is null)
        {
            // A market-priced order takes the opposite side, or the last trade when there is no quote.
            if (!arriving) return null;
            var price = opposite is > 0 ? opposite.Value : quote.LastPrice;
            return new MatchFill(Size(remaining, opposite is > 0 ? oppositeSize : null), price, Maker: false);
        }

        var crosses = opposite is > 0 && (side == OrderSide.Buy ? opposite.Value <= limit : opposite.Value >= limit);
        if (crosses)
        {
            return arriving
                ? new MatchFill(Size(remaining, oppositeSize), opposite!.Value, Maker: false)
                : new MatchFill(Size(remaining, oppositeSize), limit.Value, Maker: true);
        }

        if (arriving) return null;

        var tradedThrough = side == OrderSide.Buy ? quote.LastPrice < limit : quote.LastPrice > limit;
        var touched = quote.LastPrice == limit;
        if (tradedThrough || (touched && fillOnTouch))
            return new MatchFill(remaining, limit.Value, Maker: true);

        return null;
    }

    /// <summary>The quantity that can trade: what is left, capped by the size shown when there is one.</summary>
    private static int Size(int remaining, long? shown)
        => shown is > 0 ? (int)Math.Min(remaining, shown.Value) : remaining;
}
