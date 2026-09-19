using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

public sealed class AccountState
{
    public required string ClientId { get; init; }
    public required string Name { get; init; }
    public required BrokerProfile Profile { get; init; }
    public required string ProtectedTotpSecret { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    /// <summary>Pay-ins minus pay-outs.</summary>
    public decimal NetDeposits { get; set; }

    /// <summary>Settled cash: every ledger entry so far (pay-ins, pay-outs, posted P&amp;L and charges, delivery).</summary>
    public decimal LedgerBalance { get; set; }

    /// <summary>Profit or loss booked since the last settlement; posted to the ledger at the day's end.</summary>
    public decimal DayRealised { get; set; }

    /// <summary>Charges since the last settlement; posted to the ledger at the day's end.</summary>
    public decimal DayCharges { get; set; }

    /// <summary>Margin held by working orders.</summary>
    public decimal BlockedMargin { get; set; }

    /// <summary>Margin held by open positions.</summary>
    public decimal PositionMargin { get; set; }

    /// <summary>Cash including today's unsettled profit, loss and charges.</summary>
    public decimal Cash => LedgerBalance + DayRealised - DayCharges;

    public long LastTotpStep { get; set; }

    public KillSwitchState? KillSwitch { get; set; }

    public List<LedgerEntry> Ledger { get; } = [];
    public Dictionary<string, AppState> Apps { get; } = new(StringComparer.Ordinal);
    public List<OrderState> Orders { get; } = [];
    public List<TradeRecord> Trades { get; } = [];
    public Dictionary<(string Symbol, ProductType Product), PositionState> Positions { get; } = [];
    public Dictionary<string, HoldingState> Holdings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool KillSwitchActive(DateTimeOffset now) => KillSwitch is { } k && (k.Until is null || k.Until > now);
}

public sealed record KillSwitchState(DateTimeOffset Since, DateTimeOffset? Until, string By, string Reason);

/// <summary>One money movement on the account.</summary>
public sealed record LedgerEntry(long Seq, DateTimeOffset At, string Kind, decimal Amount, string Reference);

/// <summary>
/// An API app: the key a client's program logs in with. Read without the
/// engine lock on every request, so the fields it is read by are replaced
/// whole, never mutated in place.
/// </summary>
public sealed class AppState
{
    public required string AppId { get; init; }
    public required string ClientId { get; init; }
    public required string SecretHash { get; init; }

    private volatile string[] _staticIps = [];

    public IReadOnlyList<string> StaticIps
    {
        get => _staticIps;
        set => _staticIps = [.. value];
    }

    public List<DateTimeOffset> StaticIpChanges { get; } = [];
    public string? ActiveTokenHash { get; set; }
}

public sealed record SessionState(
    string TokenHash,
    string ClientId,
    string AppId,
    DateTimeOffset OpenedAt,
    DateTimeOffset ExpiresAt,
    string? ClientIp);

/// <summary>One order's current state and its history.</summary>
public sealed class OrderState
{
    public OrderState(OrderTicket ticket, DateTimeOffset placedAt)
    {
        Ticket = ticket;
        Quantity = ticket.Quantity;
        Type = ticket.Type;
        Validity = ticket.Validity;
        LimitPrice = ticket.LimitPrice;
        TriggerPrice = ticket.TriggerPrice;
        PlacedAt = placedAt;
        UpdatedAt = placedAt;
    }

    public OrderTicket Ticket { get; }
    public string OrderId => Ticket.OrderId;

    /// <summary>Placed by the broker itself (auto square-off, kill switch), not by a client.</summary>
    public bool IsSystem => Ticket.AppId == SystemOrders.AppId;

    public int Quantity { get; set; }
    public OrderType Type { get; set; }
    public Validity Validity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? TriggerPrice { get; set; }

    public OrderStatus Status { get; private set; } = OrderStatus.Transit;
    public int FilledQuantity { get; set; }
    public decimal FilledValue { get; set; }
    public decimal? AveragePrice => FilledQuantity > 0 ? decimal.Round(FilledValue / FilledQuantity, 4) : null;
    public decimal BrokerageCharged { get; set; }
    public decimal BlockedMargin { get; set; }
    public string? RejectionCode { get; set; }
    public string? Message { get; set; }
    public int Modifications { get; set; }

    public int Unfilled => Quantity - FilledQuantity;

    public DateTimeOffset PlacedAt { get; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<OrderHistoryEntry> History { get; } = [];

    public OrderIntent Intent => new(Ticket.Side, Quantity, Type, Ticket.Product, Validity, LimitPrice, TriggerPrice);

    /// <summary>The only way an order's status changes: through the lifecycle table.</summary>
    public void MoveTo(OrderStatus status)
    {
        if (status == Status) return;
        OrderLifecycle.EnsureCanMove(OrderId, Status, status);
        Status = status;
    }
}

/// <summary>Orders the broker places itself carry this app ID and no client IP.</summary>
public static class SystemOrders
{
    public const string AppId = "RMS";
    public const string SquareOffTag = "AUTO_SQUAREOFF";
    public const string KillSwitchTag = "KILL_SWITCH";
}

/// <summary>One line of an order's history: what happened, when, and the status after it.</summary>
public sealed record OrderHistoryEntry(long Seq, DateTimeOffset At, string Event, OrderStatus Status, string? Note);

/// <summary>A net position in one instrument and product.</summary>
public sealed class PositionState
{
    public required string ClientId { get; init; }
    public required string Symbol { get; init; }
    public required Exchange Exchange { get; init; }
    public required Segment Segment { get; init; }
    public required ProductType Product { get; init; }

    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal Margin { get; set; }

    /// <summary>Booked since the last settlement.</summary>
    public decimal RealisedToday { get; set; }

    /// <summary>Charges on this position's fills since the last settlement.</summary>
    public decimal ChargesToday { get; set; }

    public int BuyQuantity { get; set; }
    public decimal BuyValue { get; set; }
    public int SellQuantity { get; set; }
    public decimal SellValue { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Settled shares in the account.</summary>
public sealed class HoldingState
{
    public required string Symbol { get; init; }
    public required Exchange Exchange { get; init; }
    public int Quantity { get; set; }
    public decimal AveragePrice { get; set; }
}

/// <summary>One fill, as the tradebook and the contract note show it.</summary>
public sealed record TradeRecord(
    string TradeId,
    string OrderId,
    string ClientId,
    string Symbol,
    Exchange Exchange,
    Segment Segment,
    OrderSide Side,
    ProductType Product,
    int Quantity,
    decimal Price,
    bool Maker,
    ChargeBreakdown Charges,
    decimal Realised,
    DateOnly TradingDate,
    DateTimeOffset At,
    string? Tag);
