using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Tests.Support;
using static OpenFno.Broker.Tests.Support.EngineHarness;

namespace OpenFno.Broker.Tests.Engine;

public class OrderFlowTests
{
    [Fact]
    public async Task A_valid_order_blocks_margin_goes_to_the_exchange_and_rests_in_the_book()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);

        var placed = await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m), tag: "t1");

        Assert.True(placed.IsSuccess);
        Assert.Equal("26091800000001", placed.Value.OrderId);
        Assert.Equal(OrderStatus.Transit, placed.Value.Status);
        Assert.Equal(1_600m, placed.Value.BlockedMargin);
        Assert.Equal("99999", placed.Value.AlgoId);
        Assert.Equal(48_400m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);

        Assert.Equal(1, h.Scheduler.Pending);
        await h.Scheduler.RunAllAsync();

        var order = (await h.Engine.GetOrderAsync(account.ClientId, placed.Value.OrderId)).Value;
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(["placed", "accepted"], order.History!.Select(e => e.Event));
    }

    [Fact]
    public async Task An_order_the_funds_cannot_cover_is_recorded_as_rejected_with_the_reason()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 10_000m);

        var result = await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Rejected, result.Value.Status);
        Assert.Equal(ErrorCodes.InsufficientFunds, result.Value.RejectionCode);
        Assert.Equal(0, h.Scheduler.Pending);
        Assert.Equal(10_000m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);
        Assert.Single((await h.Engine.GetOrdersAsync(account.ClientId, null)).Value);
    }

    [Fact]
    public async Task An_invalid_request_creates_no_order_and_writes_nothing()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var before = h.Journal.Count;

        var result = await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 70, 25_000m, ProductType.Nrml));

        Assert.Equal(ErrorCodes.LotSizeMultiple, result.Error?.Code);
        Assert.Equal(ErrorKind.Invalid, result.Error?.Kind);
        Assert.Equal(before, h.Journal.Count);
        Assert.Empty((await h.Engine.GetOrdersAsync(account.ClientId, null)).Value);
    }

    [Fact]
    public async Task An_unknown_symbol_is_an_input_error()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var result = await h.PlaceAsync(account, "NSE:NOPE-EQ", Limit(OrderSide.Buy, 1, 10m));
        Assert.Equal(ErrorCodes.UnknownSymbol, result.Error?.Code);
    }

    [Fact]
    public async Task Cancelling_releases_the_margin_and_a_second_cancel_is_refused_and_recorded()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 50_000m);
        var placed = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();

        var cancelled = await h.Engine.CancelOrderAsync(account.ClientId, placed.OrderId);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Value.Status);
        Assert.Equal(50_000m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);

        var again = await h.Engine.CancelOrderAsync(account.ClientId, placed.OrderId);
        Assert.Equal(ErrorCodes.OrderNotModifiable, again.Error?.Code);
        var history = (await h.Engine.GetOrderAsync(account.ClientId, placed.OrderId)).Value.History!;
        Assert.Equal("cancel_rejected", history[^1].Event);
    }

    [Fact]
    public async Task An_order_in_transit_cannot_be_changed_yet()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var placed = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;

        var modify = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, placed.OrderId, null, null, null, 801m, null));
        var cancel = await h.Engine.CancelOrderAsync(account.ClientId, placed.OrderId);

        Assert.Equal(ErrorCodes.OrderInTransit, modify.Error?.Code);
        Assert.Equal(ErrorCodes.OrderInTransit, cancel.Error?.Code);
    }

    [Fact]
    public async Task Another_clients_order_is_not_found()
    {
        var h = await StartAsync();
        var owner = await h.AccountAsync();
        var other = await h.AccountAsync();
        var placed = (await h.PlaceAsync(owner, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();

        Assert.Equal(ErrorCodes.OrderNotFound, (await h.Engine.CancelOrderAsync(other.ClientId, placed.OrderId)).Error?.Code);
        Assert.Equal(ErrorCodes.OrderNotFound, (await h.Engine.GetOrderAsync(other.ClientId, placed.OrderId)).Error?.Code);
    }

    [Fact]
    public async Task Modifying_reprices_the_margin_and_can_turn_a_limit_into_a_stop()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        h.Quote("NSE:NIFTY26SEPFUT", 25_000m);
        var placed = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 24_900m, ProductType.Nrml))).Value;
        await h.Scheduler.RunAllAsync();

        var bigger = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, placed.OrderId, 130, null, null, null, null));
        Assert.Equal(130, bigger.Value.Quantity);
        Assert.Equal(388_440m, bigger.Value.BlockedMargin); // 24,900 x 130 x 12%

        var stop = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(
            account.ClientId, placed.OrderId, null, OrderType.StopLimit, null, 25_110m, 25_100m));
        Assert.Equal(OrderStatus.TriggerPending, stop.Value.Status);
        Assert.Equal(2, stop.Value.Modifications);

        var back = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(
            account.ClientId, placed.OrderId, null, OrderType.Limit, null, 24_950m, null));
        Assert.Equal(OrderStatus.Open, back.Value.Status);
        Assert.Null(back.Value.TriggerPrice);
    }

    [Fact]
    public async Task A_modification_the_funds_cannot_cover_leaves_the_order_as_it_was()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 200_000m);
        var placed = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml))).Value;
        await h.Scheduler.RunAllAsync();

        var result = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, placed.OrderId, 130, null, null, null, null));

        Assert.Equal(ErrorCodes.InsufficientFunds, result.Error?.Code);
        var order = (await h.Engine.GetOrderAsync(account.ClientId, placed.OrderId)).Value;
        Assert.Equal(65, order.Quantity);
        Assert.Equal(195_000m, order.BlockedMargin);
        Assert.Equal("modify_rejected", order.History![^1].Event);
    }

    [Fact]
    public async Task A_modification_that_changes_nothing_is_refused_without_a_record()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var placed = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();
        var before = h.Journal.Count;

        var result = await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, placed.OrderId, 10, null, null, 800m, null));

        Assert.Equal(ErrorCodes.InvalidRequest, result.Error?.Code);
        Assert.Equal(before, h.Journal.Count);
    }

    [Fact]
    public async Task An_ioc_order_with_nothing_to_trade_against_is_cancelled_on_acknowledgement()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        var intent = new OrderIntent(OrderSide.Buy, 65, OrderType.Limit, ProductType.Nrml, Validity.Ioc, 25_000m, null);
        var placed = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", intent)).Value;

        await h.Scheduler.RunAllAsync();

        var order = (await h.Engine.GetOrderAsync(account.ClientId, placed.OrderId)).Value;
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0m, order.BlockedMargin);
        Assert.Equal(500_000m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);
    }

    [Fact]
    public async Task Day_orders_expire_when_their_session_closes()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        var equity = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        var crude = (await h.PlaceAsync(account, "MCX:CRUDEOIL26SEPFUT", Limit(OrderSide.Buy, 100, 6_000m, ProductType.Nrml))).Value;
        await h.Scheduler.RunAllAsync();

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(15, 30));
        Assert.Equal(1, await h.Engine.ExpireOrdersAsync());
        Assert.Equal(OrderStatus.Expired, (await h.Engine.GetOrderAsync(account.ClientId, equity.OrderId)).Value.Status);
        Assert.Equal(OrderStatus.Open, (await h.Engine.GetOrderAsync(account.ClientId, crude.OrderId)).Value.Status);

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(23, 30));
        Assert.Equal(1, await h.Engine.ExpireOrdersAsync());
        Assert.Equal(500_000m, (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available);
        Assert.Equal(0, await h.Engine.ExpireOrdersAsync());
    }

    [Fact]
    public async Task Order_numbers_restart_each_trading_day()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        Assert.Equal("26091800000001", (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value.OrderId);
        Assert.Equal("26091800000002", (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value.OrderId);

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 21), new TimeOnly(10, 0));
        Assert.Equal("26092100000001", (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value.OrderId);
        Assert.Single((await h.Engine.GetOrdersAsync(account.ClientId, null)).Value);
        Assert.Equal(2, (await h.Engine.GetOrdersAsync(account.ClientId, new DateOnly(2026, 9, 18))).Value.Count);
    }

    [Fact]
    public async Task The_overview_counts_today_and_measures_the_exchange_acknowledgement()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 10_000m);
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m));
        await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml));
        h.Clock.Advance(TimeSpan.FromMilliseconds(35));
        await h.Scheduler.RunAllAsync();

        var overview = (await h.Engine.GetOverviewAsync()).Value;
        Assert.Equal(1, overview.OrdersToday[OrderStatus.Open]);
        Assert.Equal(1, overview.OrdersToday[OrderStatus.Rejected]);
        Assert.Equal(1, overview.RejectionsToday[ErrorCodes.InsufficientFunds]);
        Assert.Equal(35, overview.ExchangeAck?.P50Ms);
        Assert.Equal(1, overview.LiveOrders);
    }
}
