namespace OpenFno.Broker.Domain.Orders;

public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>
/// Order types as Indian brokers name them: LIMIT, MARKET, SL (stop-limit) and
/// SL-M (stop-market). The wire uses STOP_LIMIT and STOP_MARKET.
/// </summary>
public enum OrderType
{
    Limit,
    Market,
    StopLimit,
    StopMarket,
}

/// <summary>CNC: delivery (cash only). MIS: intraday, squared off the same day. NRML: carry-forward derivatives.</summary>
public enum ProductType
{
    Cnc,
    Mis,
    Nrml,
}

public enum Validity
{
    Day,
    Ioc,
}

public enum OrderStatus
{
    /// <summary>Accepted by the broker's risk checks and on its way to the exchange.</summary>
    Transit,

    /// <summary>Resting in the exchange's book.</summary>
    Open,

    /// <summary>A stop order waiting for its trigger price.</summary>
    TriggerPending,

    PartiallyFilled,
    Filled,
    Cancelled,

    /// <summary>Refused by the broker's risk checks or by the exchange.</summary>
    Rejected,

    /// <summary>A day order still working when its session closed.</summary>
    Expired,
}
