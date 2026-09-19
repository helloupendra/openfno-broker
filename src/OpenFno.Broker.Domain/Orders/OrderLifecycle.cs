namespace OpenFno.Broker.Domain.Orders;

/// <summary>
/// The order state machine. Every status change the engine makes is checked
/// against this table, so an impossible history (a fill after a cancel, a
/// rejected order coming back to life) fails loudly instead of being recorded.
/// </summary>
public static class OrderLifecycle
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Next = new()
    {
        [OrderStatus.Transit] =
        [
            OrderStatus.Open, OrderStatus.TriggerPending, OrderStatus.PartiallyFilled,
            OrderStatus.Filled, OrderStatus.Cancelled, OrderStatus.Rejected,
        ],
        // A modification may turn a limit order into a stop order and back.
        [OrderStatus.Open] =
        [
            OrderStatus.TriggerPending, OrderStatus.PartiallyFilled, OrderStatus.Filled,
            OrderStatus.Cancelled, OrderStatus.Expired,
        ],
        [OrderStatus.TriggerPending] =
        [
            OrderStatus.Open, OrderStatus.PartiallyFilled, OrderStatus.Filled,
            OrderStatus.Cancelled, OrderStatus.Expired,
        ],
        [OrderStatus.PartiallyFilled] =
        [
            OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.Cancelled, OrderStatus.Expired,
        ],
        [OrderStatus.Filled] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Rejected] = [],
        [OrderStatus.Expired] = [],
    };

    public static bool CanMove(OrderStatus from, OrderStatus to) => Next[from].Contains(to);

    public static bool IsTerminal(OrderStatus status) => Next[status].Length == 0;

    /// <summary>Resting at the exchange and able to fill, be modified or be cancelled.</summary>
    public static bool IsWorking(OrderStatus status)
        => status is OrderStatus.Open or OrderStatus.TriggerPending or OrderStatus.PartiallyFilled;

    public static void EnsureCanMove(string orderId, OrderStatus from, OrderStatus to)
    {
        if (!CanMove(from, to))
            throw new InvalidOperationException($"Order {orderId} cannot move from {from} to {to}.");
    }
}
