using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Domain.Trading;
using OpenFno.Broker.Tests.Support;
using static OpenFno.Broker.Tests.Support.EngineHarness;

namespace OpenFno.Broker.Tests.Engine;

public class MatchingTests
{
    private static void Book(EngineHarness h, string symbol, decimal last, decimal? bid = null, decimal? ask = null, long? size = null)
        => h.Quotes.Update(new Quote
        {
            Symbol = symbol, LastPrice = last, At = h.Clock.UtcNow, Bid = bid, Ask = ask, BidQuantity = size, AskQuantity = size,
        });

    [Fact]
    public async Task Nothing_waits_on_a_symbol_without_working_orders()
    {
        var h = await StartAsync();
        Assert.False(h.Engine.HasLiveOrders("NSE:SBIN-EQ"));
    }

    [Fact]
    public async Task A_resting_buy_fills_at_its_price_when_the_market_comes_to_it()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        Book(h, "NSE:SBIN-EQ", 801m, 800.95m, 801.05m, 1_000);
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();
        Assert.True(h.Engine.HasLiveOrders("NSE:SBIN-EQ"));
        Assert.Equal(OrderStatus.Open, (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value.Status);

        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Book(h, "NSE:SBIN-EQ", 799.9m, 799.85m, 799.95m, 1_000);
        Assert.Equal(1, await h.Engine.MatchSymbolAsync("NSE:SBIN-EQ"));

        var filled = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.Filled, filled.Status);
        Assert.Equal(800m, filled.AveragePrice);
        Assert.Equal(0m, filled.BlockedMargin);
        Assert.False(h.Engine.HasLiveOrders("NSE:SBIN-EQ"));

        var trade = Assert.Single((await h.Engine.GetTradesAsync(account.ClientId, null)).Value);
        Assert.True(trade.Maker);
        Assert.Equal(10, trade.Quantity);

        var position = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal(10, position.Quantity);
        Assert.Equal(800m, position.AveragePrice);
        Assert.Equal(1_600m, position.Margin);        // intraday: 20% of 8,000
        Assert.Equal(-1m, position.Unrealised);       // marked at 799.90

        var funds = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        Assert.Equal(trade.Charges.Total, funds.ChargesToday);
        Assert.Equal(50_000m - trade.Charges.Total - 1_600m - 1m, funds.Available);
    }

    [Fact]
    public async Task An_arriving_order_takes_what_the_book_shows_and_rests_for_the_rest()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        Book(h, "NSE:SBIN-EQ", 800m, 799.95m, 800.05m, size: 4);

        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800.5m))).Value;
        await h.Scheduler.RunAllAsync();

        var partial = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.PartiallyFilled, partial.Status);
        Assert.Equal(4, partial.FilledQuantity);
        Assert.Equal(800.05m, partial.AveragePrice);           // took the ask, not its limit
        Assert.Equal(960.6m, partial.BlockedMargin);           // 6 left x 800.50 x 20%

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Book(h, "NSE:SBIN-EQ", 800.1m, 800.05m, 800.15m, size: 100);
        await h.Engine.MatchSymbolAsync("NSE:SBIN-EQ");

        var done = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.Filled, done.Status);
        Assert.Equal(2, (await h.Engine.GetTradesAsync(account.ClientId, null)).Value.Count);
        Assert.Equal(20m, done.History!.Count(e => e.Event is "filled" or "partially_filled") * 10m);
    }

    [Fact]
    public async Task An_ioc_order_fills_what_it_can_and_cancels_the_rest()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        Book(h, "NSE:SBIN-EQ", 800m, 799.95m, 800.05m, size: 3);

        var intent = new OrderIntent(OrderSide.Buy, 10, OrderType.Limit, ProductType.Mis, Validity.Ioc, 800.1m, null);
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", intent)).Value;
        await h.Scheduler.RunAllAsync();

        var done = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.Cancelled, done.Status);
        Assert.Equal(3, done.FilledQuantity);
        Assert.Equal(0m, done.BlockedMargin);
    }

    [Fact]
    public async Task A_stop_triggers_on_the_last_trade_and_then_fills()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        Book(h, "NSE:NIFTY26SEPFUT", 25_000m, 24_999.9m, 25_000.1m, 650);
        var stop = new OrderIntent(OrderSide.Sell, 65, OrderType.StopLimit, ProductType.Nrml, Validity.Day, 24_890m, 24_900m);
        var order = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", stop)).Value;
        await h.Scheduler.RunAllAsync();
        Assert.Equal(OrderStatus.TriggerPending, (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value.Status);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Book(h, "NSE:NIFTY26SEPFUT", 24_950m, 24_949.9m, 24_950.1m, 650);
        await h.Engine.MatchSymbolAsync("NSE:NIFTY26SEPFUT");
        Assert.Equal(OrderStatus.TriggerPending, (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value.Status);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Book(h, "NSE:NIFTY26SEPFUT", 24_900m, 24_899.9m, 24_900.1m, 650);
        await h.Engine.MatchSymbolAsync("NSE:NIFTY26SEPFUT");

        var done = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.Filled, done.Status);
        Assert.Equal(24_899.9m, done.AveragePrice); // triggered, then took the bid
        Assert.Equal(["placed", "accepted", "triggered", "filled"], done.History!.Select(e => e.Event));
    }

    [Fact]
    public async Task Nothing_matches_while_the_market_is_closed_or_the_quote_is_stale()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        Book(h, "NSE:SBIN-EQ", 801m);
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();

        Book(h, "NSE:SBIN-EQ", 799m);
        h.Clock.Advance(TimeSpan.FromMinutes(10)); // the quote is now 10 minutes old
        Assert.Equal(0, await h.Engine.MatchSymbolAsync("NSE:SBIN-EQ"));

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 29, 59));
        Book(h, "NSE:SBIN-EQ", 799m);
        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 30, 1));
        Assert.Equal(0, await h.Engine.MatchSymbolAsync("NSE:SBIN-EQ"));
        Assert.Equal(OrderStatus.Open, (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value.Status);
    }

    [Fact]
    public async Task A_paused_feed_matches_nothing()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        Book(h, "NSE:SBIN-EQ", 799m, 798.95m, 799.05m, 100);
        h.Chaos.FeedPaused = true;
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();
        Assert.Equal(OrderStatus.Open, (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value.Status);
    }
}

