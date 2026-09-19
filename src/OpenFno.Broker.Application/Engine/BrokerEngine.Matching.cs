using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

/// <summary>The simulated exchange: working orders meet the quotes the feed brings.</summary>
public sealed partial class BrokerEngine
{
    /// <summary>
    /// Matches every working order on the symbol against its latest quote. Each
    /// order is its own command, so a fill on one order changes the position
    /// the next one is margined against.
    /// </summary>
    /// <returns>How many orders changed (triggered or filled).</returns>
    public async Task<int> MatchSymbolAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var ids = await ReadAsync<IReadOnlyList<string>>(
            _ => _state.LiveOrdersBySymbol.TryGetValue(symbol, out var set)
                ? Result<IReadOnlyList<string>>.Ok(set.OrderBy(id => id, StringComparer.Ordinal).ToList())
                : Result<IReadOnlyList<string>>.Ok([]),
            cancellationToken);
        if (!ids.IsSuccess) return 0;

        var changed = 0;
        foreach (var orderId in ids.Value)
            if (await MatchOrderAsync(orderId, arriving: false, cancellationToken)) changed++;
        return changed;
    }

    /// <summary>Matches one working order against its symbol's latest quote.</summary>
    public async Task<bool> MatchOrderAsync(string orderId, bool arriving, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync<bool>(now =>
        {
            if (!_state.Orders.TryGetValue(orderId, out var order) || !OrderLifecycle.IsWorking(order.Status))
                return Outcome<bool>.Nothing(false);
            var events = PlanMatch(order, order.Status, now, arriving);
            return events.Count == 0 ? Outcome<bool>.Nothing(false) : Outcome<bool>.Of(events, _ => true);
        }, null, cancellationToken);
        return result.IsSuccess && result.Value;
    }

    /// <summary>
    /// What the exchange does with one order against the current quote: trigger
    /// a stop, fill (in part), and for an IOC order cancel what could not fill
    /// on arrival. At most one fill, so each fill is decided on fresh state.
    /// </summary>
    private List<BrokerEvent> PlanMatch(OrderState order, OrderStatus status, DateTimeOffset now, bool arriving)
    {
        var events = new List<BrokerEvent>();
        var clientId = order.Ticket.ClientId;
        var instrument = _instruments.Find(order.Ticket.Symbol);
        if (instrument is null) return events;

        var marketOpen = _options.AlwaysOpen || _calendar.IsOpen(instrument, now);
        var quote = _chaos.FeedPaused ? null : _quotes.Find(instrument.Symbol);
        if (quote is not null && now - quote.At > TimeSpan.FromSeconds(_options.MaxQuoteAgeSeconds)) quote = null;

        if (status == OrderStatus.TriggerPending)
        {
            if (!marketOpen || quote is null || order.TriggerPrice is not { } trigger || !Matcher.Triggers(order.Ticket.Side, trigger, quote.LastPrice))
                return events;
            events.Add(new OrderTriggered { ClientId = clientId, OrderId = order.OrderId, LastPrice = quote.LastPrice });
            arriving = true; // a triggered stop enters the book now
        }

        var limit = order.Type is OrderType.Market or OrderType.StopMarket ? null : order.LimitPrice;
        MatchFill? fill = marketOpen && quote is not null
            ? Matcher.Match(order.Ticket.Side, limit, order.Unfilled, quote, arriving, _options.FillOnTouch)
            : null;
        if (fill is { } f)
            events.Add(PlanFill(order, instrument, f, now));

        var left = order.Unfilled - (fill?.Quantity ?? 0);
        if (arriving && left > 0 && (order.Validity == Validity.Ioc || limit is null))
            events.Add(new OrderCancelled
            {
                ClientId = clientId,
                OrderId = order.OrderId,
                Reason = order.Validity == Validity.Ioc
                    ? $"IOC: {left} could not trade immediately and {(left == order.Unfilled ? "the order is" : "the rest is")} cancelled."
                    : $"No price to fill a market order against; {left} cancelled.",
            });

        return events;
    }

    private OrderFilled PlanFill(OrderState order, Instrument instrument, MatchFill fill, DateTimeOffset now)
        => PlanFill(
            _state.Account(order.Ticket.ClientId), instrument, order.Ticket, order.OrderId, order.Intent, order.Unfilled,
            order.FilledValue, order.BrokerageCharged, order.FilledQuantity == 0, order.IsSystem, fill);

    /// <summary>
    /// Works out everything one fill changes: the position, the margin the order
    /// and the position hold afterwards, the profit booked, and the charges.
    /// </summary>
    private OrderFilled PlanFill(
        AccountState account,
        Instrument instrument,
        OrderTicket ticket,
        string orderId,
        OrderIntent intent,
        int unfilledBefore,
        decimal filledValueBefore,
        decimal brokerageBefore,
        bool firstFill,
        bool isSystem,
        MatchFill fill)
    {
        var product = ticket.Product;
        var position = _state.PositionOf(account, ticket.Symbol, product);
        var change = PositionMath.Apply(position?.Quantity ?? 0, position?.AveragePrice ?? 0m, ticket.Side, fill.Quantity, fill.Price);
        var positionMargin = PositionMargin(account, instrument, product, change.Quantity, change.AveragePrice);

        var remaining = unfilledBefore - fill.Quantity;
        var orderMarginAfter = remaining == 0
            ? 0m
            : OrderMargin(account, instrument, intent with { Quantity = remaining }, intent.LimitPrice ?? fill.Price, orderId, change.Quantity);

        var turnover = fill.Quantity * fill.Price;
        var brokerage = account.Profile.Brokerage.ForFill(filledValueBefore + turnover, brokerageBefore);
        var charges = ChargeSchedule.For(instrument, product, ticket.Side, turnover, brokerage, ticket.TradingDate);
        if (isSystem && firstFill && ticket.Tag == SystemOrders.SquareOffTag && account.Profile.AutoSquareOffCharge > 0)
            charges = charges.Add(ChargeSchedule.Fee(account.Profile.AutoSquareOffCharge));

        return new OrderFilled
        {
            ClientId = ticket.ClientId,
            OrderId = orderId,
            TradeId = _state.NextTradeId(ticket.TradingDate),
            Quantity = fill.Quantity,
            Price = fill.Price,
            Maker = fill.Maker,
            Charges = charges,
            OrderMarginAfter = orderMarginAfter,
            Realised = change.Realised,
            PositionAfter = new PositionSnapshot(change.Quantity, change.AveragePrice, positionMargin),
        };
    }
}
