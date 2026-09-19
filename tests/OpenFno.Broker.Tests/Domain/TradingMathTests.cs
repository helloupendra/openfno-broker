using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Trading;
using OpenFno.Broker.Tests.Support;

namespace OpenFno.Broker.Tests.Domain;

public class ChargeTests
{
    private static readonly DateOnly Sept2026 = new(2026, 9, 18);
    private static readonly BrokerageRule Fyers = new(20m, 0.03m);

    [Fact]
    public void A_futures_buy_pays_stamp_duty_and_no_transaction_tax()
    {
        // 65 x 25,000 = 16,25,000 turnover.
        var charges = ChargeSchedule.For(Fixtures.NiftyFuture, ProductType.Nrml, OrderSide.Buy, 1_625_000m, 20m, Sept2026);

        Assert.Equal(20m, charges.Brokerage);
        Assert.Equal(0m, charges.TransactionTax);
        Assert.Equal(28.11m, charges.ExchangeFee);   // 0.00173%
        Assert.Equal(1.63m, charges.SebiFee);        // ₹10 per crore
        Assert.Equal(32.5m, charges.StampDuty);      // 0.002% on the buy side
        Assert.Equal(8.95m, charges.Gst);            // 18% of brokerage + exchange + SEBI
        Assert.Equal(91.19m, charges.Total);
    }

    [Fact]
    public void A_futures_sell_pays_stt_at_the_rate_in_force_on_the_trading_day()
    {
        var after = ChargeSchedule.For(Fixtures.NiftyFuture, ProductType.Nrml, OrderSide.Sell, 1_625_000m, 20m, Sept2026);
        var before = ChargeSchedule.For(Fixtures.NiftyFuture, ProductType.Nrml, OrderSide.Sell, 1_625_000m, 20m, new DateOnly(2026, 3, 31));

        Assert.Equal(812.5m, after.TransactionTax);  // 0.05% from 1 April 2026
        Assert.Equal(325m, before.TransactionTax);   // 0.02% before it
        Assert.Equal(0m, after.StampDuty);
    }

    [Fact]
    public void Option_stt_is_on_the_sell_side_premium()
    {
        var sell = ChargeSchedule.For(Fixtures.NiftyCall, ProductType.Nrml, OrderSide.Sell, 6_500m, 0m, Sept2026);
        var buy = ChargeSchedule.For(Fixtures.NiftyCall, ProductType.Nrml, OrderSide.Buy, 6_500m, 0m, Sept2026);
        Assert.Equal(9.75m, sell.TransactionTax);    // 0.15%
        Assert.Equal(0m, buy.TransactionTax);
        Assert.Equal(0.2m, buy.StampDuty);           // 0.003%
        Assert.Equal(ChargeCategory.EquityOptions, ChargeSchedule.CategoryOf(Fixtures.NiftyCall, ProductType.Mis));
    }

    [Fact]
    public void Delivery_pays_stt_on_both_sides_and_intraday_only_on_the_sale()
    {
        var deliveryBuy = ChargeSchedule.For(Fixtures.Sbin, ProductType.Cnc, OrderSide.Buy, 80_000m, 0m, Sept2026);
        var intradayBuy = ChargeSchedule.For(Fixtures.Sbin, ProductType.Mis, OrderSide.Buy, 80_000m, 0m, Sept2026);
        var intradaySell = ChargeSchedule.For(Fixtures.Sbin, ProductType.Mis, OrderSide.Sell, 80_000m, 0m, Sept2026);
        Assert.Equal(80m, deliveryBuy.TransactionTax);   // 0.1%
        Assert.Equal(12m, deliveryBuy.StampDuty);        // 0.015%
        Assert.Equal(0m, intradayBuy.TransactionTax);
        Assert.Equal(20m, intradaySell.TransactionTax);  // 0.025%
    }

    [Fact]
    public void Commodities_pay_ctt_and_the_mcx_fee()
    {
        var sell = ChargeSchedule.For(Fixtures.CrudeFuture, ProductType.Nrml, OrderSide.Sell, 600_000m, 0m, Sept2026);
        Assert.Equal(ChargeCategory.CommodityFutures, ChargeSchedule.CategoryOf(Fixtures.CrudeFuture, ProductType.Nrml));
        Assert.Equal(60m, sell.TransactionTax);   // 0.01%
        Assert.Equal(12.6m, sell.ExchangeFee);    // 0.0021%
    }

    [Fact]
    public void Brokerage_is_capped_once_per_order_across_its_fills()
    {
        Assert.Equal(1.5m, Fyers.ForFill(orderTurnoverSoFar: 5_000m, chargedSoFar: 0m));       // 0.03% is below ₹20
        Assert.Equal(18.5m, Fyers.ForFill(orderTurnoverSoFar: 1_000_000m, chargedSoFar: 1.5m)); // up to the ₹20 cap
        Assert.Equal(0m, Fyers.ForFill(orderTurnoverSoFar: 2_000_000m, chargedSoFar: 20m));     // already paid
    }

    [Fact]
    public void A_fee_carries_gst()
    {
        var fee = ChargeSchedule.Fee(50m);
        Assert.Equal(50m, fee.Other);
        Assert.Equal(9m, fee.Gst);
        Assert.Equal(59m, fee.Total);
    }
}

