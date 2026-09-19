using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

public sealed record AccountSummary(
    string ClientId,
    string Name,
    string ProfileId,
    decimal NetDeposits,
    decimal Available,
    decimal RealisedToday,
    int Apps,
    int LiveOrders,
    int OpenPositions,
    bool KillSwitch);

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

    /// <summary>The tradebook for one IST trading day, newest first.</summary>
    public Task<Result<IReadOnlyList<TradeRecord>>> GetTradesAsync(string clientId, DateOnly? tradingDate, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<TradeRecord>>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            var day = tradingDate ?? Ist.DateOf(now);
            return Result<IReadOnlyList<TradeRecord>>.Ok(
                account.Trades.Where(t => t.TradingDate == day).OrderByDescending(t => t.TradeId, StringComparer.Ordinal).ToList());
        }, cancellationToken);

    /// <summary>Open positions, and the ones closed since the last settlement, marked at the latest prices.</summary>
    public Task<Result<IReadOnlyList<PositionView>>> GetPositionsAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<PositionView>>(_ =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            return Result<IReadOnlyList<PositionView>>.Ok(account.Positions.Values
                .OrderByDescending(p => p.Quantity != 0)
                .ThenBy(p => p.Symbol, StringComparer.Ordinal)
                .Select(PositionViewOf)
                .ToList());
        }, cancellationToken);

    public Task<Result<IReadOnlyList<HoldingView>>> GetHoldingsAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<HoldingView>>(_ =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            return Result<IReadOnlyList<HoldingView>>.Ok(account.Holdings.Values
                .OrderBy(h => h.Symbol, StringComparer.Ordinal)
                .Select(h =>
                {
                    var last = _quotes.Find(h.Symbol)?.LastPrice;
                    var invested = decimal.Round(h.Quantity * h.AveragePrice, 2);
                    var value = decimal.Round(h.Quantity * (last ?? h.AveragePrice), 2);
                    return new HoldingView(h.Symbol, h.Exchange, h.Quantity, h.AveragePrice, last, invested, value, value - invested);
                })
                .ToList());
        }, cancellationToken);

    /// <summary>A day's trades with the charges on them, as a contract note lists them.</summary>
    public Task<Result<ContractNote>> GetContractNoteAsync(string clientId, DateOnly? tradingDate, CancellationToken cancellationToken = default)
        => ReadAsync<ContractNote>(now =>
        {
            if (!_state.Accounts.TryGetValue(clientId, out var account)) return AccountMissing(clientId);
            var day = tradingDate ?? Ist.DateOf(now);
            var trades = account.Trades.Where(t => t.TradingDate == day).OrderBy(t => t.TradeId, StringComparer.Ordinal).ToList();
            var buy = trades.Where(t => t.Side == OrderSide.Buy).Sum(t => t.Quantity * t.Price);
            var sell = trades.Where(t => t.Side == OrderSide.Sell).Sum(t => t.Quantity * t.Price);
            var charges = trades.Aggregate(ChargeBreakdown.None, (sum, t) => sum.Add(t.Charges));
            return new ContractNote(account.ClientId, account.Name, day, trades, buy, sell, charges, sell - buy, sell - buy - charges.Total);
        }, cancellationToken);

    public Task<Result<FundsView>> GetFundsAsync(string clientId, CancellationToken cancellationToken = default)
        => ReadAsync<FundsView>(_ => _state.Accounts.TryGetValue(clientId, out var account)
            ? FundsOf(account)
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
        => ReadAsync<IReadOnlyList<AccountSummary>>(now =>
        {
            var summaries = _state.Accounts.Values
                .OrderBy(a => a.ClientId, StringComparer.Ordinal)
                .Select(a => new AccountSummary(
                    a.ClientId, a.Name, a.Profile.Id, a.NetDeposits, Available(a), a.DayRealised, a.Apps.Count,
                    a.Orders.Count(o => _state.LiveOrderIds.Contains(o.OrderId)),
                    a.Positions.Values.Count(p => p.Quantity != 0),
                    a.KillSwitchActive(now)))
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
            var accounts = _state.Accounts.Values.ToList();
            var orders = accounts.SelectMany(a => a.Orders).Where(o => o.Ticket.TradingDate == today).ToList();
            var trades = accounts.SelectMany(a => a.Trades).Where(t => t.TradingDate == today).ToList();

            var acks = orders
                .Select(o => (Placed: o.History.FirstOrDefault(h => h.Event == "placed"), Accepted: o.History.FirstOrDefault(h => h.Event == "accepted")))
                .Where(p => p.Placed is not null && p.Accepted is not null)
                .Select(p => (p.Accepted!.At - p.Placed!.At).TotalMilliseconds);

            return new BrokerOverview(
                today,
                accounts.Count,
                _state.LiveOrderIds.Count,
                orders.GroupBy(o => o.Status).ToDictionary(g => g.Key, g => g.Count()),
                orders.Where(o => o.RejectionCode is not null).GroupBy(o => o.RejectionCode!).ToDictionary(g => g.Key, g => g.Count()),
                LatencySummary.Of(acks),
                trades.Count,
                trades.Sum(t => t.Quantity * t.Price),
                accounts.Sum(a => a.DayCharges),
                accounts.Sum(a => a.DayRealised),
                accounts.Sum(a => a.Positions.Values.Count(p => p.Quantity != 0)),
                _state.LastClosedDate,
                _state.LastSeq);
        }, cancellationToken);

    private FundsView FundsOf(AccountState account)
    {
        var unrealised = Unrealised(account);
        return new FundsView(
            account.ClientId,
            account.NetDeposits,
            account.LedgerBalance,
            account.DayRealised,
            account.DayCharges,
            account.Cash,
            account.BlockedMargin,
            account.PositionMargin,
            unrealised,
            Available(account),
            account.Ledger.ToList());
    }

    private PositionView PositionViewOf(PositionState p)
    {
        var last = _quotes.Find(p.Symbol)?.LastPrice;
        var unrealised = p.Quantity == 0 ? 0m : PositionMath.Unrealised(p.Quantity, p.AveragePrice, last ?? p.AveragePrice);
        return new PositionView(
            p.Symbol, p.Exchange, p.Segment, p.Product, p.Quantity, p.AveragePrice, last,
            unrealised,
            p.RealisedToday,
            p.ChargesToday,
            p.RealisedToday + unrealised - p.ChargesToday,
            p.BuyQuantity, p.BuyQuantity > 0 ? decimal.Round(p.BuyValue / p.BuyQuantity, 4) : null,
            p.SellQuantity, p.SellQuantity > 0 ? decimal.Round(p.SellValue / p.SellQuantity, 4) : null,
            p.Margin, p.UpdatedAt);
    }
}
