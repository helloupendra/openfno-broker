using System.Reflection;
using System.Text.Json.Serialization;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Domain.Events;

/// <summary>
/// One fact the broker recorded. The journal of these events is the only
/// durable state: accounts, sessions, funds and the order book are all rebuilt
/// by replaying it, and it doubles as the audit trail a broker must keep.
/// An event carries every value that was computed when it happened (margin,
/// expiry time, rejection reason), so a replay never recomputes anything
/// against today's prices or rules. In JSON the event's name is in the
/// <c>event</c> field (<c>type</c> would clash with an order's type).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(AccountOpened), "account.opened")]
[JsonDerivedType(typeof(FundsAdded), "funds.added")]
[JsonDerivedType(typeof(FundsWithdrawn), "funds.withdrawn")]
[JsonDerivedType(typeof(AppRegistered), "app.registered")]
[JsonDerivedType(typeof(StaticIpsChanged), "app.static_ips_changed")]
[JsonDerivedType(typeof(SessionOpened), "session.opened")]
[JsonDerivedType(typeof(SessionClosed), "session.closed")]
[JsonDerivedType(typeof(OrderPlaced), "order.placed")]
[JsonDerivedType(typeof(OrderRejected), "order.rejected")]
[JsonDerivedType(typeof(OrderAccepted), "order.accepted")]
[JsonDerivedType(typeof(OrderModified), "order.modified")]
[JsonDerivedType(typeof(OrderAmendRejected), "order.amend_rejected")]
[JsonDerivedType(typeof(OrderCancelled), "order.cancelled")]
[JsonDerivedType(typeof(OrderExpired), "order.expired")]
[JsonDerivedType(typeof(OrderTriggered), "order.triggered")]
[JsonDerivedType(typeof(OrderFilled), "order.filled")]
[JsonDerivedType(typeof(OrderExchangeRejected), "order.exchange_rejected")]
[JsonDerivedType(typeof(KillSwitchChanged), "account.kill_switch")]
[JsonDerivedType(typeof(DaySettled), "account.day_settled")]
[JsonDerivedType(typeof(TradingDayClosed), "broker.day_closed")]
public abstract record BrokerEvent
{
    /// <summary>Position in the journal, gap-free from 1.</summary>
    public long Seq { get; init; }

    public DateTimeOffset At { get; init; }

    public required string ClientId { get; init; }

    private static readonly IReadOnlyDictionary<Type, string> Names = typeof(BrokerEvent)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .ToDictionary(a => a.DerivedType, a => (string)a.TypeDiscriminator!);

    /// <summary>The event's name in the journal, such as <c>order.placed</c>.</summary>
    public static string NameOf(BrokerEvent e) => Names[e.GetType()];
}

public sealed record AccountOpened : BrokerEvent
{
    public required string Name { get; init; }
    public required string ProfileId { get; init; }

    /// <summary>The TOTP secret, encrypted with the server's data-protection key.</summary>
    public required string ProtectedTotpSecret { get; init; }
}

public sealed record FundsAdded : BrokerEvent
{
    public required decimal Amount { get; init; }
    public required string Reference { get; init; }
}

public sealed record FundsWithdrawn : BrokerEvent
{
    public required decimal Amount { get; init; }
    public required string Reference { get; init; }
}

public sealed record AppRegistered : BrokerEvent
{
    public required string AppId { get; init; }

    /// <summary>SHA-256 of the app secret; the secret itself is shown once and never stored.</summary>
    public required string SecretHash { get; init; }

    public required IReadOnlyList<string> StaticIps { get; init; }
}

public sealed record StaticIpsChanged : BrokerEvent
{
    public required string AppId { get; init; }
    public required IReadOnlyList<string> StaticIps { get; init; }
}

public sealed record SessionOpened : BrokerEvent
{
    public required string AppId { get; init; }

    /// <summary>SHA-256 of the access token.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The TOTP step the login used; no code from this step or earlier is accepted again.</summary>
    public required long TotpStep { get; init; }