public class PositionMathTests
{
    [Fact]
    public void Buying_into_a_long_averages_the_price()
    {
        var change = PositionMath.Apply(65, 100m, OrderSide.Buy, 65, 110m);
        Assert.Equal(new PositionChange(130, 105m, 0m), change);
    }

    [Fact]
    public void Selling_part_of_a_long_books_the_difference_and_keeps_the_average()
    {
        var change = PositionMath.Apply(65, 100m, OrderSide.Sell, 30, 110m);
        Assert.Equal(new PositionChange(35, 100m, 300m), change);
    }

    [Fact]
    public void Selling_through_a_long_closes_it_and_opens_a_short_at_the_fill_price()
    {
        var change = PositionMath.Apply(35, 100m, OrderSide.Sell, 50, 120m);
        Assert.Equal(new PositionChange(-15, 120m, 700m), change);
    }

    [Fact]
    public void Buying_back_a_short_below_its_price_is_a_profit()
    {
        var change = PositionMath.Apply(-65, 100m, OrderSide.Buy, 65, 90m);
        Assert.Equal(new PositionChange(0, 0m, 650m), change);
    }

    [Fact]
    public void Unrealised_follows_the_sign_of_the_position()
    {
        Assert.Equal(650m, PositionMath.Unrealised(65, 100m, 110m));
        Assert.Equal(-650m, PositionMath.Unrealised(-65, 100m, 110m));
    }
}

public class MatcherTests
{
    private static Quote At(decimal last, decimal? bid = null, decimal? ask = null, long? bidSize = null, long? askSize = null) => new()
    {
        Symbol = "X",
        LastPrice = last,
        At = Fixtures.TradingMorning,
        Bid = bid,
        Ask = ask,
        BidQuantity = bidSize,
        AskQuantity = askSize,
    };

    [Fact]
    public void An_arriving_buy_that_crosses_takes_the_ask_up_to_the_size_shown()
    {
        var fill = Matcher.Match(OrderSide.Buy, 101m, 100, At(100m, 99.9m, 100.1m, askSize: 40), arriving: true, fillOnTouch: false);
        Assert.Equal(new MatchFill(40, 100.1m, Maker: false), fill);
    }

    [Fact]
    public void An_arriving_order_that_does_not_cross_waits()
        => Assert.Null(Matcher.Match(OrderSide.Buy, 100m, 10, At(100.2m, 100.1m, 100.3m), arriving: true, fillOnTouch: false));

    [Fact]
    public void A_resting_buy_fills_at_its_own_price_when_the_ask_comes_down_to_it()
    {
        var fill = Matcher.Match(OrderSide.Buy, 100m, 10, At(99.9m, 99.8m, 99.95m, askSize: 500), arriving: false, fillOnTouch: false);
        Assert.Equal(new MatchFill(10, 100m, Maker: true), fill);
    }

    [Fact]
    public void A_resting_order_fills_when_the_market_trades_through_it_but_not_on_a_touch()
    {
        Assert.Equal(new MatchFill(10, 100m, Maker: true),
            Matcher.Match(OrderSide.Buy, 100m, 10, At(99.95m), arriving: false, fillOnTouch: false));
        Assert.Null(Matcher.Match(OrderSide.Buy, 100m, 10, At(100m), arriving: false, fillOnTouch: false));
        Assert.NotNull(Matcher.Match(OrderSide.Buy, 100m, 10, At(100m), arriving: false, fillOnTouch: true));
    }

    [Fact]
    public void Sells_mirror_buys()
    {
        Assert.Equal(new MatchFill(10, 100.2m, Maker: false),
            Matcher.Match(OrderSide.Sell, 100m, 10, At(100.2m, 100.2m, 100.3m), arriving: true, fillOnTouch: false));
        Assert.Equal(new MatchFill(10, 100m, Maker: true),
            Matcher.Match(OrderSide.Sell, 100m, 10, At(100.05m), arriving: false, fillOnTouch: false));
        Assert.Null(Matcher.Match(OrderSide.Sell, 100m, 10, At(99.95m), arriving: false, fillOnTouch: false));
    }

    [Fact]
    public void A_market_order_takes_the_opposite_side_or_the_last_trade()
    {
        Assert.Equal(new MatchFill(10, 100.3m, Maker: false), Matcher.Match(OrderSide.Buy, null, 10, At(100.2m, 100.1m, 100.3m), true, false));
        Assert.Equal(new MatchFill(10, 100.2m, Maker: false), Matcher.Match(OrderSide.Buy, null, 10, At(100.2m), true, false));
    }

    [Theory]
    [InlineData(OrderSide.Buy, "100", "100", true)]
    [InlineData(OrderSide.Buy, "100", "99.95", false)]
    [InlineData(OrderSide.Sell, "100", "99.95", true)]
    [InlineData(OrderSide.Sell, "100", "100.05", false)]
    public void Stops_trigger_when_the_last_trade_reaches_them(OrderSide side, string trigger, string last, bool expected)
        => Assert.Equal(expected, Matcher.Triggers(side, decimal.Parse(trigger), decimal.Parse(last)));
}
