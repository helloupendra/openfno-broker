using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

/// <summary>An order as the order book shows it.</summary>
public sealed record OrderView
{
    public required string OrderId { get; init; }
    public required string Symbol { get; init; }
    public required Exchange Exchange { get; init; }
    public required Segment Segment { get; init; }
    public required OrderSide Side { get; init; }
    public required int Quantity { get; init; }
    public required int LotSize { get; init; }
    public required int Lots { get; init; }
    public required int FilledQuantity { get; init; }
    public required int PendingQuantity { get; init; }
    public required OrderType Type { get; init; }
    public required ProductType Product { get; init; }
    public required Validity Validity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? TriggerPrice { get; init; }
    public decimal? AveragePrice { get; init; }
    public required OrderStatus Status { get; init; }
    public string? RejectionCode { get; init; }
    public string? Message { get; init; }
    public string? Tag { get; init; }
    public required string AlgoId { get; init; }
    public required string AppId { get; init; }
    public required decimal BlockedMargin { get; init; }
    public required int Modifications { get; init; }
    public required DateOnly TradingDate { get; init; }
    public required DateTimeOffset PlacedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<OrderHistoryEntry>? History { get; init; }

    public static OrderView From(OrderState order, bool withHistory) => new()
    {
        OrderId = order.OrderId,
        Symbol = order.Ticket.Symbol,
        Exchange = order.Ticket.Exchange,
        Segment = order.Ticket.Segment,
        Side = order.Ticket.Side,
        Quantity = order.Quantity,
        LotSize = order.Ticket.LotSize,
        Lots = order.Ticket.LotSize > 0 ? order.Quantity / order.Ticket.LotSize : 0,
        FilledQuantity = order.FilledQuantity,
        PendingQuantity = OrderLifecycle.IsTerminal(order.Status) ? 0 : order.Quantity - order.FilledQuantity,
        Type = order.Type,
        Product = order.Ticket.Product,
        Validity = order.Validity,
        LimitPrice = order.LimitPrice,
        TriggerPrice = order.TriggerPrice,
        AveragePrice = order.AveragePrice,
        Status = order.Status,
        RejectionCode = order.RejectionCode,
        Message = order.Message,
        Tag = order.Ticket.Tag,
        AlgoId = order.Ticket.AlgoId,
        AppId = order.Ticket.AppId,
        BlockedMargin = order.BlockedMargin,
        Modifications = order.Modifications,
        TradingDate = order.Ticket.TradingDate,
        PlacedAt = order.PlacedAt,
        UpdatedAt = order.UpdatedAt,
        History = withHistory ? order.History.ToList() : null,
    };
}

/// <summary>
/// An account's money. <see cref="Cash"/> is the ledger plus today's unsettled
/// profit, loss and charges; <see cref="Available"/> is what a new order may
/// use: cash less margin held, less any unrealised loss.
/// </summary>
public sealed record FundsView(
    string ClientId,
    decimal NetDeposits,
    decimal LedgerBalance,
    decimal RealisedToday,
    decimal ChargesToday,
    decimal Cash,
    decimal OrderMargin,
    decimal PositionMargin,
    decimal Unrealised,
    decimal Available,
    IReadOnlyList<LedgerEntry> Ledger);

public sealed record PositionView(
    string Symbol,
    Exchange Exchange,
    Segment Segment,
    ProductType Product,
    int Quantity,
    decimal AveragePrice,
    decimal? LastPrice,
    decimal Unrealised,
    decimal RealisedToday,
    int BuyQuantity,
    decimal? BuyAverage,
    int SellQuantity,
    decimal? SellAverage,
    decimal Margin,
    DateTimeOffset UpdatedAt);

public sealed record HoldingView(
    string Symbol,
    Exchange Exchange,
    int Quantity,
    decimal AveragePrice,
    decimal? LastPrice,
    decimal Invested,
    decimal Value,
    decimal Pnl);

/// <summary>A day's trades for one account with their charges: the simulated contract note.</summary>
public sealed record ContractNote(
    string ClientId,
    string Name,
    DateOnly TradingDate,
    IReadOnlyList<TradeRecord> Trades,
    decimal BuyValue,
    decimal SellValue,
    ChargeBreakdown Charges,
    decimal NetTradedValue,
    decimal NetAfterCharges);

public sealed record AppView(
    string AppId,
    IReadOnlyList<string> StaticIps,
    int StaticIpChangesThisWeek,
    DateTimeOffset? SessionExpiresAt);

public sealed record ProfileView(
    string Id,
    string Name,
    RateLimitPolicy RateLimits,
    int MaxStaticIpsPerApp,
    int StaticIpChangesPerWeek,
    TimeOnly SessionExpiresAtIst,
    bool AllowMarketOrders,
    bool AllowIocInCommodities,
    int? MaxModificationsPerOrder,
    IReadOnlyDictionary<Exchange, TimeOnly> IntradayCutoffIst,
    MarginRates Margins,
    decimal CashPriceBandPercent,
    string AlgoId)
{
    public static ProfileView From(BrokerProfile p) => new(
        p.Id, p.Name, p.RateLimits, p.MaxStaticIpsPerApp, p.StaticIpChangesPerWeek, p.SessionExpiresAt,
        p.AllowMarketOrders, p.AllowIocInCommodities, p.MaxModificationsPerOrder, p.IntradayCutoff,
        p.Margins, p.CashPriceBandPercent, p.AlgoId);
}

public sealed record AccountView(
    string ClientId,
    string Name,
    DateTimeOffset OpenedAt,
    ProfileView Profile,
    IReadOnlyList<AppView> Apps);

/// <summary>Returned once when an account is opened: the TOTP secret is never shown again.</summary>
public sealed record OpenedAccount(string ClientId, string TotpSecret, string TotpUri);

/// <summary>Returned once when an app is registered: the app secret is never shown again.</summary>
public sealed record RegisteredApp(string ClientId, string AppId, string AppSecret, IReadOnlyList<string> StaticIps);

public sealed record SessionGrant(string AccessToken, string ClientId, string AppId, DateTimeOffset ExpiresAt);

/// <summary>Who is calling, established from an access token.</summary>
public sealed record Principal(
    string ClientId,
    string AppId,
    string TokenHash,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> StaticIps,
    BrokerProfile Profile);

/// <summary>How long something took across a set of samples.</summary>
public sealed record LatencySummary(int Samples, double P50Ms, double P95Ms, double MaxMs)
{
    public static LatencySummary? Of(IEnumerable<double> milliseconds)
    {
        var sorted = milliseconds.Order().ToArray();
        if (sorted.Length == 0) return null;
        return new LatencySummary(sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of an ascending array.</summary>
    private static double Percentile(double[] sorted, double p)
        => Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)], 2);
}

/// <summary>The back office's first screen: how many of what, today.</summary>
public sealed record BrokerOverview(
    DateOnly TradingDate,
    int Accounts,
    int LiveOrders,
    IReadOnlyDictionary<OrderStatus, int> OrdersToday,
    IReadOnlyDictionary<string, int> RejectionsToday,
    LatencySummary? ExchangeAck,
    int TradesToday,
    decimal TurnoverToday,
    decimal ChargesToday,
    decimal RealisedToday,
    int OpenPositions,
    DateOnly? LastClosedDate,
    long LastSeq);
