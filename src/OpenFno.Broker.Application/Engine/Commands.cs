using System.Diagnostics;
using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Application.Engine;

public sealed record PlaceOrderCommand(
    string ClientId,
    string AppId,
    string Symbol,
    OrderIntent Intent,
    string? Tag,
    string? ClientIp);

/// <summary>A modification: each field that is set replaces the order's current value.</summary>
public sealed record ModifyOrderCommand(
    string ClientId,
    string OrderId,
    int? Quantity,
    OrderType? Type,
    Validity? Validity,
    decimal? LimitPrice,
    decimal? TriggerPrice);

public sealed record LoginCommand(string AppId, string AppSecret, string ClientId, string Totp, string? ClientIp);

/// <summary>
/// Where the time of one command went inside the engine: waiting for the
/// single-writer lock, deciding (rules, margin), and writing the journal. The
/// API adds these to the request log so a client can see its own latency.
/// </summary>
public sealed class CommandTiming
{
    public TimeSpan QueueWait { get; set; }
    public TimeSpan Decide { get; set; }
    public TimeSpan Journal { get; set; }

    internal static TimeSpan Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp);
}