public class ExposureTests
{
    private static async Task<(EngineHarness H, TestAccount Account)> LongNiftyAsync(decimal funds = 400_000m)
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds);
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEPFUT", LastPrice = 25_000m, At = h.Clock.UtcNow, Bid = 24_999.9m, Ask = 25_000m, AskQuantity = 650, BidQuantity = 650 });
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));
        await h.Scheduler.RunAllAsync();
        return (h, account);
    }

    [Fact]
    public async Task An_order_that_closes_a_position_needs_no_margin_and_books_the_result()
    {
        var (h, account) = await LongNiftyAsync();
        var position = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal(65, position.Quantity);
        Assert.Equal(195_000m, position.Margin);

        var exit = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Sell, 65, 25_100m, ProductType.Nrml))).Value;
        Assert.Equal(0m, exit.BlockedMargin);
        await h.Scheduler.RunAllAsync();

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEPFUT", LastPrice = 25_100.1m, At = h.Clock.UtcNow });
        await h.Engine.MatchSymbolAsync("NSE:NIFTY26SEPFUT");

        var flat = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal(0, flat.Quantity);
        Assert.Equal(6_500m, flat.RealisedToday);   // 65 x 100
        Assert.Equal(0m, flat.Margin);
        var funds = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        Assert.Equal(6_500m, funds.RealisedToday);
        Assert.Equal(0m, funds.PositionMargin);
    }

    [Fact]
    public async Task A_second_exit_order_is_margined_because_the_first_already_closes_the_position()
    {
        var (h, account) = await LongNiftyAsync(funds: 600_000m);
        var first = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Sell, 65, 25_200m, ProductType.Nrml))).Value;
        var second = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Sell, 65, 25_200m, ProductType.Nrml))).Value;
        Assert.Equal(0m, first.BlockedMargin);
        Assert.Equal(196_560m, second.BlockedMargin);   // it would open a short: 25,200 x 65 x 12%
    }

    [Fact]
    public async Task Exits_are_allowed_after_the_intraday_cutoff_and_new_positions_are_not()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Ask = 800m, AskQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m));
        await h.Scheduler.RunAllAsync();

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 16));
        var exit = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 10, 801m))).Value;
        var entry = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 799m))).Value;
        Assert.Equal(OrderStatus.Transit, exit.Status);
        Assert.Equal(ErrorCodes.IntradayCutoff, entry.RejectionCode);
    }

    [Fact]
    public async Task Shares_bought_for_delivery_today_can_be_sold_today_but_no_more()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Ask = 800m, Bid = 799.95m, AskQuantity = 100, BidQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m, ProductType.Cnc));
        await h.Scheduler.RunAllAsync();

        var tooMany = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 11, 805m, ProductType.Cnc))).Value;
        var ten = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 10, 805m, ProductType.Cnc))).Value;
        var more = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 1, 805m, ProductType.Cnc))).Value;

        Assert.Equal(ErrorCodes.NoHoldings, tooMany.RejectionCode);
        Assert.Equal(OrderStatus.Transit, ten.Status);
        Assert.Equal(ErrorCodes.NoHoldings, more.RejectionCode);   // the ten are already on their way out
    }
}

