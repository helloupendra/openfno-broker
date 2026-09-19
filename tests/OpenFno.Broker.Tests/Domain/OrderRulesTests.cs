using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Risk;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Tests.Support;

namespace OpenFno.Broker.Tests.Domain;

public class OrderRulesTests
{
    private static readonly BrokerProfile Fyers = BrokerProfiles.Fyers;
    private static readonly ExchangeCalendar Calendar = Fixtures.Calendar();
    private static readonly DateTimeOffset Now = Fixtures.TradingMorning;

    private static OrderIntent Limit(int quantity, decimal price, ProductType product = ProductType.Nrml, OrderSide side = OrderSide.Buy)
        => new(side, quantity, OrderType.Limit, product, Validity.Day, price, null);

    private static string? Check(Instrument instrument, OrderIntent intent, Quote? quote = null, string? tag = null, DateTimeOffset? at = null)
        => OrderRules.CheckRequest(instrument, intent, tag, Fyers, Calendar, quote, at ?? Now)?.Code;

    private static Quote QuoteOf(Instrument instrument, decimal last, decimal? previousClose = null)
        => new() { Symbol = instrument.Symbol, LastPrice = last, At = Now, PreviousClose = previousClose };

    [Fact]
    public void A_plain_limit_order_passes() => Assert.Null(Check(Fixtures.NiftyFuture, Limit(65, 25_000.1m)));

    [Fact]
    public void An_index_cannot_be_traded()
        => Assert.Equal(ErrorCodes.InstrumentNotTradable, Check(Fixtures.NiftyIndex, Limit(1, 25_000m, ProductType.Mis)));

    [Fact]
    public void An_expired_contract_cannot_be_traded()
        => Assert.Equal(ErrorCodes.InstrumentExpired, Check(Fixtures.ExpiredCall, Limit(65, 10m)));

    [Fact]
    public void A_contract_without_a_known_lot_size_is_refused_rather_than_sized_wrong()
        => Assert.Equal(ErrorCodes.InstrumentNotConfigured,
            Check(Fixtures.CrudeFuture with { LotSize = 0 }, Limit(100, 6_000m)));

