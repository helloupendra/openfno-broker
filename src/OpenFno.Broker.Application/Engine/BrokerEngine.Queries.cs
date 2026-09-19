using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Application.Engine;

public sealed record AccountSummary(
    string ClientId,
    string Name,
    string ProfileId,
    decimal NetDeposits,
    decimal Available,
    int Apps,
    int LiveOrders);

public sealed partial class BrokerEngine
{
    /// <summary>The order book for one IST trading day (today by default), newest first.</summary>
    public Task<Result<IReadOnlyList<OrderView>>> GetOrdersAsync(string clientId, DateOnly? tradingDate, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<OrderView>>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            var day = tradingDate ?? Ist.DateOf(now);
            var orders = account.Orders
                .Where(o => o.Ticket.TradingDate == day)
                .OrderByDescending(o => o.OrderId, StringComparer.Ordinal)
                .Select(o => OrderView.From(o, withHistory: false))
                .ToList();
            return Result<IReadOnlyList<OrderView>>.Ok(orders);
        }, cancellationToken);

    /// <summary>One order with its full history: every step, when it happened and what it changed.</summary>
    public Task<Result<OrderView>> GetOrderAsync(string clientId, string orderId, CancellationToken cancellationToken = default)
        => ReadAsync<OrderView>(_ => FindOrder(clientId, orderId) is { } order
            ? OrderView.From(order, withHistory: true)
            : OrderMissing(orderId), cancellationToken);

    public Task<Result<FundsView>> GetFundsAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<FundsView>(_ => _state.Accounts.TryGetValue(clientId, out var account)
            ? FundsView.From(account)
            : AccountMissing(clientId), cancellationToken);

    public Task<Result<AccountView>> GetAccountAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<AccountView>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            return new AccountView(
                account.ClientId,
                account.Name,
                account.OpenedAt,
                ProfileView.From(account.Profile),
                account.Apps.Values.Select(app => AppViewOf(app, now)).ToList());
        }, cancellationToken);

    public Task<Result<IReadOnlyList<AccountSummary>>> ListAccountsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<AccountSummary>>(_ =>
        {
            var summaries = _state.Accounts.Values
                .OrderBy(a => a.ClientId, StringComparer.Ordinal)
                .Select(a => new AccountSummary(
                    a.ClientId, a.Name, a.Profile.Id, a.NetDeposits, a.Available, a.Apps.Count,
                    a.Orders.Count(o => _state.LiveOrderIds.Contains(o.OrderId))))
                .ToList();
            return Result<IReadOnlyList<AccountSummary>>.Ok(summaries);
        }, cancellationToken);

    /// <summary>The account's journal: every event recorded for it, oldest first. Read from the store, not from memory.</summary>
    public Task<IReadOnlyList<BrokerEvent>> GetJournalAsync(string clientId, long afterSeq, int limit, CancellationToken cancellationToken = default)
        => _journal.ReadAsync(clientId, Math.Max(0, afterSeq), Math.Clamp(limit, 1, 1000), cancellationToken);

    /// <summary>Today's counts across every account, and how long the simulated exchange took to acknowledge.</summary>
    public Task<Result<BrokerOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
        => ReadAsync<BrokerOverview>(now =>
        {
            var today = Ist.DateOf(now);
            var orders = _state.Accounts.Values.SelectMany(a => a.Orders).Where(o => o.Ticket.TradingDate == today).ToList();

            var acks = orders
                .Select(o => (Placed: o.History.FirstOrDefault(h => h.Event == "placed"), Accepted: o.History.FirstOrDefault(h => h.Event == "accepted")))
                .Where(p => p.Placed is not null && p.Accepted is not null)
                .Select(p => (p.Accepted!.At - p.Placed!.At).TotalMilliseconds);

            return new BrokerOverview(
                today,
                _state.Accounts.Count,
                _state.LiveOrderIds.Count,
                orders.GroupBy(o => o.Status).ToDictionary(g => g.Key, g => g.Count()),
                orders.Where(o => o.RejectionCode is not null).GroupBy(o => o.RejectionCode!).ToDictionary(g => g.Key, g => g.Count()),
                LatencySummary.Of(acks),
                _state.LastSeq);
        }, cancellationToken);
}