public class RmsTests
{
    [Fact]
    public async Task The_kill_switch_cancels_everything_refuses_new_orders_and_only_the_back_office_can_lift_it_early()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEPFUT", LastPrice = 25_000m, At = h.Clock.UtcNow, Bid = 24_999.9m, Ask = 25_000m, AskQuantity = 650, BidQuantity = 650 });
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));
        var resting = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 700m))).Value;
        await h.Scheduler.RunAllAsync();

        var on = await h.Engine.SetKillSwitchAsync(account.ClientId, true, "client", squareOff: true);
        Assert.True(on.Value.Active);
        Assert.Equal(Ist.At(new DateOnly(2026, 9, 19), new TimeOnly(6, 0)), on.Value.Until);

        Assert.Equal(OrderStatus.Cancelled, (await h.Engine.GetOrderAsync(account.ClientId, resting.OrderId)).Value.Status);
        Assert.Equal(0, Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value).Quantity);
        var closing = (await h.Engine.GetOrdersAsync(account.ClientId, null)).Value.First(o => o.Tag == SystemOrders.KillSwitchTag);
        Assert.Equal(OrderStatus.Filled, closing.Status);
        Assert.Equal(24_999.9m, closing.AveragePrice); // sold at the bid

        var refused = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value;
        Assert.Equal(ErrorCodes.KillSwitchActive, refused.RejectionCode);

        Assert.Equal(ErrorCodes.KillSwitchActive, (await h.Engine.SetKillSwitchAsync(account.ClientId, false, "client", false)).Error?.Code);
        Assert.False((await h.Engine.SetKillSwitchAsync(account.ClientId, false, "admin", false)).Value.Active);
        Assert.Equal(OrderStatus.Transit, (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value.Status);
    }

    [Fact]
    public async Task Intraday_positions_are_squared_off_at_the_market_with_the_brokers_fee()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Bid = 799.95m, Ask = 800m, AskQuantity = 100, BidQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m));
        var working = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 700m))).Value;
        await h.Scheduler.RunAllAsync();

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 19));
        Assert.Equal(0, await h.Engine.SquareOffIntradayAsync());

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 20));
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 810m, At = h.Clock.UtcNow, Bid = 809.95m, Ask = 810m });
        Assert.Equal(2, await h.Engine.SquareOffIntradayAsync());

        Assert.Equal(OrderStatus.Cancelled, (await h.Engine.GetOrderAsync(account.ClientId, working.OrderId)).Value.Status);
        var position = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal(0, position.Quantity);
        Assert.Equal(99.5m, position.RealisedToday);   // 10 x (809.95 - 800)

        var squareOff = (await h.Engine.GetTradesAsync(account.ClientId, null)).Value.First();
        Assert.Equal(50m, squareOff.Charges.Other);
        Assert.Equal(0, await h.Engine.SquareOffIntradayAsync());
    }

    [Fact]
    public async Task A_chaos_exchange_rejection_releases_the_margin()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Chaos.ExchangeRejectPercent = 100;
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();

        var rejected = (await h.Engine.GetOrderAsync(account.ClientId, order.OrderId)).Value;
        Assert.Equal(OrderStatus.Rejected, rejected.Status);
        Assert.Equal(ErrorCodes.ExchangeRejected, rejected.RejectionCode);
        Assert.Equal(50_000m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);
    }
}

