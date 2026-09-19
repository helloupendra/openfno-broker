using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Infrastructure.Instruments;
using OpenFno.Broker.Infrastructure.Market;

namespace OpenFno.Broker.Tests.Infrastructure;

public class FyersSymbolMasterTests
{
    // Rows copied from the FYERS masters of September 2026.
    private const string NiftyFuture = "101126092968407,NIFTY 29 Sep 26 FUT,11,65,0.1,,0915-1540|1815-1915:,2026-09-09,1790676600,NSE:NIFTY26SEPFUT,10,11,68407,NIFTY,26000,-1.0,XX,101000000026000,None,0,0.0";
    private const string NiftyCall = "101126092935086,NIFTY 29 Sep 26 29200 CE,14,65,0.05,,0915-1540|1815-1915:,2026-09-09,1790676600,NSE:NIFTY26SEP29200CE,10,11,35086,NIFTY,26000,29200.0,CE,101000000026000,None,0,0.0";
    private const string Sbin = "10100000003045,STATE BANK OF INDIA,0,1,0.1,INE062A01020,0915-1530|1815-1915:,2026-09-09,,NSE:SBIN-EQ,10,10,3045,SBIN,3045,-1.0,XX,10100000003045,None,1,3.7";
    private const string Nifty50 = "101000000026000,NIFTY50-INDEX,10,0,0.05,,0915-1530|1815-1915:,2026-09-09,,NSE:NIFTY50-INDEX,10,10,26000,NIFTY,26000,-1.0,XX,101000000026000,None,0,0.0";
    private const string Crude = "1120260921565899,CRUDEOIL 21 Sep 26 FUT,30,1,1.0,,0900-2330|1815-1915:,2026-09-10,1790013600,MCX:CRUDEOIL26SEPFUT,11,20,565899,CRUDEOIL,294,-1.0,XX,1120000000294,1790013600,0,0.0";
    private const string RelianceCall = "101126092954321,RELIANCE 29 Sep 26 1400 CE,15,500,0.05,,0915-1530|1815-1915:,2026-09-09,1790676600,NSE:RELIANCE26SEP1400CE,10,11,54321,RELIANCE,2885,1400.0,CE,10100000002885,None,0,0.0";

    private static Instrument Parse(string row)
    {
        Assert.True(FyersSymbolMaster.TryParse(row, out var instrument));
        return instrument;
    }

    [Fact]
    public void Reads_an_index_future()
    {
        var future = Parse(NiftyFuture);
        Assert.Equal("NSE:NIFTY26SEPFUT", future.Symbol);
        Assert.Equal(Exchange.Nse, future.Exchange);
        Assert.Equal(Segment.Derivatives, future.Segment);
        Assert.Equal(InstrumentKind.Future, future.Kind);
        Assert.Equal(UnderlyingClass.Index, future.UnderlyingClass);
        Assert.Equal(65, future.LotSize);
        Assert.Equal(0.1m, future.TickSize);
        Assert.Equal(new DateOnly(2026, 9, 29), future.Expiry);
        Assert.Null(future.Strike);
        Assert.Equal("68407", future.ExchangeToken);
        Assert.Equal(new TimeOnly(15, 40), future.Session.Closes);
    }

    [Fact]
    public void Reads_options_with_strike_and_right()
    {
        var call = Parse(NiftyCall);
        Assert.Equal(InstrumentKind.Option, call.Kind);
        Assert.Equal(29_200m, call.Strike);
        Assert.Equal(OptionRight.Call, call.Right);

        var stockCall = Parse(RelianceCall);
        Assert.Equal(UnderlyingClass.Stock, stockCall.UnderlyingClass);
        Assert.Equal(500, stockCall.LotSize);
    }

    [Fact]
    public void Tells_equities_from_indices_in_the_cash_master()
    {
        Assert.Equal(InstrumentKind.Equity, Parse(Sbin).Kind);
        Assert.False(Parse(Nifty50).IsTradable);
        Assert.Null(Parse(Sbin).Expiry);
    }

