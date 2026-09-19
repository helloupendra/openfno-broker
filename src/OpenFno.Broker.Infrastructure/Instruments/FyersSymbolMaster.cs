using System.Globalization;
using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Infrastructure.Instruments;

/// <summary>
/// Reads FYERS's public symbol masters (https://public.fyers.in/sym_details/NSE_FO.csv
/// and friends): headerless CSVs with 21 columns and no quoting.
/// </summary>
public static class FyersSymbolMaster
{
    public static readonly IReadOnlyList<string> Files = ["NSE_CM.csv", "NSE_FO.csv", "BSE_CM.csv", "BSE_FO.csv", "MCX_COM.csv"];

    private const int ColumnInstrumentType = 2;
    private const int ColumnLotSize = 3;
    private const int ColumnTickSize = 4;
    private const int ColumnSession = 6;
    private const int ColumnExpiry = 8;
    private const int ColumnSymbol = 9;
    private const int ColumnExchange = 10;
    private const int ColumnSegment = 11;
    private const int ColumnToken = 12;
    private const int ColumnUnderlying = 13;
    private const int ColumnStrike = 15;
    private const int ColumnOptionType = 16;
    private const int MinimumColumns = 17;

    // FYERS instrument types: in the F&O masters 11 and 14 are index futures and
    // options, 13 and 15 stock futures and options; in the cash masters 10 is an index.
    private const int StockFuture = 13;
    private const int StockOption = 15;
    private const int CashIndex = 10;

    private static readonly TradingSession McxDefault = new(new TimeOnly(9, 0), new TimeOnly(23, 30));

    public static IEnumerable<Instrument> Parse(TextReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            if (TryParse(line, out var instrument)) yield return instrument;
        }
    }

    public static bool TryParse(string line, out Instrument instrument)
    {
        instrument = null!;
        var f = line.Split(',');
        if (f.Length < MinimumColumns) return false;

        Exchange? exchange = f[ColumnExchange].Trim() switch
        {
            "10" => Exchange.Nse,
            "11" => Exchange.Mcx,
            "12" => Exchange.Bse,
            _ => null,
        };
        // Currency derivatives (segment 12) are not simulated.
        Segment? segment = f[ColumnSegment].Trim() switch
        {
            "10" => Segment.Cash,
            "11" => Segment.Derivatives,
            "20" => Segment.Commodity,
            _ => null,
        };
        var symbol = f[ColumnSymbol].Trim();
        if (exchange is null || segment is null || !symbol.Contains(':')) return false;

        int.TryParse(f[ColumnInstrumentType], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type);
        var right = f[ColumnOptionType].Trim() switch
        {
            "CE" => OptionRight.Call,
            "PE" => (OptionRight?)OptionRight.Put,
            _ => null,
        };
        var kind = segment == Segment.Cash
            ? type == CashIndex ? InstrumentKind.Index : InstrumentKind.Equity
            : right is null ? InstrumentKind.Future : InstrumentKind.Option;

        var underlyingClass = segment switch
        {
            Segment.Commodity => UnderlyingClass.Commodity,
            Segment.Derivatives => type is StockFuture or StockOption ? UnderlyingClass.Stock : UnderlyingClass.Index,
            _ => UnderlyingClass.None,
        };

        // The commodity master reports a lot size of 1 on every row, which is not
        // the contract's lot; it is left unknown for the Dhan master to supply.
        var lotSize = segment == Segment.Commodity ? 0 : ParseInt(f[ColumnLotSize]);

        instrument = new Instrument
        {
            Symbol = symbol,
            Exchange = exchange.Value,
            Segment = segment.Value,
            Kind = kind,
            Underlying = f[ColumnUnderlying].Trim(),
            UnderlyingClass = underlyingClass,
            ExchangeToken = f[ColumnToken].Trim(),
            LotSize = lotSize,
            TickSize = ParseDecimal(f[ColumnTickSize]) ?? 0.05m,
            Expiry = ParseExpiry(f[ColumnExpiry]),
            Strike = ParseDecimal(f[ColumnStrike]) is { } strike && strike > 0 ? strike : null,
            Right = right,
            Session = TradingSession.TryParse(f[ColumnSession], out var session)
                ? session
                : exchange == Exchange.Mcx ? McxDefault : TradingSession.NseCash,
        };
        return true;
    }

    private static int ParseInt(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? (int)decimal.Round(value)
            : 0;

    private static decimal? ParseDecimal(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The expiry column is Unix seconds at the contract's last trading moment; its IST date is the expiry date.</summary>
    private static DateOnly? ParseExpiry(string text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? Domain.Time.Ist.DateOf(DateTimeOffset.FromUnixTimeSeconds(seconds))
            : null;
}