public class SettlementTests
{
    [Fact]
    public async Task The_day_close_moves_delivery_to_holdings_closes_intraday_marks_futures_and_posts_the_ledger()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        void Quote(string symbol, decimal last) => h.Quotes.Update(new Quote
        {
            Symbol = symbol, LastPrice = last, At = h.Clock.UtcNow, Bid = last - 0.1m, Ask = last, AskQuantity = 1_000, BidQuantity = 1_000,
        });
        Quote("NSE:SBIN-EQ", 800m);
        Quote("NSE:NIFTY26SEPFUT", 25_000m);

        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m, ProductType.Cnc));
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 5, 800m, ProductType.Mis));
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));
        var working = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 700m))).Value;
        await h.Scheduler.RunAllAsync();

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(16, 0));
        Quote("NSE:SBIN-EQ", 805m);
        Quote("NSE:NIFTY26SEPFUT", 25_050m);
        var charges = (await h.Engine.GetFundsAsync(account.ClientId)).Value.ChargesToday;

        var report = (await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 18))).Value;
        Assert.Equal(1, report.OrdersExpired);
        Assert.Equal(1, report.AccountsSettled);
        Assert.Equal(OrderStatus.Expired, (await h.Engine.GetOrderAsync(account.ClientId, working.OrderId)).Value.Status);

        var holding = Assert.Single((await h.Engine.GetHoldingsAsync(account.ClientId)).Value);
        Assert.Equal(10, holding.Quantity);
        Assert.Equal(800m, holding.AveragePrice);

        var future = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal("NSE:NIFTY26SEPFUT", future.Symbol);
        Assert.Equal(25_050m, future.AveragePrice);   // carried at the settlement price

        var funds = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        var kinds = funds.Ledger.Select(l => l.Kind).ToList();
        Assert.Equal(["PAY_IN", "DELIVERY_BUY", "REALISED_PNL", "CHARGES"], kinds);
        // Intraday SBIN closed at 805 (+25) and the future marked 50 up (+3,250).
        Assert.Equal(3_275m, funds.Ledger.Single(l => l.Kind == "REALISED_PNL").Amount);
        Assert.Equal(-8_000m, funds.Ledger.Single(l => l.Kind == "DELIVERY_BUY").Amount);
        Assert.Equal(500_000m - 8_000m + 3_275m - charges, funds.Cash);
        Assert.Equal(0m, funds.RealisedToday);
        Assert.Equal(0m, funds.ChargesToday);

        var overview = (await h.Engine.GetOverviewAsync()).Value;
        Assert.Equal(new DateOnly(2026, 9, 18), overview.LastClosedDate);
        Assert.Null(await h.Engine.CloseTradingDayIfDueAsync());
    }

    [Fact]
    public async Task Holdings_can_be_sold_the_next_day_and_the_proceeds_settle_to_cash()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Ask = 800m, Bid = 799.95m, AskQuantity = 100, BidQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m, ProductType.Cnc));
        await h.Scheduler.RunAllAsync();
        await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 18));

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 21), new TimeOnly(10, 0));
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 820m, At = h.Clock.UtcNow, Ask = 820.05m, Bid = 820m, AskQuantity = 100, BidQuantity = 100 });
        var sell = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 10, 820m, ProductType.Cnc))).Value;
        await h.Scheduler.RunAllAsync();
        Assert.Equal(OrderStatus.Filled, (await h.Engine.GetOrderAsync(account.ClientId, sell.OrderId)).Value.Status);

        await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 21));
        Assert.Empty((await h.Engine.GetHoldingsAsync(account.ClientId)).Value);
        var funds = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        Assert.Equal(8_200m, funds.Ledger.Single(l => l.Kind == "DELIVERY_SELL").Amount);
        Assert.Equal(0m, funds.PositionMargin);
    }

    [Fact]
    public async Task An_expiring_option_settles_at_its_intrinsic_value_when_it_has_no_last_price()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEP25000CE", LastPrice = 100m, At = h.Clock.UtcNow, Ask = 100m, AskQuantity = 650 });
        await h.PlaceAsync(account, "NSE:NIFTY26SEP25000CE", Limit(OrderSide.Buy, 65, 100m, ProductType.Nrml));
        await h.Scheduler.RunAllAsync();
        await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 18));
        Assert.Equal(65, Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value).Quantity); // options carry

        // Expiry day: the option has no price, the index has.
        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 29), new TimeOnly(16, 0));
        h.Quotes.Remove("NSE:NIFTY26SEP25000CE");
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY50-INDEX", LastPrice = 25_120m, At = h.Clock.UtcNow });
        await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 29));

        var funds = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        Assert.Equal(1_300m, funds.Ledger.Last(l => l.Kind == "REALISED_PNL").Amount); // 65 x (120 - 100)
        Assert.Empty((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        Assert.Equal(0m, funds.PositionMargin);
    }
}