    [Fact]
    public void Leaves_the_commodity_lot_size_unknown_because_the_master_says_one_for_everything()
    {
        var crude = Parse(Crude);
        Assert.Equal(Segment.Commodity, crude.Segment);
        Assert.Equal(UnderlyingClass.Commodity, crude.UnderlyingClass);
        Assert.Equal(0, crude.LotSize);
        Assert.Equal(new TimeOnly(23, 30), crude.Session.Closes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not,a,row")]
    [InlineData("1,USDINR,1,1,0.0025,,0900-1700,2026-09-09,1790676600,NSE:USDINR26SEPFUT,10,12,1,USDINR,1,-1.0,XX,1,None,0,0.0")]
    public void Skips_rows_it_does_not_simulate(string row) => Assert.False(FyersSymbolMaster.TryParse(row, out _));
}

public class DhanScripMasterTests
{
    private const string Sample = """
        EXCH_ID,SEGMENT,SECURITY_ID,ISIN,INSTRUMENT,UNDERLYING_SECURITY_ID,UNDERLYING_SYMBOL,SYMBOL_NAME,DISPLAY_NAME,INSTRUMENT_TYPE,SERIES,LOT_SIZE,SM_EXPIRY_DATE,STRIKE_PRICE,OPTION_TYPE,TICK_SIZE,EXPIRY_FLAG,SM_FREEZE_QTY,
        NSE,D,68407,NA,FUTIDX,26000,NIFTY,NIFTY-Sep2026-FUT,NIFTY SEP FUT,FUT,NA,65.0,2026-09-29,-0.01000,XX,10.0000,M,1756,
        MCX,M,565899,NA,FUTCOM,294,CRUDEOIL,CRUDEOIL,CRUDEOIL SEP FUT,FUTCOM,2,1.0,2026-09-21,0.00000,XX,100.0000,M,100,
        NSE,E,3045,INE062A01020,EQUITY,,SBIN,"STATE BANK OF INDIA, LTD",State Bank,ES,EQ,1.0,,,,10.0000,NA,0,
        """;

    [Fact]
    public void Reads_freeze_quantities_by_exchange_and_token()
    {
        var limits = DhanScripMaster.Parse(new StringReader(Sample));
        Assert.Equal(1756, limits[(Exchange.Nse, "68407")].FreezeQuantity);
        Assert.Null(limits[(Exchange.Nse, "3045")].FreezeQuantity);
    }

    [Fact]
    public void A_commodity_freeze_given_in_lots_becomes_units()
    {
        var limits = DhanScripMaster.Parse(new StringReader(Sample));
        Assert.True(FyersSymbolMaster.TryParse(
            "1120260921565899,CRUDEOIL 21 Sep 26 FUT,30,1,1.0,,0900-2330|1815-1915:,2026-09-10,1790013600,MCX:CRUDEOIL26SEPFUT,11,20,565899,CRUDEOIL,294,-1.0,XX,1120000000294,1790013600,0,0.0",
            out var crude));

        var enriched = InstrumentLoader.Enrich(crude, limits, new Dictionary<string, int> { ["CRUDEOIL"] = 100 });
        Assert.Equal(100, enriched.LotSize);
        Assert.Equal(10_000, enriched.FreezeQuantity);

        var unknown = InstrumentLoader.Enrich(crude, limits, new Dictionary<string, int>());
        Assert.Equal(0, unknown.LotSize);
        Assert.Null(unknown.FreezeQuantity);
    }

    [Fact]
    public void Quoted_fields_may_hold_commas()
        => Assert.Equal(["a", "b, c", "d\"e"], DhanScripMaster.SplitCsv("a,\"b, c\",\"d\"\"e\""));
}

public class RedisTickFeedTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 18, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_the_platforms_normalized_tick()
    {
        var quote = RedisTickFeed.ParseTick(
            """{"exchange":"NSE","symbol":"NSE:SBIN-EQ","exchangeTimestampUtc":"2026-09-18T04:29:59.500Z","lastTradedPrice":801.5,"bidPrice":801.45,"askPrice":801.5,"bidSize":120,"askSize":0,"close":795.2}""",
            Received);

        Assert.NotNull(quote);
        Assert.Equal("NSE:SBIN-EQ", quote.Symbol);
        Assert.Equal(801.5m, quote.LastPrice);
        Assert.Equal(801.45m, quote.Bid);
        Assert.Equal(120, quote.BidQuantity);
        Assert.Null(quote.AskQuantity);
        Assert.Null(quote.PreviousClose); // the feed's close field is not trusted as the previous close
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 4, 29, 59, 500, TimeSpan.Zero), quote.At);
    }

    [Theory]
    [InlineData("""{"symbol":"NSE:SBIN-EQ","lastTradedPrice":null}""")]
    [InlineData("""{"symbol":"NSE:SBIN-EQ","lastTradedPrice":0}""")]
    [InlineData("""{"lastTradedPrice":801.5}""")]
    [InlineData("not json")]
    public void Ignores_ticks_without_a_symbol_and_a_price(string json) => Assert.Null(RedisTickFeed.ParseTick(json, Received));

    [Fact]
    public void Falls_back_to_the_receipt_time()
        => Assert.Equal(Received, RedisTickFeed.ParseTick("""{"symbol":"NSE:SBIN-EQ","lastTradedPrice":801.5}""", Received)?.At);
}
