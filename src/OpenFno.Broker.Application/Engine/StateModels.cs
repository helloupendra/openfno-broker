using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;

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

    /// <summary>Margin held by working orders.</summary>
    public decimal BlockedMargin { get; set; }

    public decimal Available => NetDeposits - BlockedMargin;

    public long LastTotpStep { get; set; }

    public List<LedgerEntry> Ledger { get; } = [];
    public Dictionary<string, AppState> Apps { get; } = new(StringComparer.Ordinal);
    public List<OrderState> Orders { get; } = [];
}

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

    public int Quantity { get; set; }
    public OrderType Type { get; set; }
    public Validity Validity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? TriggerPrice { get; set; }

    public OrderStatus Status { get; private set; } = OrderStatus.Transit;
    public int FilledQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public decimal BlockedMargin { get; set; }
    public string? RejectionCode { get; set; }
    public string? Message { get; set; }
    public int Modifications { get; set; }

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

/// <summary>One line of an order's history: what happened, when, and the status after it.</summary>
public sealed record OrderHistoryEntry(long Seq, DateTimeOffset At, string Event, OrderStatus Status, string? Note);
