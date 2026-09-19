using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Risk;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Application.Engine;

public sealed partial class BrokerEngine
{
    /// <summary>
    /// Places an order. An invalid request is refused with no order created; an
    /// order that fails the risk checks is created as REJECTED; one that passes
    /// is sent to the exchange (TRANSIT), acknowledged after the simulated
    /// exchange latency, and matched against the market from then on.
    /// </summary>
    public Task<Result<OrderView>> PlaceOrderAsync(PlaceOrderCommand command, CommandTiming? timing = null, CancellationToken cancellationToken = default)
        => RunAsync<OrderView>(now =>
        {
            if (!_state.Accounts.TryGetValue(command.ClientId, out var account)) return AccountMissing(command.ClientId);
            var profile = account.Profile;

            var instrument = _instruments.Find(command.Symbol);
            if (instrument is null)
                return BrokerError.Invalid(ErrorCodes.UnknownSymbol,
                    $"No instrument '{command.Symbol}'. Symbols look like NSE:SBIN-EQ or NSE:NIFTY26SEP25000CE.");

            var intent = command.Intent;
            var quote = _quotes.Find(instrument.Symbol);
            var invalid = OrderRules.CheckRequest(instrument, intent, command.Tag, profile, _calendar, quote, now,
                enforceMarketHours: !_options.AlwaysOpen);
            if (invalid is not null) return BrokerError.Invalid(invalid);

            if (Margin.PriceFor(intent, quote?.LastPrice) is not { } price)
                return BrokerError.Invalid(ErrorCodes.InvalidPrice,
                    $"There is no last price for {instrument.Symbol} to margin a market-priced order against.");

            var tradingDate = Ist.DateOf(now);
            var ticket = new OrderTicket
            {
                OrderId = _state.NextOrderId(tradingDate),
                ClientId = command.ClientId,
                AppId = command.AppId,
                Symbol = instrument.Symbol,
                Exchange = instrument.Exchange,
                Segment = instrument.Segment,
                Side = intent.Side,
                Quantity = intent.Quantity,
                Type = intent.Type,
                Product = intent.Product,
                Validity = intent.Validity,
                LimitPrice = intent.LimitPrice,
                TriggerPrice = intent.TriggerPrice,
                LotSize = instrument.LotSize,
                AlgoId = profile.AlgoId,
                Tag = command.Tag,
                TradingDate = tradingDate,
                ClientIp = command.ClientIp,
            };

            Rejection? refusal = account.KillSwitchActive(now)
                ? new Rejection(ErrorCodes.KillSwitchActive,
                    $"The kill switch is on until {Ist.ToIst(account.KillSwitch!.Until ?? now):yyyy-MM-dd HH:mm} IST; no new orders are accepted.")
                : null;

            var increasing = IncreasingQuantity(account, instrument.Symbol, intent.Product, intent.Side, intent.Quantity, null);
            var margin = OrderMargin(account, instrument, intent, price, null);
            refusal ??= OrderRules.CheckRisk(instrument, intent, profile, quote, now, margin, Available(account),
                holdings: intent.Product == ProductType.Cnc && intent.Side == OrderSide.Sell
                    ? SellableShares(account, instrument.Symbol, null)
                    : 0,
                isNewOrder: increasing > 0);

            if (refusal is not null)
            {
                return Outcome<OrderView>.Of(
                    new OrderRejected { ClientId = command.ClientId, Ticket = ticket, Code = refusal.Code, Message = refusal.Message },
                    state => OrderView.From(state.Orders[ticket.OrderId], withHistory: false));
            }

            var ackDelay = _options.NextAckDelay() + TimeSpan.FromMilliseconds(_chaos.ExtraAckLatencyMs);
            return Outcome<OrderView>.Of(
                new OrderPlaced { ClientId = command.ClientId, Ticket = ticket, BlockedMargin = margin },
                state => OrderView.From(state.Orders[ticket.OrderId], withHistory: false),
                () => ScheduleAck(ticket.OrderId, ackDelay));
        }, timing, cancellationToken);

