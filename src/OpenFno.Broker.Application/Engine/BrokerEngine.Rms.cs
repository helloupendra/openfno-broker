using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

/// <summary>
/// What the broker's RMS does on its own: close intraday positions at the
/// square-off time, and stop an account's trading when its kill switch is on.
/// </summary>
public sealed partial class BrokerEngine
{
    /// <summary>
    /// Past each exchange's square-off time: cancels working intraday orders
    /// and closes open intraday positions at the market, with the broker's fee.
    /// Safe to run repeatedly; it does nothing once everything is flat.
    /// </summary>
    /// <returns>How many orders were cancelled and positions closed.</returns>
    public async Task<int> SquareOffIntradayAsync(CancellationToken cancellationToken = default)
    {
        var due = await ReadAsync<SquareOffWork>(now =>
        {
            var orders = new List<(string ClientId, string OrderId)>();
            var positions = new List<(string ClientId, string Symbol)>();
            foreach (var account in _state.Accounts.Values)
            {
                foreach (var orderId in _state.LiveOrderIds)
                {
                    var order = _state.Orders[orderId];
                    if (order.Ticket.ClientId == account.ClientId
                        && order.Ticket.Product == ProductType.Mis
                        && OrderLifecycle.IsWorking(order.Status)
                        && !order.IsSystem
                        && SquareOffDue(account.Profile, order.Ticket.Exchange, order.Ticket.Symbol, now))
                        orders.Add((account.ClientId, orderId));
                }
                foreach (var position in account.Positions.Values)
                {
                    if (position.Product == ProductType.Mis && position.Quantity != 0
                        && SquareOffDue(account.Profile, position.Exchange, position.Symbol, now))
                        positions.Add((account.ClientId, position.Symbol));
                }
            }
            return new SquareOffWork(orders, positions);
        }, cancellationToken);
        if (!due.IsSuccess) return 0;

        var done = 0;
        foreach (var (clientId, orderId) in due.Value.Orders)
            if (await CancelBySystemAsync(clientId, orderId, "Auto square-off: working intraday orders are cancelled.", cancellationToken)) done++;
        foreach (var (clientId, symbol) in due.Value.Positions)
            if (await ClosePositionAsync(clientId, symbol, ProductType.Mis, SystemOrders.SquareOffTag, cancellationToken)) done++;
        return done;
    }

    private sealed record SquareOffWork(IReadOnlyList<(string ClientId, string OrderId)> Orders, IReadOnlyList<(string ClientId, string Symbol)> Positions);

    private bool SquareOffDue(BrokerProfile profile, Exchange exchange, string symbol, DateTimeOffset now)
    {
        if (!profile.AutoSquareOff.TryGetValue(exchange, out var at) || Ist.TimeOf(now) < at) return false;
        if (_options.AlwaysOpen) return true;
        var instrument = _instruments.Find(symbol);
        return instrument is not null && _calendar.WindowFor(instrument, Ist.DateOf(now)) is not null;
    }

    /// <summary>
    /// Turns the account's kill switch on or off. Turning it on cancels every
    /// working order and, if asked, closes every open position; no new order is
    /// accepted until it is off. A client cannot turn it off before the next
    /// trading day; the back office can.
    /// </summary>
    public async Task<Result<KillSwitchView>> SetKillSwitchAsync(
        string clientId, bool active, string by, bool squareOff, CancellationToken cancellationToken = default)
    {
        var changed = await RunAsync<bool>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);

            if (active)
            {
                if (account.KillSwitchActive(now)) return Outcome<bool>.Nothing(false);
                return Outcome<bool>.Of(new KillSwitchChanged
                {
                    ClientId = clientId,
                    Active = true,
                    Until = Ist.Next(now, account.Profile.SessionExpiresAt),
                    By = by,
                    Reason = squareOff ? "Kill switch: cancel every order and close every position." : "Kill switch: cancel every order.",
                }, _ => true);
            }

