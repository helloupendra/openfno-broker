using System.Globalization;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Domain.Rules;

/// <summary>
/// The checks an order passes before it reaches the simulated exchange, in two
/// stages that behave differently, as they do at Indian brokers:
/// <list type="bullet">
/// <item><see cref="CheckRequest"/>: is this a valid order at all? A failure is
/// an input error and no order is created.</item>
/// <item><see cref="CheckRisk"/>: may this client place it now? A failure
/// creates the order with status REJECTED and the reason, so it shows in the
/// order book like a broker RMS rejection does.</item>
/// </list>
/// Each returns the first failing rule, in a fixed order, or null.
/// </summary>
public static class OrderRules
{
    public const int MaxTagLength = 20;

    public static Rejection? CheckRequest(
        Instrument instrument,
        OrderIntent intent,
        string? tag,
        BrokerProfile profile,
        ExchangeCalendar calendar,
        Quote? quote,
        DateTimeOffset now,
        bool enforceMarketHours = true)
    {
        return CheckInstrument(instrument, now)
            ?? (enforceMarketHours ? CheckMarketOpen(instrument, calendar, now) : null)
            ?? CheckShape(instrument, intent, profile)
            ?? CheckPrices(instrument, intent, quote)
            ?? CheckTag(tag);
    }

    public static Rejection? CheckInstrument(Instrument instrument, DateTimeOffset now)
    {
        if (!instrument.IsTradable)
            return new(ErrorCodes.InstrumentNotTradable, $"{instrument.Symbol} is an index and cannot be traded.");
        if (instrument.Expiry is { } expiry && expiry < Ist.DateOf(now))
            return new(ErrorCodes.InstrumentExpired, $"{instrument.Symbol} expired on {expiry:yyyy-MM-dd}.");
        if (instrument.LotSize <= 0)
            return new(ErrorCodes.InstrumentNotConfigured,
                $"No lot size is known for {instrument.Symbol}; it cannot be traded until the instrument master provides one.");
        return null;
    }

    public static Rejection? CheckMarketOpen(Instrument instrument, ExchangeCalendar calendar, DateTimeOffset now)
    {
        if (calendar.IsOpen(instrument, now)) return null;

        var date = Ist.DateOf(now);
        var reason = calendar.WindowFor(instrument, date) is { } window
            ? $"it trades {window} IST today"
            : calendar.HolidayOn(instrument.Exchange, date) is { } holiday
                ? $"it is closed today for {holiday.Name}"
                : "it does not trade today";
        return new(ErrorCodes.MarketClosed, $"{instrument.Exchange.ToString().ToUpperInvariant()} is closed for {instrument.Symbol}: {reason}.");
    }

    public static Rejection? CheckShape(Instrument instrument, OrderIntent intent, BrokerProfile profile)
    {
        if (intent.Quantity <= 0)
            return new(ErrorCodes.InvalidQuantity, "Quantity must be a positive number of units.");
        if (intent.Quantity % instrument.LotSize != 0)
            return new(ErrorCodes.LotSizeMultiple,
                $"Quantity {intent.Quantity} is not a multiple of the lot size {instrument.LotSize}.");
        if (instrument.FreezeQuantity is { } freeze && intent.Quantity >= freeze)
            return new(ErrorCodes.FreezeQuantity,
                $"Quantity {intent.Quantity} reaches the exchange freeze quantity {freeze} for {instrument.Symbol}; split the order.");

        var productError = intent.Product switch
        {
            ProductType.Cnc when instrument.Segment != Segment.Cash => "CNC (delivery) is only for the cash segment",
            ProductType.Nrml when instrument.Segment == Segment.Cash => "NRML is only for derivatives and commodities",
            _ => null,
        };
        if (productError is not null)
            return new(ErrorCodes.ProductNotAllowed, $"{productError}; {instrument.Symbol} is in {instrument.Segment}.");

        if (intent.IsMarketPriced && !profile.AllowMarketOrders)
            return new(ErrorCodes.MarketOrderNotAllowed,
                "Market orders are not permitted for API (algo) orders. Send a LIMIT or STOP_LIMIT order.");
        if (intent.Validity == Validity.Ioc && instrument.Segment == Segment.Commodity && !profile.AllowIocInCommodities)
            return new(ErrorCodes.IocNotAllowed, "IOC orders are not permitted for algo orders in the commodity segment.");

        return null;
    }