    public Task<Result<OrderView>> ModifyOrderAsync(ModifyOrderCommand command, CommandTiming? timing = null, CancellationToken cancellationToken = default)
        => RunAsync<OrderView>(now =>
        {
            if (FindOrder(command.ClientId, command.OrderId) is not { } order) return OrderMissing(command.OrderId);
            var account = _state.Account(command.ClientId);
            var profile = account.Profile;

            if (AmendBlocker(order, "modify", profile) is { } blocked) return AmendRefused(order, "modify", blocked, ErrorKind.Conflict);
            if (account.KillSwitchActive(now))
                return AmendRefused(order, "modify",
                    new Rejection(ErrorCodes.KillSwitchActive, "The kill switch is on; orders can be cancelled but not modified."),
                    ErrorKind.Conflict);

            var type = command.Type ?? order.Type;
            var isStop = type is OrderType.StopLimit or OrderType.StopMarket;
            var isMarketPriced = type is OrderType.Market or OrderType.StopMarket;
            var merged = order.Intent with
            {
                Quantity = command.Quantity ?? order.Quantity,
                Type = type,
                Validity = command.Validity ?? order.Validity,
                LimitPrice = isMarketPriced ? null : command.LimitPrice ?? order.LimitPrice,
                TriggerPrice = isStop ? command.TriggerPrice ?? order.TriggerPrice : null,
            };
            if (merged == order.Intent)
                return BrokerError.Invalid(ErrorCodes.InvalidRequest, "The modification changes nothing.");

            var instrument = _instruments.Find(order.Ticket.Symbol);
            if (instrument is null)
                return AmendRefused(order, "modify",
                    new Rejection(ErrorCodes.UnknownSymbol, $"{order.Ticket.Symbol} is no longer in the instrument master."),
                    ErrorKind.Conflict);

            // The trigger is checked against the market only while the order will still be waiting for it;
            // a stop that has already triggered rests as a limit order.
            var waitsForTrigger = isStop && (order.Status == OrderStatus.TriggerPending || !order.Intent.IsStop);
            var quote = _quotes.Find(instrument.Symbol);
            var invalid = (_options.AlwaysOpen ? null : OrderRules.CheckMarketOpen(instrument, _calendar, now))
                          ?? OrderRules.CheckShape(instrument, merged, profile)
                          ?? OrderRules.CheckPrices(instrument, merged, waitsForTrigger || !isStop ? quote : null);
            if (invalid is null && merged.Quantity <= order.FilledQuantity)
                invalid = new Rejection(ErrorCodes.InvalidQuantity,
                    $"Quantity must stay above the {order.FilledQuantity} already filled.");
            if (invalid is null && order.FilledQuantity > 0 && type != order.Type)
                invalid = new Rejection(ErrorCodes.InvalidRequest, "A partly filled order keeps its order type.");
            if (invalid is not null) return AmendRefused(order, "modify", invalid, ErrorKind.Invalid);

            if (Margin.PriceFor(merged, quote?.LastPrice) is not { } price)
                return AmendRefused(order, "modify",
                    new Rejection(ErrorCodes.InvalidPrice, $"There is no last price for {instrument.Symbol} to margin a market-priced order against."),
                    ErrorKind.Invalid);

            var unfilled = merged with { Quantity = merged.Quantity - order.FilledQuantity };
            var margin = OrderMargin(account, instrument, unfilled, price, order.OrderId);
            var refusal = OrderRules.CheckRisk(instrument, merged, profile, quote, now, margin,
                Available(account) + order.BlockedMargin,
                holdings: merged.Product == ProductType.Cnc && merged.Side == OrderSide.Sell
                    ? SellableShares(account, instrument.Symbol, order.OrderId) + order.FilledQuantity
                    : 0,
                isNewOrder: false);
            if (refusal is not null) return AmendRefused(order, "modify", refusal, ErrorKind.Conflict);

            var status = order.Status switch
            {
                OrderStatus.Open when waitsForTrigger => OrderStatus.TriggerPending,
                OrderStatus.TriggerPending when !isStop => OrderStatus.Open,
                var unchanged => unchanged,
            };

            var orderId = order.OrderId;
            return Outcome<OrderView>.Of(
                [
                    new OrderModified
                    {
                        ClientId = command.ClientId,
                        OrderId = orderId,
                        Quantity = merged.Quantity,
                        Type = merged.Type,
                        Validity = merged.Validity,
                        LimitPrice = merged.LimitPrice,
                        TriggerPrice = merged.TriggerPrice,
                        BlockedMargin = margin,
                        Status = status,
                    },
                ],
                state => OrderView.From(state.Orders[orderId], withHistory: false),
                // A modified order meets the market afresh: a new price may now cross the spread.
                () => _scheduler.Schedule(TimeSpan.Zero, token => MatchOrderAsync(orderId, arriving: true, token)));
        }, timing, cancellationToken);

    public Task<Result<OrderView>> CancelOrderAsync(string clientId, string orderId, CommandTiming? timing = null, CancellationToken cancellationToken = default)
        => RunAsync<OrderView>(_ =>
        {
            if (FindOrder(clientId, orderId) is not { } order) return OrderMissing(orderId);
            if (AmendBlocker(order, "cancel", _state.Account(clientId).Profile) is { } blocked)
                return AmendRefused(order, "cancel", blocked, ErrorKind.Conflict);

            return Outcome<OrderView>.Of(
                new OrderCancelled { ClientId = clientId, OrderId = order.OrderId, Reason = "Cancelled by the client." },
                state => OrderView.From(state.Orders[order.OrderId], withHistory: false));
        }, timing, cancellationToken);

