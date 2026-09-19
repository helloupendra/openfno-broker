using System.Collections.Concurrent;
using System.Globalization;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Application.Engine;

/// <summary>
/// Everything the broker knows, rebuilt from the journal. <see cref="Apply"/>
/// is the only method that changes it, and it is called for live events and
/// replayed events alike, so a restart ends in exactly the state it left.
/// </summary>
/// <remarks>
/// One writer (the engine, under its lock). Accounts, apps and sessions sit in
/// concurrent maps because request authentication reads them without the lock.
/// </remarks>
public sealed class BrokerState
{
    private int _accountCount;
    private DateOnly _orderDate;
    private int _ordersToday;

    public ConcurrentDictionary<string, AccountState> Accounts { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, AppState> Apps { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, OrderState> Orders { get; } = new(StringComparer.Ordinal);

    /// <summary>Orders not yet in a final status: the ones expiry and matching look at.</summary>
    public HashSet<string> LiveOrderIds { get; } = new(StringComparer.Ordinal);

    public long LastSeq { get; private set; }

    public string NextClientId() => $"OFB{_accountCount + 1:00000}";

    /// <summary>A 14-digit order number: the IST trading date (yyMMdd) and a daily sequence, as FYERS numbers orders.</summary>
    public string NextOrderId(DateOnly tradingDate)
    {
        var next = tradingDate == _orderDate ? _ordersToday + 1 : 1;
        return tradingDate.ToString("yyMMdd", CultureInfo.InvariantCulture) + next.ToString("00000000", CultureInfo.InvariantCulture);
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
                    account.Ledger.Add(new LedgerEntry(added.Seq, added.At, "PAY_IN", added.Amount, added.Reference));
                    break;
                }

            case FundsWithdrawn withdrawn:
                {
                    var account = Account(withdrawn.ClientId);
                    account.NetDeposits -= withdrawn.Amount;
                    account.Ledger.Add(new LedgerEntry(withdrawn.Seq, withdrawn.At, "PAY_OUT", -withdrawn.Amount, withdrawn.Reference));
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
                    Record(order, placed, "placed", "Passed the broker's checks; sent to the exchange.");
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

            default:
                throw new InvalidOperationException($"No state change is defined for {e.GetType().Name}.");
        }

        LastSeq = e.Seq;
    }

    public AccountState Account(string clientId)
        => Accounts.TryGetValue(clientId, out var account)
            ? account
            : throw new InvalidOperationException($"Unknown account {clientId}.");

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
        if (OrderLifecycle.IsTerminal(order.Status)) LiveOrderIds.Remove(order.OrderId);
        else LiveOrderIds.Add(order.OrderId);
    }

    private static string Describe(OrderModified m)
    {
        var parts = new List<string> { $"qty {m.Quantity}", m.Type.ToString() };
        if (m.LimitPrice is { } limit) parts.Add($"limit {limit}");
        if (m.TriggerPrice is { } trigger) parts.Add($"trigger {trigger}");
        return "Now " + string.Join(", ", parts) + ".";
    }
}