    public static Rejection? CheckPrices(Instrument instrument, OrderIntent intent, Quote? quote)
    {
        if (intent.IsMarketPriced)
        {
            if (intent.LimitPrice is not null)
                return new(ErrorCodes.InvalidPrice, $"A {intent.Type} order takes no limit price.");
        }
        else if (intent.LimitPrice is not { } limit || limit <= 0)
        {
            return new(ErrorCodes.InvalidPrice, $"A {intent.Type} order needs a limit price above zero.");
        }
        else if (!IsTickMultiple(limit, instrument.TickSize))
        {
            return new(ErrorCodes.TickSizeMultiple,
                $"Price {limit} is not a multiple of the tick size {instrument.TickSize}.");
        }

        if (!intent.IsStop)
        {
            return intent.TriggerPrice is null
                ? null
                : new(ErrorCodes.InvalidTriggerPrice, $"A {intent.Type} order takes no trigger price.");
        }

        if (intent.TriggerPrice is not { } trigger || trigger <= 0)
            return new(ErrorCodes.InvalidTriggerPrice, "A stop order needs a trigger price above zero.");
        if (!IsTickMultiple(trigger, instrument.TickSize))
            return new(ErrorCodes.TickSizeMultiple,
                $"Trigger price {trigger} is not a multiple of the tick size {instrument.TickSize}.");

        if (intent.LimitPrice is { } stopLimit)
        {
            if (intent.Side == OrderSide.Buy && trigger > stopLimit)
                return new(ErrorCodes.InvalidTriggerPrice,
                    $"A buy stop's trigger price ({trigger}) must be at or below its limit price ({stopLimit}).");
            if (intent.Side == OrderSide.Sell && trigger < stopLimit)
                return new(ErrorCodes.InvalidTriggerPrice,
                    $"A sell stop's trigger price ({trigger}) must be at or above its limit price ({stopLimit}).");
        }

        // A stop that the market has already passed would trigger at once; the
        // exchange refuses it. Without a quote the check cannot be made.
        if (quote is not null)
        {
            if (intent.Side == OrderSide.Buy && trigger <= quote.LastPrice)
                return new(ErrorCodes.InvalidTriggerPrice,
                    $"A buy stop's trigger price ({trigger}) must be above the last traded price ({quote.LastPrice}).");
            if (intent.Side == OrderSide.Sell && trigger >= quote.LastPrice)
                return new(ErrorCodes.InvalidTriggerPrice,
                    $"A sell stop's trigger price ({trigger}) must be below the last traded price ({quote.LastPrice}).");
        }

        return null;
    }

    public static Rejection? CheckTag(string? tag)
    {
        if (tag is null) return null;
        if (tag.Length is 0 or > MaxTagLength || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return new(ErrorCodes.InvalidTag,
                $"A tag is 1 to {MaxTagLength} characters of letters, digits, '-' and '_'.");
        return null;
    }

    /// <summary>
    /// The broker's risk checks. <paramref name="requiredMargin"/> and
    /// <paramref name="availableFunds"/> come from the caller, which owns the
    /// account; <paramref name="holdings"/> is the client's settled quantity of
    /// the instrument, used for a delivery sell. The intraday cut-off stops new
    /// orders only: an order placed before it can still be modified.
    /// </summary>
    public static Rejection? CheckRisk(
        Instrument instrument,
        OrderIntent intent,
        BrokerProfile profile,
        Quote? quote,
        DateTimeOffset now,
        decimal requiredMargin,
        decimal availableFunds,
        int holdings,
        bool isNewOrder = true)
    {
        if (isNewOrder
            && intent.Product == ProductType.Mis
            && profile.IntradayCutoff.TryGetValue(instrument.Exchange, out var cutoff)
            && Ist.TimeOf(now) >= cutoff)
            return new(ErrorCodes.IntradayCutoff,
                $"New intraday (MIS) orders are not accepted on {instrument.Exchange.ToString().ToUpperInvariant()} after {cutoff:HH\\:mm} IST.");

        if (intent.Product == ProductType.Cnc && intent.Side == OrderSide.Sell && holdings < intent.Quantity)
            return new(ErrorCodes.NoHoldings,
                $"A delivery sell of {intent.Quantity} needs that many shares in holdings; {holdings} held.");

        if (instrument.Segment == Segment.Cash
            && intent.LimitPrice is { } limit
            && (quote?.PreviousClose ?? quote?.LastPrice) is { } reference and > 0)
        {
            var distance = Math.Abs(limit - reference) / reference * 100m;
            if (distance > profile.CashPriceBandPercent)
                return new(ErrorCodes.PriceBand,
                    $"Price {limit} is {distance:0.##}% from the reference price {reference}; the band is {profile.CashPriceBandPercent}%.");
        }

        if (requiredMargin > availableFunds)
            return new(ErrorCodes.InsufficientFunds,
                $"The order needs {Money(requiredMargin)} of margin; {Money(availableFunds)} is available.");

        return null;
    }

    public static bool IsTickMultiple(decimal price, decimal tickSize)
        => tickSize <= 0 || price % tickSize == 0;

    /// <summary>Rupees with Indian digit grouping: ₹1,95,000.00.</summary>
    public static string Money(decimal amount)
    {
        var text = Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture);
        var whole = text[..^3];
        if (whole.Length > 3)
        {
            var head = whole[..^3];
            var groups = new List<string>();
            for (var end = head.Length; end > 0; end -= 2) groups.Insert(0, head[Math.Max(0, end - 2)..end]);
            whole = string.Join(",", groups) + "," + whole[^3..];
        }
        return (amount < 0 ? "-₹" : "₹") + whole + text[^3..];
    }
}