    /// <summary>
    /// The simulated exchange's acknowledgement of an order in transit, and the
    /// order's first look at the market: it may fill at once, trigger, or (IOC)
    /// cancel whatever cannot fill immediately.
    /// </summary>
    public async Task AcknowledgeAsync(string orderId, CancellationToken cancellationToken = default)
    {
        await RunAsync<bool>(now =>
        {
            if (!_state.Orders.TryGetValue(orderId, out var order) || order.Status != OrderStatus.Transit)
                return Outcome<bool>.Nothing(false);

            var clientId = order.Ticket.ClientId;
            if (!order.IsSystem && _chaos.Roll(_chaos.ExchangeRejectPercent))
                return Outcome<bool>.Of(
                    new OrderExchangeRejected
                    {
                        ClientId = clientId,
                        OrderId = orderId,
                        Code = ErrorCodes.ExchangeRejected,
                        Message = "The exchange rejected the order (simulated fault, chaos mode).",
                    },
                    _ => true);

            var status = order.Intent.IsStop ? OrderStatus.TriggerPending : OrderStatus.Open;
            var events = new List<BrokerEvent>
            {
                new OrderAccepted { ClientId = clientId, OrderId = orderId, Status = status },
            };

            if (_state.Account(clientId).KillSwitchActive(now))
                events.Add(new OrderCancelled { ClientId = clientId, OrderId = orderId, Reason = "Kill switch: the order arrived after it was turned on." });
            else
                events.AddRange(PlanMatch(order, status, now, arriving: true));

            return Outcome<bool>.Of(events, _ => true);
        }, null, cancellationToken);
    }

    /// <summary>Expires every day order whose session has closed. Run periodically.</summary>
    /// <returns>How many orders expired.</returns>
    public async Task<int> ExpireOrdersAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync<int>(now =>
        {
            var today = Ist.DateOf(now);
            var events = new List<BrokerEvent>();
            foreach (var orderId in _state.LiveOrderIds)
            {
                var order = _state.Orders[orderId];
                // An order still in transit is acknowledged first and expires on the next pass.
                if (OrderLifecycle.IsWorking(order.Status) && SessionOver(order, now, today))
                    events.Add(new OrderExpired { ClientId = order.Ticket.ClientId, OrderId = orderId });
            }
            return Outcome<int>.Of(events, _ => events.Count);
        }, null, cancellationToken);
        return result.IsSuccess ? result.Value : 0;
    }

    private bool SessionOver(OrderState order, DateTimeOffset now, DateOnly today)
    {
        if (order.Ticket.TradingDate < today) return true;
        if (_options.AlwaysOpen) return false;
        var instrument = _instruments.Find(order.Ticket.Symbol);
        return instrument is not null
               && _calendar.SessionEnd(instrument, order.Ticket.TradingDate) is { } end
               && now >= end;
    }

    private void ScheduleAck(string orderId, TimeSpan delay)
        => _scheduler.Schedule(delay, token => AcknowledgeAsync(orderId, token));

    private OrderState? FindOrder(string clientId, string orderId)
        => _state.Orders.TryGetValue(orderId, out var order) && order.Ticket.ClientId == clientId ? order : null;

    private static BrokerError OrderMissing(string orderId)
        => BrokerError.NotFound(ErrorCodes.OrderNotFound, $"No order {orderId} on this account.");

    /// <summary>Why an order cannot be modified or cancelled right now, or null when it can.</summary>
    private static Rejection? AmendBlocker(OrderState order, string action, BrokerProfile profile)
    {
        if (order.Status == OrderStatus.Transit)
            return new(ErrorCodes.OrderInTransit, "The order is still on its way to the exchange; try again in a moment.");
        if (!OrderLifecycle.IsWorking(order.Status))
            return new(ErrorCodes.OrderNotModifiable, $"The order is {order.Status} and can no longer be changed.");
        if (order.IsSystem)
            return new(ErrorCodes.OrderNotModifiable, "The order was placed by the broker's RMS and cannot be changed.");
        if (action == "modify" && profile.MaxModificationsPerOrder is { } max && order.Modifications >= max)
            return new(ErrorCodes.ModificationLimit, $"An order can be modified at most {max} times.");
        return null;
    }

    /// <summary>Refuses a modify or cancel, and records the refusal in the order's history.</summary>
    private static Outcome<OrderView> AmendRefused(OrderState order, string action, Rejection rejection, ErrorKind kind)
        => Outcome<OrderView>.Of(
            new OrderAmendRejected
            {
                ClientId = order.Ticket.ClientId,
                OrderId = order.OrderId,
                Action = action,
                Code = rejection.Code,
                Message = rejection.Message,
            },
            _ => new BrokerError(rejection.Code, rejection.Message, kind));
}
