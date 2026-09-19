using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Domain.Risk;

/// <summary>
/// The margin an order blocks while it works. Option buyers pay the full
/// premium and delivery buyers the full value, as at every Indian broker; the
/// rest is a percentage of notional from the profile's <see cref="MarginRates"/>.
/// </summary>
public static class Margin
{
    public static decimal Required(Instrument instrument, OrderIntent intent, decimal price, MarginRates rates)
    {
        var quantity = (decimal)intent.Quantity;
        var margin = instrument.Kind switch
        {
            InstrumentKind.Equity => intent.Product switch
            {
                // A delivery sell is paid for by the shares; the holdings check covers it.
                ProductType.Cnc => intent.Side == OrderSide.Buy ? price * quantity : 0m,
                _ => price * quantity * rates.EquityIntradayPercent / 100m,
            },
            InstrumentKind.Option when intent.Side == OrderSide.Buy => price * quantity,
            // A short option is margined on the notional of what it is written on;
            // the strike stands in for the underlying's price.
            InstrumentKind.Option => (instrument.Strike ?? price) * quantity * DerivativePercent(instrument, rates) / 100m,
            InstrumentKind.Future => price * quantity * DerivativePercent(instrument, rates) / 100m,
            _ => throw new InvalidOperationException($"{instrument.Symbol} is not tradable."),
        };
        return decimal.Round(margin, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>The price an order is margined at: its limit price, or the last price for a market-priced order.</summary>
    public static decimal? PriceFor(OrderIntent intent, decimal? lastPrice)
        => intent.IsMarketPriced ? lastPrice : intent.LimitPrice;

    private static decimal DerivativePercent(Instrument instrument, MarginRates rates) => instrument.UnderlyingClass switch
    {
        UnderlyingClass.Index => rates.IndexDerivativePercent,
        UnderlyingClass.Commodity => rates.CommodityDerivativePercent,
        _ => rates.StockDerivativePercent,
    };
}