public class TradingReplayTests
{
    [Fact]
    public async Task Fills_positions_holdings_and_settlements_survive_a_restart()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Bid = 799.95m, Ask = 800m, AskQuantity = 100, BidQuantity = 100 });
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEPFUT", LastPrice = 25_000m, At = h.Clock.UtcNow, Bid = 24_999.9m, Ask = 25_000m, AskQuantity = 650, BidQuantity = 650 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m, ProductType.Cnc));
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));
        await h.Scheduler.RunAllAsync();
        await h.Engine.CloseTradingDayAsync(new DateOnly(2026, 9, 18));
        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 21), new TimeOnly(10, 0));
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Sell, 65, 24_999.9m, ProductType.Nrml));
        h.Quotes.Update(new Quote { Symbol = "NSE:NIFTY26SEPFUT", LastPrice = 25_000m, At = h.Clock.UtcNow, Bid = 24_999.9m, Ask = 25_000m, AskQuantity = 650, BidQuantity = 650 });
        await h.Scheduler.RunAllAsync();

        var before = (await h.Engine.GetFundsAsync(account.ClientId)).Value;
        var restarted = await h.RestartAsync();
        var after = (await restarted.GetFundsAsync(account.ClientId)).Value;

        Assert.Equal(before with { Ledger = [] }, after with { Ledger = [] });
        Assert.Equal(before.Ledger, after.Ledger);
        Assert.Equal(
            (await h.Engine.GetHoldingsAsync(account.ClientId)).Value,
            (await restarted.GetHoldingsAsync(account.ClientId)).Value);
        Assert.Equal(
            (await h.Engine.GetTradesAsync(account.ClientId, new DateOnly(2026, 9, 18))).Value.Select(t => t.TradeId),
            (await restarted.GetTradesAsync(account.ClientId, new DateOnly(2026, 9, 18))).Value.Select(t => t.TradeId));
    }
}

public class NetProfitTests
{
    /// <summary>
    /// The owner's BANKNIFTY 57700 PE trade of September 2026: 120 bought at 570
    /// on the 3rd, sold at 1,670 on the 11th. ₹1,32,000 gross; the charges are
    /// worked by hand in the commit that added this test.
    /// </summary>
    [Fact]
    public void An_option_trade_nets_its_charges_on_both_legs()
    {
        var put = Fixtures.NiftyCall with { Symbol = "NSE:BANKNIFTY26SEP57700PE", Underlying = "BANKNIFTY", LotSize = 30, Strike = 57_700m, Right = OpenFno.Broker.Domain.Instruments.OptionRight.Put };
        var brokerage = new BrokerageRule(20m, 0.03m);

        var buy = ChargeSchedule.For(put, ProductType.Nrml, OrderSide.Buy, 570m * 120, brokerage.ForFill(570m * 120, 0m), new DateOnly(2026, 9, 3));
        var sell = ChargeSchedule.For(put, ProductType.Nrml, OrderSide.Sell, 1_670m * 120, brokerage.ForFill(1_670m * 120, 0m), new DateOnly(2026, 9, 11));
        var gross = PositionMath.Apply(120, 570m, OrderSide.Sell, 120, 1_670m).Realised;

        Assert.Equal(54.01m, buy.Total);
        Assert.Equal(300.6m, sell.TransactionTax);
        Assert.Equal(407.27m, sell.Total);
        Assert.Equal(132_000m, gross);
        Assert.Equal(131_538.72m, gross - buy.Total - sell.Total);
    }

    [Fact]
    public async Task A_position_shows_its_profit_after_charges()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 800m, At = h.Clock.UtcNow, Bid = 799.95m, Ask = 800m, AskQuantity = 100, BidQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m));
        await h.Scheduler.RunAllAsync();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Quotes.Update(new Quote { Symbol = "NSE:SBIN-EQ", LastPrice = 810m, At = h.Clock.UtcNow, Bid = 810m, Ask = 810.05m, AskQuantity = 100, BidQuantity = 100 });
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 10, 810m));
        await h.Scheduler.RunAllAsync();

        var position = Assert.Single((await h.Engine.GetPositionsAsync(account.ClientId)).Value);
        var charges = (await h.Engine.GetTradesAsync(account.ClientId, null)).Value.Sum(t => t.Charges.Total);
        Assert.Equal(100m, position.RealisedToday);
        Assert.Equal(charges, position.ChargesToday);
        Assert.Equal(100m - charges, position.NetToday);
    }
}
