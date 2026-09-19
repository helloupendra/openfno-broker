using System.Collections.Concurrent;
using System.Globalization;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

/// <summary>
/// Everything the broker knows, rebuilt from the journal. <see cref="Apply"/>
/// is the only method that changes it, and it is called for live events and
/// replayed events alike, so a restart ends in exactly the state it left.
/// </summary>
/// <remarks>
/// One writer (the engine, under its lock). Accounts, apps and sessions sit in
/// concurrent maps because request authentication reads them without the lock;
/// the symbols with working orders do too, because the market-data feed asks
/// on every tick whether anything needs matching.
/// </remarks>
public sealed class BrokerState
{
    private int _accountCount;
    private DateOnly _orderDate;
    private int _ordersToday;
    private DateOnly _tradeDate;
    private int _tradesToday;

    public ConcurrentDictionary<string, AccountState> Accounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, AppState> Apps { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OrderState> Orders { get; } = new(StringComparer.Ordinal);

    /// <summary>Orders not yet in a final status: the ones expiry and matching look at.</summary>
    public HashSet<string> LiveOrderIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Live orders by symbol, for matching a quote to the orders it can fill.</summary>
    public Dictionary<string, HashSet<string>> LiveOrdersBySymbol { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many live orders each symbol has; read without the lock.</summary>
    public ConcurrentDictionary<string, int> LiveSymbolCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long LastSeq { get; private set; }

    /// <summary>The last trading date the broker settled.</summary>
    public DateOnly? LastClosedDate { get; private set; }

    public string NextClientId() => $"OFB{_accountCount + 1:00000}";

    /// <summary>A 14-digit order number: the IST trading date (yyMMdd) and a daily sequence, as FYERS numbers orders.</summary>
    public string NextOrderId(DateOnly tradingDate)
    {
        var next = tradingDate == _orderDate ? _ordersToday + 1 : 1;
        return tradingDate.ToString("yyMMdd", CultureInfo.InvariantCulture) + next.ToString("00000000", CultureInfo.InvariantCulture);
    }

    /// <summary>A 15-digit trade number, so it is never mistaken for an order number.</summary>
    public string NextTradeId(DateOnly tradingDate)
    {
        var next = tradingDate == _tradeDate ? _tradesToday + 1 : 1;
        return tradingDate.ToString("yyMMdd", CultureInfo.InvariantCulture) + next.ToString("000000000", CultureInfo.InvariantCulture);
    }

    public void Apply(BrokerEvent e)
    {
        if (e.Seq != LastSeq + 1)
            throw new InvalidOperationException($"Event {e.Seq} applied after {LastSeq}; the journal has a gap or a repeat.");

        switch (e)
        {
            case AccountOpened opened:
                Accounts[opened.ClientId] = new AccountState
                {
                    ClientId = opened.ClientId,
                    Name = opened.Name,
                    Profile = BrokerProfiles.Find(opened.ProfileId)
                        ?? throw new InvalidOperationException($"Account {opened.ClientId} uses unknown profile '{opened.ProfileId}'."),
                    ProtectedTotpSecret = opened.ProtectedTotpSecret,
                    OpenedAt = opened.At,
                };
                _accountCount++;
                break;

            case FundsAdded added:
                {
                    var account = Account(added.ClientId);
                    account.NetDeposits += added.Amount;
                    Post(account, added, "PAY_IN", added.Amount, added.Reference);
                    break;
                }

            case FundsWithdrawn withdrawn:
                {
                    var account = Account(withdrawn.ClientId);
                    account.NetDeposits -= withdrawn.Amount;
                    Post(account, withdrawn, "PAY_OUT", -withdrawn.Amount, withdrawn.Reference);
                    break;
                }

            case AppRegistered registered:
                {
                    var app = new AppState
                    {
                        AppId = registered.AppId,
                        ClientId = registered.ClientId,
                        SecretHash = registered.SecretHash,
                        StaticIps = registered.StaticIps,
                    };
                    Account(registered.ClientId).Apps[app.AppId] = app;
                    Apps[app.AppId] = app;
                    break;
                }

            case StaticIpsChanged changed:
                {
                    var app = App(changed.AppId);
                    app.StaticIps = changed.StaticIps;
                    app.StaticIpChanges.Add(changed.At);
                    break;
                }

            case SessionOpened session:
                {
                    Sessions[session.TokenHash] = new SessionState(
                        session.TokenHash, session.ClientId, session.AppId, session.At, session.ExpiresAt, session.ClientIp);
                    App(session.AppId).ActiveTokenHash = session.TokenHash;
                    var account = Account(session.ClientId);
                    account.LastTotpStep = Math.Max(account.LastTotpStep, session.TotpStep);
                    break;
                }

            case SessionClosed closed:
                if (Sessions.TryRemove(closed.TokenHash, out var ended)
                    && Apps.TryGetValue(ended.AppId, out var owner)
                    && owner.ActiveTokenHash == closed.TokenHash)
                    owner.ActiveTokenHash = null;
                break;

            case OrderPlaced placed:
                {
                    var order = NewOrder(placed.Ticket, placed.At);
                    order.BlockedMargin = placed.BlockedMargin;
                    Account(placed.ClientId).BlockedMargin += placed.BlockedMargin;
                    Record(order, placed, "placed", order.IsSystem
                        ? $"Placed by the broker's RMS ({placed.Ticket.Tag})."
                        : "Passed the broker's checks; sent to the exchange.");
                    break;
                }

            case OrderRejected rejected:
                {
                    var order = NewOrder(rejected.Ticket, rejected.At);
                    order.MoveTo(OrderStatus.Rejected);
                    order.RejectionCode = rejected.Code;
                    order.Message = rejected.Message;
                    Record(order, rejected, "rejected", $"{rejected.Code}: {rejected.Message}");
                    break;
                }

            case OrderAccepted accepted:
                {
                    var order = Order(accepted.OrderId);
                    order.MoveTo(accepted.Status);
                    Record(order, accepted, "accepted", accepted.Status == OrderStatus.TriggerPending
                        ? "Acknowledged by the exchange; waiting for the trigger price."
                        : "Acknowledged by the exchange; resting in the book.");
                    break;
                }

            case OrderExchangeRejected exchangeRejected:
                {
                    var order = Order(exchangeRejected.OrderId);
                    order.MoveTo(OrderStatus.Rejected);
                    order.RejectionCode = exchangeRejected.Code;
                    order.Message = exchangeRejected.Message;
                    Release(order);
                    Record(order, exchangeRejected, "exchange_rejected", $"{exchangeRejected.Code}: {exchangeRejected.Message}");
                    break;
                }

            case OrderModified modified:
                {
                    var order = Order(modified.OrderId);
                    Account(modified.ClientId).BlockedMargin += modified.BlockedMargin - order.BlockedMargin;
                    order.BlockedMargin = modified.BlockedMargin;
                    order.Quantity = modified.Quantity;
                    order.Type = modified.Type;
                    order.Validity = modified.Validity;
                    order.LimitPrice = modified.LimitPrice;
                    order.TriggerPrice = modified.TriggerPrice;
                    order.Modifications++;
                    order.MoveTo(modified.Status);
                    Record(order, modified, "modified", Describe(modified));
                    break;
                }

            case OrderAmendRejected refused:
                {
                    var order = Order(refused.OrderId);
                    Record(order, refused, $"{refused.Action}_rejected", $"{refused.Code}: {refused.Message}");
                    break;
                }

            case OrderTriggered triggered:
                {
                    var order = Order(triggered.OrderId);
                    order.MoveTo(OrderStatus.Open);
                    Record(order, triggered, "triggered", $"Last price {triggered.LastPrice} reached the trigger {order.TriggerPrice}; now a limit order.");
                    break;
                }

            case OrderFilled filled:
                ApplyFill(filled);
                break;

            case OrderCancelled cancelled:
                {
                    var order = Order(cancelled.OrderId);
                    order.MoveTo(OrderStatus.Cancelled);
                    Release(order);
                    Record(order, cancelled, "cancelled", cancelled.Reason);
                    break;
                }

            case OrderExpired expired:
                {
                    var order = Order(expired.OrderId);
                    order.MoveTo(OrderStatus.Expired);
                    Release(order);
                    Record(order, expired, "expired", "Still working when the session closed.");
                    break;
                }

            case KillSwitchChanged kill:
                Account(kill.ClientId).KillSwitch = kill.Active
                    ? new KillSwitchState(kill.At, kill.Until, kill.By, kill.Reason)
                    : null;
                break;

            case DaySettled settled:
                ApplySettlement(settled);
                break;

            case TradingDayClosed closed:
                LastClosedDate = closed.TradingDate;
                break;

            default:
                throw new InvalidOperationException($"No state change is defined for {e.GetType().Name}.");
        }

        LastSeq = e.Seq;
    }

    public AccountState Account(string clientId)
        => Accounts.TryGetValue(clientId, out var account)
            ? account
            : throw new InvalidOperationException($"Unknown account {clientId}.");

    public PositionState? PositionOf(AccountState account, string symbol, ProductType product)
        => account.Positions.GetValueOrDefault((symbol, product));

    private void ApplyFill(OrderFilled filled)
    {
        var order = Order(filled.OrderId);
        var account = Account(filled.ClientId);
        var ticket = order.Ticket;

        order.FilledQuantity += filled.Quantity;
        order.FilledValue += filled.Quantity * filled.Price;
        order.BrokerageCharged += filled.Charges.Brokerage;
        order.MoveTo(order.FilledQuantity >= order.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled);
        account.BlockedMargin += filled.OrderMarginAfter - order.BlockedMargin;
        order.BlockedMargin = filled.OrderMarginAfter;

        var position = PositionOf(account, ticket.Symbol, ticket.Product);
        if (position is null)
        {
            position = new PositionState
            {
                ClientId = ticket.ClientId,
                Symbol = ticket.Symbol,
                Exchange = ticket.Exchange,
                Segment = ticket.Segment,
                Product = ticket.Product,
            };
            account.Positions[(ticket.Symbol, ticket.Product)] = position;
        }
        account.PositionMargin += filled.PositionAfter.Margin - position.Margin;
        position.Quantity = filled.PositionAfter.Quantity;
        position.AveragePrice = filled.PositionAfter.AveragePrice;
        position.Margin = filled.PositionAfter.Margin;
        position.RealisedToday += filled.Realised;
        position.ChargesToday += filled.Charges.Total;
        if (ticket.Side == OrderSide.Buy)
        {
            position.BuyQuantity += filled.Quantity;
            position.BuyValue += filled.Quantity * filled.Price;
        }
        else
        {
            position.SellQuantity += filled.Quantity;
            position.SellValue += filled.Quantity * filled.Price;
        }
        position.UpdatedAt = filled.At;

        account.DayRealised += filled.Realised;
        account.DayCharges += filled.Charges.Total;
        account.Trades.Add(new TradeRecord(
            filled.TradeId, order.OrderId, ticket.ClientId, ticket.Symbol, ticket.Exchange, ticket.Segment, ticket.Side,
            ticket.Product, filled.Quantity, filled.Price, filled.Maker, filled.Charges, filled.Realised,
            ticket.TradingDate, filled.At, ticket.Tag));

        var tradeDate = ticket.TradingDate;
        if (tradeDate != _tradeDate)
        {
            _tradeDate = tradeDate;
            _tradesToday = 0;
        }
        _tradesToday++;

        var how = filled.Maker ? "resting, at its own price" : "taking the market";
        Record(order, filled, order.Status == OrderStatus.Filled ? "filled" : "partially_filled",
            $"Traded {filled.Quantity} @ {filled.Price} ({how}); trade {filled.TradeId}; charges ₹{filled.Charges.Total}.");
    }

    private void ApplySettlement(DaySettled settled)
    {
        var account = Account(settled.ClientId);
        foreach (var entry in settled.Entries)
        {
            if (account.Positions.TryGetValue((entry.Symbol, entry.Product), out var position))
            {
                account.PositionMargin += entry.PositionAfter.Margin - position.Margin;
                position.Quantity = entry.PositionAfter.Quantity;
                position.AveragePrice = entry.PositionAfter.AveragePrice;
                position.Margin = entry.PositionAfter.Margin;
            }

            if (entry.Kind is SettlementKind.DeliveryIn or SettlementKind.DeliveryOut)
            {
                if (!account.Holdings.TryGetValue(entry.Symbol, out var holding))
                {
                    holding = new HoldingState { Symbol = entry.Symbol, Exchange = position?.Exchange ?? default };
                    account.Holdings[entry.Symbol] = holding;
                }
                if (entry.Kind == SettlementKind.DeliveryIn)
                {
                    var total = holding.Quantity + entry.Quantity;
                    holding.AveragePrice = decimal.Round((holding.AveragePrice * holding.Quantity + entry.Price * entry.Quantity) / total, 4);
                    holding.Quantity = total;
                }
                else
                {
                    holding.Quantity -= entry.Quantity;
                    if (holding.Quantity <= 0) account.Holdings.Remove(entry.Symbol);
                }
                Post(account, settled, entry.Kind == SettlementKind.DeliveryIn ? "DELIVERY_BUY" : "DELIVERY_SELL",
                    entry.CashAmount, $"{entry.Symbol} {entry.Quantity} @ {entry.Price} ({settled.TradingDate:yyyy-MM-dd})");
            }
        }

        if (settled.Realised != 0)
            Post(account, settled, "REALISED_PNL", settled.Realised, $"Trading on {settled.TradingDate:yyyy-MM-dd}");
        if (settled.Charges != 0)
            Post(account, settled, "CHARGES", -settled.Charges, $"Charges on {settled.TradingDate:yyyy-MM-dd}");
        account.DayRealised = 0m;
        account.DayCharges = 0m;

        // Flat positions leave the book; open ones start the next day afresh.
        foreach (var key in account.Positions.Where(p => p.Value.Quantity == 0).Select(p => p.Key).ToList())
            account.Positions.Remove(key);
        foreach (var position in account.Positions.Values)
        {
            position.RealisedToday = 0m;
            position.ChargesToday = 0m;
            position.BuyQuantity = 0;
            position.BuyValue = 0m;
            position.SellQuantity = 0;
            position.SellValue = 0m;
        }
    }

    private static void Post(AccountState account, BrokerEvent e, string kind, decimal amount, string reference)
    {
        account.LedgerBalance += amount;
        account.Ledger.Add(new LedgerEntry(e.Seq, e.At, kind, amount, reference));
    }

    private AppState App(string appId)
        => Apps.TryGetValue(appId, out var app) ? app : throw new InvalidOperationException($"Unknown app {appId}.");

    private OrderState Order(string orderId)
        => Orders.TryGetValue(orderId, out var order) ? order : throw new InvalidOperationException($"Unknown order {orderId}.");

    private OrderState NewOrder(OrderTicket ticket, DateTimeOffset at)
    {
        if (Orders.ContainsKey(ticket.OrderId))
            throw new InvalidOperationException($"Order {ticket.OrderId} already exists.");

        var order = new OrderState(ticket, at);
        Orders[order.OrderId] = order;
        Account(ticket.ClientId).Orders.Add(order);

        if (ticket.TradingDate != _orderDate)
        {
            _orderDate = ticket.TradingDate;
            _ordersToday = 0;
        }
        _ordersToday++;
        return order;
    }

    private void Release(OrderState order)
    {
        Account(order.Ticket.ClientId).BlockedMargin -= order.BlockedMargin;
        order.BlockedMargin = 0;
    }

    private void Record(OrderState order, BrokerEvent e, string name, string? note)
    {
        order.UpdatedAt = e.At;
        order.History.Add(new OrderHistoryEntry(e.Seq, e.At, name, order.Status, note));

        var symbol = order.Ticket.Symbol;
        if (OrderLifecycle.IsTerminal(order.Status))
        {
            if (LiveOrderIds.Remove(order.OrderId) && LiveOrdersBySymbol.TryGetValue(symbol, out var ids))
            {
                ids.Remove(order.OrderId);
                if (ids.Count == 0) LiveOrdersBySymbol.Remove(symbol);
                LiveSymbolCounts.AddOrUpdate(symbol, 0, (_, n) => Math.Max(0, n - 1));
                if (LiveSymbolCounts.TryGetValue(symbol, out var left) && left == 0) LiveSymbolCounts.TryRemove(symbol, out _);
            }
        }
        else if (LiveOrderIds.Add(order.OrderId))
        {
            if (!LiveOrdersBySymbol.TryGetValue(symbol, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                LiveOrdersBySymbol[symbol] = ids;
            }
            ids.Add(order.OrderId);
            LiveSymbolCounts.AddOrUpdate(symbol, 1, (_, n) => n + 1);
        }
    }

    private static string Describe(OrderModified m)
    {
        var parts = new List<string> { $"qty {m.Quantity}", m.Type.ToString() };
        if (m.LimitPrice is { } limit) parts.Add($"limit {limit}");
        if (m.TriggerPrice is { } trigger) parts.Add($"trigger {trigger}");
        return "Now " + string.Join(", ", parts) + ".";
    }
}