            if (!account.KillSwitchActive(now)) return Outcome<bool>.Nothing(false);
            if (by != "admin" && account.KillSwitch!.Until is { } until && until > now)
                return BrokerError.Conflict(ErrorCodes.KillSwitchActive,
                    $"The kill switch stays on until {Ist.ToIst(until):yyyy-MM-dd HH:mm} IST.");
            return Outcome<bool>.Of(new KillSwitchChanged { ClientId = clientId, Active = false, By = by, Reason = "Turned off." }, _ => true);
        }, null, cancellationToken);
        if (!changed.IsSuccess) return changed.Error!;

        if (active)
        {
            var working = await ReadAsync<IReadOnlyList<string>>(_ => Result<IReadOnlyList<string>>.Ok(
                _state.LiveOrderIds
                    .Select(id => _state.Orders[id])
                    .Where(o => o.Ticket.ClientId == clientId && OrderLifecycle.IsWorking(o.Status) && !o.IsSystem)
                    .Select(o => o.OrderId)
                    .ToList()), cancellationToken);

            foreach (var orderId in working.Value)
                await CancelBySystemAsync(clientId, orderId, "Kill switch: every working order is cancelled.", cancellationToken);
            if (squareOff)
            {
                var products = await ReadAsync<IReadOnlyList<(string Symbol, ProductType Product)>>(_ =>
                    Result<IReadOnlyList<(string, ProductType)>>.Ok(
                        _state.Account(clientId).Positions.Values.Where(p => p.Quantity != 0).Select(p => (p.Symbol, p.Product)).ToList()),
                    cancellationToken);
                foreach (var (symbol, product) in products.Value)
                    await ClosePositionAsync(clientId, symbol, product, SystemOrders.KillSwitchTag, cancellationToken);
            }
        }

        return await GetKillSwitchAsync(clientId, cancellationToken);
    }

    public Task<Result<KillSwitchView>> GetKillSwitchAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<KillSwitchView>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            var k = account.KillSwitch;
            return account.KillSwitchActive(now)
                ? new KillSwitchView(true, k!.Since, k.Until, k.By, k.Reason)
                : new KillSwitchView(false, null, null, null, null);
        }, cancellationToken);

    private async Task<bool> CancelBySystemAsync(string clientId, string orderId, string reason, CancellationToken cancellationToken)
    {
        var result = await RunAsync<bool>(_ =>
        {
            if (FindOrder(clientId, orderId) is not { } order || !OrderLifecycle.IsWorking(order.Status))
                return Outcome<bool>.Nothing(false);
            return Outcome<bool>.Of(new OrderCancelled { ClientId = clientId, OrderId = orderId, Reason = reason }, _ => true);
        }, null, cancellationToken);
        return result.IsSuccess && result.Value;
    }

    /// <summary>
    /// Closes a position with a broker order at the market: the bid for a sale,
    /// the ask for a purchase, the last price when the side is empty. Without
    /// any price the position stays open for the day's settlement to close.
    /// </summary>
    private async Task<bool> ClosePositionAsync(string clientId, string symbol, ProductType product, string tag, CancellationToken cancellationToken)
    {
        var result = await RunAsync<bool>(now =>
        {
            var account = _state.Account(clientId);
            var position = _state.PositionOf(account, symbol, product);
            if (position is null || position.Quantity == 0) return Outcome<bool>.Nothing(false);
            var instrument = _instruments.Find(symbol);
            var quote = _quotes.Find(symbol);
            if (instrument is null || quote is null) return Outcome<bool>.Nothing(false);

            var side = position.Quantity > 0 ? OrderSide.Sell : OrderSide.Buy;
            var quantity = Math.Abs(position.Quantity);
            var price = side == OrderSide.Sell
                ? quote.Bid is > 0 ? quote.Bid.Value : quote.LastPrice
                : quote.Ask is > 0 ? quote.Ask.Value : quote.LastPrice;

            var tradingDate = Ist.DateOf(now);
            var ticket = new OrderTicket
            {
                OrderId = _state.NextOrderId(tradingDate),
                ClientId = clientId,
                AppId = SystemOrders.AppId,
                Symbol = symbol,
                Exchange = instrument.Exchange,
                Segment = instrument.Segment,
                Side = side,
                Quantity = quantity,
                Type = OrderType.Market,
                Product = product,
                Validity = Validity.Day,
                LotSize = instrument.LotSize,
                AlgoId = string.Empty,
                Tag = tag,
                TradingDate = tradingDate,
            };
            var intent = new OrderIntent(side, quantity, OrderType.Market, product, Validity.Day, null, null);
            var fill = PlanFill(account, instrument, ticket, ticket.OrderId, intent, quantity, 0m, 0m,
                firstFill: true, isSystem: true, new MatchFill(quantity, price, Maker: false));

            return Outcome<bool>.Of(
            [
                new OrderPlaced { ClientId = clientId, Ticket = ticket, BlockedMargin = 0m },
                new OrderAccepted { ClientId = clientId, OrderId = ticket.OrderId, Status = OrderStatus.Open },
                fill,
            ], _ => true);
        }, null, cancellationToken);
        return result.IsSuccess && result.Value;
    }
}

public sealed record KillSwitchView(bool Active, DateTimeOffset? Since, DateTimeOffset? Until, string? By, string? Reason);