    [Fact]
    public void Outside_market_hours_the_order_is_refused_with_the_hours()
    {
        var evening = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(16, 0));
        var rejection = OrderRules.CheckRequest(Fixtures.Sbin, Limit(1, 800m, ProductType.Cnc), null, Fyers, Calendar, null, evening);
        Assert.Equal(ErrorCodes.MarketClosed, rejection?.Code);
        Assert.Contains("09:15-15:30", rejection!.Message);
    }

    [Fact]
    public void Market_hours_can_be_ignored_for_a_sandbox()
    {
        var evening = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(16, 0));
        Assert.Null(OrderRules.CheckRequest(Fixtures.Sbin, Limit(1, 800m, ProductType.Cnc), null, Fyers, Calendar, null, evening,
            enforceMarketHours: false));
    }

    [Theory]
    [InlineData(0, ErrorCodes.InvalidQuantity)]
    [InlineData(-65, ErrorCodes.InvalidQuantity)]
    [InlineData(70, ErrorCodes.LotSizeMultiple)]
    [InlineData(1755, null)]
    [InlineData(1820, ErrorCodes.FreezeQuantity)]
    public void Quantity_must_be_whole_lots_below_the_freeze(int quantity, string? expected)
        => Assert.Equal(expected, Check(Fixtures.NiftyFuture, Limit(quantity, 25_000m)));

    [Fact]
    public void The_freeze_quantity_itself_is_refused()
        => Assert.Equal(ErrorCodes.FreezeQuantity,
            Check(Fixtures.NiftyFuture with { FreezeQuantity = 1820 }, Limit(1820, 25_000m)));

    [Theory]
    [InlineData(ProductType.Cnc, ErrorCodes.ProductNotAllowed)]
    [InlineData(ProductType.Mis, null)]
    [InlineData(ProductType.Nrml, null)]
    public void Products_follow_the_segment_for_derivatives(ProductType product, string? expected)
        => Assert.Equal(expected, Check(Fixtures.NiftyFuture, Limit(65, 25_000m, product)));

    [Fact]
    public void Nrml_is_not_for_the_cash_segment()
        => Assert.Equal(ErrorCodes.ProductNotAllowed, Check(Fixtures.Sbin, Limit(1, 800m, ProductType.Nrml)));

    [Theory]
    [InlineData(OrderType.Market)]
    [InlineData(OrderType.StopMarket)]
    public void Market_priced_orders_are_refused_for_algo_orders(OrderType type)
    {
        var intent = new OrderIntent(OrderSide.Buy, 65, type, ProductType.Nrml, Validity.Day, null,
            type == OrderType.StopMarket ? 25_100m : null);
        Assert.Equal(ErrorCodes.MarketOrderNotAllowed, Check(Fixtures.NiftyFuture, intent));
    }

    [Fact]
    public void Ioc_is_refused_in_commodities_but_not_in_equity_derivatives()
    {
        var crude = new OrderIntent(OrderSide.Buy, 100, OrderType.Limit, ProductType.Nrml, Validity.Ioc, 6_000m, null);
        var nifty = new OrderIntent(OrderSide.Buy, 65, OrderType.Limit, ProductType.Nrml, Validity.Ioc, 25_000m, null);
        Assert.Equal(ErrorCodes.IocNotAllowed, Check(Fixtures.CrudeFuture, crude));
        Assert.Null(Check(Fixtures.NiftyFuture, nifty));
    }

    [Theory]
    [InlineData("25000.1", null)]
    [InlineData("25000.15", ErrorCodes.TickSizeMultiple)]
    [InlineData("0", ErrorCodes.InvalidPrice)]
    public void Prices_sit_on_the_tick_grid(string price, string? expected)
        => Assert.Equal(expected, Check(Fixtures.NiftyFuture, Limit(65, decimal.Parse(price))));

    [Fact]
    public void A_limit_order_needs_a_price_and_takes_no_trigger()
    {
        Assert.Equal(ErrorCodes.InvalidPrice,
            Check(Fixtures.NiftyFuture, new OrderIntent(OrderSide.Buy, 65, OrderType.Limit, ProductType.Nrml, Validity.Day, null, null)));
        Assert.Equal(ErrorCodes.InvalidTriggerPrice,
            Check(Fixtures.NiftyFuture, new OrderIntent(OrderSide.Buy, 65, OrderType.Limit, ProductType.Nrml, Validity.Day, 25_000m, 24_990m)));
    }

    [Theory]
    [InlineData(OrderSide.Buy, "25100", "25110", "25000", null)]
    [InlineData(OrderSide.Buy, "25120", "25110", "25000", ErrorCodes.InvalidTriggerPrice)] // trigger above limit
    [InlineData(OrderSide.Buy, "24990", "25000", "25000", ErrorCodes.InvalidTriggerPrice)] // already below the market
    [InlineData(OrderSide.Sell, "24900", "24890", "25000", null)]
    [InlineData(OrderSide.Sell, "24880", "24890", "25000", ErrorCodes.InvalidTriggerPrice)] // trigger below limit
    [InlineData(OrderSide.Sell, "25010", "25000", "25000", ErrorCodes.InvalidTriggerPrice)] // already above the market
    public void Stop_limit_triggers_sit_between_the_market_and_the_limit(
        OrderSide side, string trigger, string limit, string last, string? expected)
    {
        var intent = new OrderIntent(side, 65, OrderType.StopLimit, ProductType.Nrml, Validity.Day, decimal.Parse(limit), decimal.Parse(trigger));
        Assert.Equal(expected, Check(Fixtures.NiftyFuture, intent, QuoteOf(Fixtures.NiftyFuture, decimal.Parse(last))));
    }

    [Theory]
    [InlineData("scalp-1", null)]
    [InlineData("a_very_long_tag_over_20", ErrorCodes.InvalidTag)]
    [InlineData("has space", ErrorCodes.InvalidTag)]
    [InlineData("", ErrorCodes.InvalidTag)]
    public void Tags_are_short_and_plain(string tag, string? expected)
        => Assert.Equal(expected, Check(Fixtures.NiftyFuture, Limit(65, 25_000m), tag: tag));

    [Fact]
    public void Risk_refuses_new_intraday_orders_after_the_cutoff_but_not_changes_to_old_ones()
    {
        var late = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 16));
        var intent = Limit(65, 25_000m, ProductType.Mis);
        Assert.Equal(ErrorCodes.IntradayCutoff,
            OrderRules.CheckRisk(Fixtures.NiftyFuture, intent, Fyers, null, late, 1m, 10m, 0)?.Code);
        Assert.Null(OrderRules.CheckRisk(Fixtures.NiftyFuture, intent, Fyers, null, late, 1m, 10m, 0, isNewOrder: false));
    }

    [Fact]
    public void Risk_refuses_a_delivery_sell_without_holdings()
        => Assert.Equal(ErrorCodes.NoHoldings, OrderRules.CheckRisk(
            Fixtures.Sbin, Limit(10, 800m, ProductType.Cnc, OrderSide.Sell), Fyers, null, Now, 0m, 1_000m, holdings: 5)?.Code);

    [Theory]
    [InlineData("959.95", null)]
    [InlineData("960.05", ErrorCodes.PriceBand)]
    [InlineData("640", null)]
    [InlineData("639.95", ErrorCodes.PriceBand)]
    public void Risk_keeps_cash_prices_within_twenty_percent_of_the_previous_close(string price, string? expected)
    {
        var quote = QuoteOf(Fixtures.Sbin, 810m, previousClose: 800m);
        Assert.Equal(expected, OrderRules.CheckRisk(
            Fixtures.Sbin, Limit(1, decimal.Parse(price), ProductType.Mis), Fyers, quote, Now, 1m, 1_000m, 0)?.Code);
    }

    [Fact]
    public void Risk_refuses_an_order_the_funds_do_not_cover_and_says_how_much()
    {
        var rejection = OrderRules.CheckRisk(Fixtures.NiftyFuture, Limit(65, 25_000m), Fyers, null, Now, 195_000m, 191_995m, 0);
        Assert.Equal(ErrorCodes.InsufficientFunds, rejection?.Code);
        Assert.Contains("₹1,95,000.00", rejection!.Message);
        Assert.Contains("₹1,91,995.00", rejection.Message);
    }

    [Theory]
    [InlineData("0", "₹0.00")]
    [InlineData("999.5", "₹999.50")]
    [InlineData("1000", "₹1,000.00")]
    [InlineData("100000", "₹1,00,000.00")]
    [InlineData("12345678.9", "₹1,23,45,678.90")]
    [InlineData("-2500", "-₹2,500.00")]
    public void Money_uses_indian_digit_grouping(string amount, string expected)
        => Assert.Equal(expected, OrderRules.Money(decimal.Parse(amount)));
}