    public string? ClientIp { get; init; }
}

public sealed record SessionClosed : BrokerEvent
{
    public required string TokenHash { get; init; }
    public required string Reason { get; init; }
}

/// <summary>An order passed every check and was sent to the exchange (status TRANSIT).</summary>
public sealed record OrderPlaced : BrokerEvent
{
    public required OrderTicket Ticket { get; init; }
    public required decimal BlockedMargin { get; init; }
}

/// <summary>An order was refused by the broker's risk checks (status REJECTED).</summary>
public sealed record OrderRejected : BrokerEvent
{
    public required OrderTicket Ticket { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>The exchange acknowledged an order: it rests in the book (OPEN) or waits for its trigger (TRIGGER_PENDING).</summary>
public sealed record OrderAccepted : BrokerEvent
{
    public required string OrderId { get; init; }
    public required OrderStatus Status { get; init; }
}

public sealed record OrderModified : BrokerEvent
{
    public required string OrderId { get; init; }
    public required int Quantity { get; init; }
    public required OrderType Type { get; init; }
    public required Validity Validity { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? TriggerPrice { get; init; }
    public required decimal BlockedMargin { get; init; }
    public required OrderStatus Status { get; init; }
}

/// <summary>A modification or cancellation was refused; the order itself is unchanged.</summary>
public sealed record OrderAmendRejected : BrokerEvent
{
    public required string OrderId { get; init; }
    public required string Action { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record OrderCancelled : BrokerEvent
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
}

public sealed record OrderExpired : BrokerEvent
{
    public required string OrderId { get; init; }
}

/// <summary>A stop order's trigger price was reached; it now rests as a limit order.</summary>
public sealed record OrderTriggered : BrokerEvent
{
    public required string OrderId { get; init; }
    public required decimal LastPrice { get; init; }
}

/// <summary>
/// Part or all of an order traded. Carries everything the fill changed, so a
/// replay applies it without looking at prices, rates or rules again.
/// </summary>
public sealed record OrderFilled : BrokerEvent
{
    public required string OrderId { get; init; }
    public required string TradeId { get; init; }
    public required int Quantity { get; init; }
    public required decimal Price { get; init; }

    /// <summary>True when the order was resting and was filled at its own price; false when it took the market.</summary>
    public required bool Maker { get; init; }

    public required ChargeBreakdown Charges { get; init; }

    /// <summary>Margin the order still blocks for its unfilled part.</summary>
    public required decimal OrderMarginAfter { get; init; }

    /// <summary>Profit or loss this fill booked by closing part of a position.</summary>
    public required decimal Realised { get; init; }

    public required PositionSnapshot PositionAfter { get; init; }
}

/// <summary>The simulated exchange refused an order it had received (chaos mode).</summary>
public sealed record OrderExchangeRejected : BrokerEvent
{
    public required string OrderId { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>The account's kill switch was turned on or off. While on, no new order is accepted.</summary>
public sealed record KillSwitchChanged : BrokerEvent
{
    public required bool Active { get; init; }
    public DateTimeOffset? Until { get; init; }
    public required string By { get; init; }
    public required string Reason { get; init; }
}

/// <summary>An account's end-of-day settlement for one trading date.</summary>
public sealed record DaySettled : BrokerEvent
{
    public required DateOnly TradingDate { get; init; }
    public required IReadOnlyList<SettlementEntry> Entries { get; init; }

    /// <summary>Profit or loss booked during the day and by settlement, posted to the ledger.</summary>
    public required decimal Realised { get; init; }

    /// <summary>The day's charges, posted to the ledger.</summary>
    public required decimal Charges { get; init; }
}

/// <summary>The broker finished a trading date: every account is settled. The next settlement starts after it.</summary>
public sealed record TradingDayClosed : BrokerEvent
{
    public const string Broker = "BROKER";

    public required DateOnly TradingDate { get; init; }
}
