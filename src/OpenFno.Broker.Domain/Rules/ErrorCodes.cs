namespace OpenFno.Broker.Domain.Rules;

/// <summary>A reason the broker refused something, in a form a client can branch on.</summary>
public sealed record Rejection(string Code, string Message);

/// <summary>
/// Every refusal code the broker returns. These strings are part of the API:
/// clients branch on them, so they never change meaning once published.
/// </summary>
public static class ErrorCodes
{
    // The request is not a valid order. No order is created (HTTP 400).
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NotFound = "NOT_FOUND";
    public const string UnknownSymbol = "UNKNOWN_SYMBOL";
    public const string InstrumentNotTradable = "INSTRUMENT_NOT_TRADABLE";
    public const string InstrumentExpired = "INSTRUMENT_EXPIRED";
    public const string InstrumentNotConfigured = "INSTRUMENT_NOT_CONFIGURED";
    public const string MarketClosed = "MARKET_CLOSED";
    public const string InvalidQuantity = "INVALID_QUANTITY";
    public const string LotSizeMultiple = "LOT_SIZE_MULTIPLE";
    public const string FreezeQuantity = "FREEZE_QUANTITY";
    public const string ProductNotAllowed = "PRODUCT_NOT_ALLOWED";
    public const string MarketOrderNotAllowed = "MARKET_ORDER_NOT_ALLOWED";
    public const string IocNotAllowed = "IOC_NOT_ALLOWED";
    public const string InvalidPrice = "INVALID_PRICE";
    public const string TickSizeMultiple = "TICK_SIZE_MULTIPLE";
    public const string InvalidTriggerPrice = "INVALID_TRIGGER_PRICE";
    public const string InvalidTag = "INVALID_TAG";

    // The order was created and refused by the broker's risk checks (status REJECTED).
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string NoHoldings = "NO_HOLDINGS";
    public const string PriceBand = "PRICE_BAND";
    public const string IntradayCutoff = "INTRADAY_CUTOFF";
    public const string KillSwitchActive = "KILL_SWITCH_ACTIVE";

    // The simulated exchange refused an order it had received (chaos mode).
    public const string ExchangeRejected = "EXCHANGE_REJECTED";

    // Modify and cancel.
    public const string OrderNotFound = "ORDER_NOT_FOUND";
    public const string OrderInTransit = "ORDER_IN_TRANSIT";
    public const string OrderNotModifiable = "ORDER_NOT_MODIFIABLE";
    public const string ModificationLimit = "MODIFICATION_LIMIT";

    // Access.
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string InvalidTotp = "INVALID_TOTP";
    public const string StaticIpMismatch = "STATIC_IP_MISMATCH";
    public const string StaticIpChangeLimit = "STATIC_IP_CHANGE_LIMIT";
    public const string RateLimited = "RATE_LIMITED";
    public const string DayBlocked = "DAY_BLOCKED";

    // Accounts and funds (admin).
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string UnknownProfile = "UNKNOWN_PROFILE";
    public const string InvalidAmount = "INVALID_AMOUNT";
    public const string InvalidIp = "INVALID_IP";
    public const string TooManyIps = "TOO_MANY_IPS";
    public const string AppNotFound = "APP_NOT_FOUND";

    public const string AdminDisabled = "ADMIN_DISABLED";
    public const string Unavailable = "UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";

    // Chaos mode: the broker failed on purpose.
    public const string ChaosUnavailable = "CHAOS_UNAVAILABLE";
    public const string ChaosLostResponse = "CHAOS_LOST_RESPONSE";
}
