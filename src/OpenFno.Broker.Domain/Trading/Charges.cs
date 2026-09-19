using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Domain.Trading;

/// <summary>What a trade costs beyond its price, line by line as a contract note shows it.</summary>
public sealed record ChargeBreakdown(
    decimal Brokerage,
    decimal TransactionTax,
    decimal ExchangeFee,
    decimal SebiFee,
    decimal StampDuty,
    decimal Gst,
    decimal Other = 0m)
{
    public static readonly ChargeBreakdown None = new(0m, 0m, 0m, 0m, 0m, 0m);

    public decimal Total => Brokerage + TransactionTax + ExchangeFee + SebiFee + StampDuty + Gst + Other;

    public ChargeBreakdown Add(ChargeBreakdown other) => new(
        Brokerage + other.Brokerage,
        TransactionTax + other.TransactionTax,
        ExchangeFee + other.ExchangeFee,
        SebiFee + other.SebiFee,
        StampDuty + other.StampDuty,
        Gst + other.Gst,
        Other + other.Other);
}

/// <summary>How charges are grouped: the statutory rates differ for each.</summary>
public enum ChargeCategory
{
    EquityDelivery,
    EquityIntraday,
    EquityFutures,
    EquityOptions,
    CommodityFutures,
    CommodityOptions,
}

/// <summary>
/// The statutory rates for one category from a date on, as percentages of
/// turnover (premium turnover for options). The transaction tax is STT for
/// equity and CTT for commodities.
/// </summary>
public sealed record StatutoryRates(
    DateOnly From,
    ChargeCategory Category,
    decimal TaxBuyPercent,
    decimal TaxSellPercent,
    decimal StampBuyPercent,
    decimal NseFeePercent,
    decimal BseFeePercent,
    decimal McxFeePercent);

/// <summary>A broker's brokerage: a flat amount per executed order, or a percentage of its turnover if that is less.</summary>
public sealed record BrokerageRule(decimal FlatPerOrder, decimal PercentOfTurnover)
{
    /// <summary>
    /// Brokerage due on one fill, given the order's turnover including this fill
    /// and what earlier fills of the same order were already charged. The cap
    /// is per order, so a partly filled order never pays more than one flat fee.
    /// </summary>
    public decimal ForFill(decimal orderTurnoverSoFar, decimal chargedSoFar)
    {
        var due = Math.Min(FlatPerOrder, orderTurnoverSoFar * PercentOfTurnover / 100m);
        return Math.Max(0m, Round(due - chargedSoFar));
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Statutory charges by date, so a trade is charged at the rates in force on
/// its trading day. Sources and the rows that are assumptions are listed in
/// docs/rules.md.
/// </summary>
public static class ChargeSchedule
{
    /// <summary>SEBI turnover fee: ₹10 per crore.</summary>
    public const decimal SebiFeePercent = 0.0001m;

    /// <summary>GST on brokerage, exchange fees and the SEBI fee.</summary>
    public const decimal GstPercent = 18m;

    private static readonly DateOnly October2024 = new(2024, 10, 1);
    private static readonly DateOnly April2026 = new(2026, 4, 1);

    private static readonly StatutoryRates[] Rates =
    [
        new(October2024, ChargeCategory.EquityDelivery, 0.1m, 0.1m, 0.015m, 0.00297m, 0.00375m, 0m),
        new(October2024, ChargeCategory.EquityIntraday, 0m, 0.025m, 0.003m, 0.00297m, 0.00375m, 0m),
        new(October2024, ChargeCategory.EquityFutures, 0m, 0.02m, 0.002m, 0.00173m, 0m, 0m),
        new(October2024, ChargeCategory.EquityOptions, 0m, 0.1m, 0.003m, 0.03503m, 0.0325m, 0m),
        new(October2024, ChargeCategory.CommodityFutures, 0m, 0.01m, 0.002m, 0m, 0m, 0.0021m),
        new(October2024, ChargeCategory.CommodityOptions, 0m, 0.05m, 0.003m, 0m, 0m, 0.0418m),

        // Union Budget 2026: STT on equity derivatives raised from 1 April 2026.
        new(April2026, ChargeCategory.EquityFutures, 0m, 0.05m, 0.002m, 0.00173m, 0m, 0m),
        new(April2026, ChargeCategory.EquityOptions, 0m, 0.15m, 0.003m, 0.03503m, 0.0325m, 0m),
    ];

    public static StatutoryRates RatesOn(ChargeCategory category, DateOnly tradingDate)
        => Rates.Where(r => r.Category == category && r.From <= tradingDate).MaxBy(r => r.From)
           ?? throw new InvalidOperationException($"No {category} rates before {tradingDate:yyyy-MM-dd}.");

    public static ChargeCategory CategoryOf(Instrument instrument, ProductType product) => instrument.Segment switch
    {
        Segment.Cash => product == ProductType.Cnc ? ChargeCategory.EquityDelivery : ChargeCategory.EquityIntraday,
        Segment.Commodity => instrument.Kind == InstrumentKind.Option ? ChargeCategory.CommodityOptions : ChargeCategory.CommodityFutures,
        _ => instrument.Kind == InstrumentKind.Option ? ChargeCategory.EquityOptions : ChargeCategory.EquityFutures,
    };

    /// <summary>The charges on one fill. <paramref name="brokerage"/> comes from the broker's rule.</summary>
    public static ChargeBreakdown For(
        Instrument instrument, ProductType product, OrderSide side, decimal turnover, decimal brokerage, DateOnly tradingDate)
    {
        var rates = RatesOn(CategoryOf(instrument, product), tradingDate);
        var taxPercent = side == OrderSide.Buy ? rates.TaxBuyPercent : rates.TaxSellPercent;
        var feePercent = instrument.Exchange switch
        {
            Exchange.Nse => rates.NseFeePercent,
            Exchange.Bse => rates.BseFeePercent,
            _ => rates.McxFeePercent,
        };

        var tax = Percent(turnover, taxPercent);
        var exchangeFee = Percent(turnover, feePercent);
        var sebiFee = Percent(turnover, SebiFeePercent);
        var stamp = side == OrderSide.Buy ? Percent(turnover, rates.StampBuyPercent) : 0m;
        var gst = Percent(brokerage + exchangeFee + sebiFee, GstPercent);
        return new ChargeBreakdown(brokerage, tax, exchangeFee, sebiFee, stamp, gst);
    }

    /// <summary>A flat fee (an auto square-off charge) with its GST.</summary>
    public static ChargeBreakdown Fee(decimal amount)
        => ChargeBreakdown.None with { Other = amount, Gst = Percent(amount, GstPercent) };

    private static decimal Percent(decimal amount, decimal percent)
        => decimal.Round(amount * percent / 100m, 2, MidpointRounding.AwayFromZero);
}