public class MarginTests
{
    private static readonly MarginRates Rates = new();

    private static decimal Of(Instrument instrument, OrderSide side, int quantity, decimal price, ProductType product = ProductType.Nrml)
        => Margin.Required(instrument, new OrderIntent(side, quantity, OrderType.Limit, product, Validity.Day, price, null), price, Rates);

    [Fact]
    public void Delivery_buys_pay_in_full_and_delivery_sells_need_no_margin()
    {
        Assert.Equal(8_000m, Of(Fixtures.Sbin, OrderSide.Buy, 10, 800m, ProductType.Cnc));
        Assert.Equal(0m, Of(Fixtures.Sbin, OrderSide.Sell, 10, 800m, ProductType.Cnc));
    }

    [Fact]
    public void Intraday_equity_is_leveraged_five_times_at_most()
        => Assert.Equal(1_600m, Of(Fixtures.Sbin, OrderSide.Sell, 10, 800m, ProductType.Mis));

    [Fact]
    public void Option_buyers_pay_the_premium_and_sellers_are_margined_on_the_strike()
    {
        Assert.Equal(6_500m, Of(Fixtures.NiftyCall, OrderSide.Buy, 65, 100m));
        Assert.Equal(195_000m, Of(Fixtures.NiftyCall, OrderSide.Sell, 65, 100m)); // 25,000 x 65 x 12%
    }

    [Fact]
    public void Futures_are_margined_on_notional_by_underlying_class()
    {
        Assert.Equal(195_000m, Of(Fixtures.NiftyFuture, OrderSide.Buy, 65, 25_000m));
        Assert.Equal(150_000m, Of(Fixtures.CrudeFuture, OrderSide.Sell, 100, 6_000m)); // 25%
    }
}

public class OrderLifecycleTests
{
    [Theory]
    [InlineData(OrderStatus.Transit, OrderStatus.Open, true)]
    [InlineData(OrderStatus.Transit, OrderStatus.Rejected, true)]
    [InlineData(OrderStatus.Open, OrderStatus.TriggerPending, true)]
    [InlineData(OrderStatus.TriggerPending, OrderStatus.Open, true)]
    [InlineData(OrderStatus.Open, OrderStatus.Expired, true)]
    [InlineData(OrderStatus.Transit, OrderStatus.Expired, false)]
    [InlineData(OrderStatus.Open, OrderStatus.Rejected, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Open, false)]
    [InlineData(OrderStatus.Filled, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Rejected, OrderStatus.Transit, false)]
    public void Only_listed_moves_are_allowed(OrderStatus from, OrderStatus to, bool allowed)
        => Assert.Equal(allowed, OrderLifecycle.CanMove(from, to));

    [Fact]
    public void Final_statuses_go_nowhere()
    {
        foreach (var status in new[] { OrderStatus.Filled, OrderStatus.Cancelled, OrderStatus.Rejected, OrderStatus.Expired })
        {
            Assert.True(OrderLifecycle.IsTerminal(status));
            Assert.False(OrderLifecycle.IsWorking(status));
        }
    }

    [Fact]
    public void An_impossible_move_throws() => Assert.Throws<InvalidOperationException>(
        () => OrderLifecycle.EnsureCanMove("1", OrderStatus.Cancelled, OrderStatus.Filled));
}
