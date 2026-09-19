using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Tests.Support;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Collects scheduled work so a test decides when the simulated exchange answers.</summary>
public sealed class ManualScheduler : IScheduler
{
    private readonly List<(TimeSpan Delay, Func<CancellationToken, Task> Work)> _pending = [];

    public int Pending => _pending.Count;

    public void Schedule(TimeSpan delay, Func<CancellationToken, Task> work) => _pending.Add((delay, work));

    /// <summary>Drops pending work, as a process that stops loses its timers.</summary>
    public void Clear() => _pending.Clear();

    public async Task RunAllAsync()
    {
        while (_pending.Count > 0)
        {
            var batch = _pending.ToList();
            _pending.Clear();
            foreach (var (_, work) in batch) await work(CancellationToken.None);
        }
    }
}

/// <summary>Marks secrets as protected without encrypting them: the engine's use of the protector is what is under test.</summary>
public sealed class PrefixProtector : ISecretProtector
{
    public string Protect(string plaintext) => "protected:" + plaintext;

    public string Unprotect(string protectedText)
        => protectedText.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedText["protected:".Length..]
            : throw new InvalidOperationException("Not protected by this protector.");
}

public static class Fixtures
{
    /// <summary>Friday 18 September 2026, 10:00 IST: a normal trading day.</summary>
    public static readonly DateTimeOffset TradingMorning = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(10, 0));

    public static readonly TradingSession FoSession = new(new TimeOnly(9, 15), new TimeOnly(15, 30));
    public static readonly TradingSession McxSession = new(new TimeOnly(9, 0), new TimeOnly(23, 30));

    public static readonly Instrument Sbin = new()
    {
        Symbol = "NSE:SBIN-EQ",
        Exchange = Exchange.Nse,
        Segment = Segment.Cash,
        Kind = InstrumentKind.Equity,
        Underlying = "SBIN",
        LotSize = 1,
        TickSize = 0.05m,
        Session = TradingSession.NseCash,
    };

    public static readonly Instrument NiftyIndex = new()
    {
        Symbol = "NSE:NIFTY50-INDEX",
        Exchange = Exchange.Nse,
        Segment = Segment.Cash,
        Kind = InstrumentKind.Index,
        Underlying = "NIFTY",
        LotSize = 0,
        TickSize = 0.05m,
    };

    public static readonly Instrument NiftyFuture = new()
    {
        Symbol = "NSE:NIFTY26SEPFUT",
        Exchange = Exchange.Nse,
        Segment = Segment.Derivatives,
        Kind = InstrumentKind.Future,
        Underlying = "NIFTY",
        UnderlyingClass = UnderlyingClass.Index,
        LotSize = 65,
        TickSize = 0.1m,
        FreezeQuantity = 1756,
        Expiry = new DateOnly(2026, 9, 29),
        Session = FoSession,
    };

    public static readonly Instrument NiftyCall = new()
    {
        Symbol = "NSE:NIFTY26SEP25000CE",
        Exchange = Exchange.Nse,
        Segment = Segment.Derivatives,
        Kind = InstrumentKind.Option,
        Underlying = "NIFTY",
        UnderlyingClass = UnderlyingClass.Index,
        LotSize = 65,
        TickSize = 0.05m,
        FreezeQuantity = 1756,
        Expiry = new DateOnly(2026, 9, 29),
        Strike = 25000m,
        Right = OptionRight.Call,
        Session = FoSession,
    };

    public static readonly Instrument ExpiredCall = NiftyCall with
    {
        Symbol = "NSE:NIFTY2690825000CE",
        Expiry = new DateOnly(2026, 9, 8),
    };

    public static readonly Instrument CrudeFuture = new()
    {
        Symbol = "MCX:CRUDEOIL26SEPFUT",
        Exchange = Exchange.Mcx,
        Segment = Segment.Commodity,
        Kind = InstrumentKind.Future,
        Underlying = "CRUDEOIL",
        UnderlyingClass = UnderlyingClass.Commodity,
        LotSize = 100,
        TickSize = 1m,
        FreezeQuantity = 10_000,
        Expiry = new DateOnly(2026, 9, 21),
        Session = McxSession,
    };

    public static readonly Instrument[] All = [Sbin, NiftyIndex, NiftyFuture, NiftyCall, ExpiredCall, CrudeFuture];

    public static ExchangeCalendar Calendar() => new(
        [
            new Holiday(Exchange.Nse, new DateOnly(2026, 10, 2), "Mahatma Gandhi Jayanti", Closure.FullDay, "test"),
            new Holiday(Exchange.Mcx, new DateOnly(2026, 10, 20), "Dussehra", Closure.MorningSession, "test"),
            new Holiday(Exchange.Mcx, new DateOnly(2026, 1, 1), "New Year Day", Closure.EveningSession, "test"),
        ],
        [
            new SpecialSession(Exchange.Nse, new DateOnly(2026, 2, 1), "Budget", new TimeOnly(9, 15), new TimeOnly(15, 30), "test"),
        ]);
}
