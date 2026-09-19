using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Live;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Infrastructure.Market;

public sealed record SimulatedSymbol(string Symbol, decimal? StartPrice = null);

/// <summary>How the offline market moves.</summary>
/// <param name="VolatilityPercent">Standard deviation of each step, in percent of the price.</param>
/// <param name="IntervalMs">Time between steps.</param>
/// <param name="SpreadTicks">Ticks between bid and ask.</param>
/// <param name="DepthLots">Lots shown on each side of the book.</param>
public sealed record SimulatorSettings(
    bool Running,
    IReadOnlyList<SimulatedSymbol> Symbols,
    decimal VolatilityPercent = 0.05m,
    int IntervalMs = 1000,
    int SpreadTicks = 2,
    int DepthLots = 20);

public sealed record SimulatedQuote(string Symbol, decimal LastPrice, decimal? Bid, decimal? Ask);

public sealed record SimulatorStatus(
    bool Running,
    IReadOnlyList<SimulatedQuote> Symbols,
    decimal VolatilityPercent,
    int IntervalMs,
    int SpreadTicks,
    int DepthLots,
    long Steps);

/// <summary>
/// An offline market for the sandbox: a random walk per symbol, with a bid,
/// an ask and depth, written into the quote book as if the feed had sent it.
/// Orders then trigger and fill outside market hours, at a weekend, or without
/// the live feed. It is off until the back office starts it, and it should not
/// run on symbols the live feed also prices.
/// </summary>
public sealed class MarketSimulator : BackgroundService
{
    private readonly IQuoteBook _quotes;
    private readonly IInstrumentCatalog _instruments;
    private readonly IClock _clock;
    private readonly ChaosSettings _chaos;
    private readonly ILogger<MarketSimulator> _logger;

    private readonly Lock _lock = new();
    private SimulatorSettings _settings = new(false, []);
    private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);
    private long _steps;

    public MarketSimulator(IQuoteBook quotes, IInstrumentCatalog instruments, IClock clock, ChaosSettings chaos, ILogger<MarketSimulator> logger)
    {
        _quotes = quotes;
        _instruments = instruments;
        _clock = clock;
        _chaos = chaos;
        _logger = logger;
    }

    public SimulatorStatus Status()
    {
        lock (_lock)
        {
            var symbols = _prices.Keys.Order(StringComparer.Ordinal).Select(s =>
            {
                var quote = _quotes.Find(s);
                return new SimulatedQuote(s, quote?.LastPrice ?? _prices[s], quote?.Bid, quote?.Ask);
            }).ToList();
            return new SimulatorStatus(_settings.Running, symbols, _settings.VolatilityPercent, _settings.IntervalMs,
                _settings.SpreadTicks, _settings.DepthLots, _steps);
        }
    }

    /// <summary>Replaces the settings. Every symbol must exist and have a starting price, given or from its last quote.</summary>
    public Result<SimulatorStatus> Configure(SimulatorSettings settings)
    {
        if (settings.VolatilityPercent is < 0 or > 5)
            return BrokerError.Invalid(ErrorCodes.InvalidRequest, "Volatility is 0 to 5 percent per step.");
        if (settings.IntervalMs is < 100 or > 60_000)
            return BrokerError.Invalid(ErrorCodes.InvalidRequest, "The interval is 100 ms to 60 s.");
        if (settings.SpreadTicks is < 1 or > 100 || settings.DepthLots is < 1 or > 10_000)
            return BrokerError.Invalid(ErrorCodes.InvalidRequest, "The spread is 1 to 100 ticks and the depth 1 to 10,000 lots.");

        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.Symbols)
        {
            var instrument = _instruments.Find(entry.Symbol);
            if (instrument is null)
                return BrokerError.Invalid(ErrorCodes.UnknownSymbol, $"No instrument '{entry.Symbol}'.");
            var start = entry.StartPrice ?? _quotes.Find(instrument.Symbol)?.LastPrice;
            if (start is not > 0)
                return BrokerError.Invalid(ErrorCodes.InvalidPrice, $"{instrument.Symbol} has no quote yet; give it a starting price.");
            prices[instrument.Symbol] = start.Value;
        }

        lock (_lock)
        {
            _settings = settings with { Symbols = prices.Keys.Select(s => new SimulatedSymbol(s)).ToList() };
            _prices.Clear();
            foreach (var (symbol, price) in prices) _prices[symbol] = price;
        }
        foreach (var symbol in prices.Keys) Publish(symbol);
        _logger.LogInformation("Market simulator {State} for {Count} symbol(s).", settings.Running ? "running" : "stopped", prices.Count);
        return Status();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int interval;
            lock (_lock) interval = _settings.IntervalMs;
            await Task.Delay(interval, stoppingToken);

            List<string> symbols;
            lock (_lock)
            {
                if (!_settings.Running || _chaos.FeedPaused) continue;
                symbols = _prices.Keys.ToList();
                foreach (var symbol in symbols) Step(symbol);
                _steps++;
            }
            foreach (var symbol in symbols) Publish(symbol);
        }
    }

    /// <summary>One random-walk step: a normal draw scaled by the volatility, kept on the tick grid and above zero.</summary>
    private void Step(string symbol)
    {
        var tick = TickOf(symbol);
        var gaussian = Math.Sqrt(-2.0 * Math.Log(1.0 - Random.Shared.NextDouble())) * Math.Cos(2.0 * Math.PI * Random.Shared.NextDouble());
        var moved = _prices[symbol] * (1m + (decimal)gaussian * _settings.VolatilityPercent / 100m);
        _prices[symbol] = Math.Max(tick, Math.Round(moved / tick) * tick);
    }

    private void Publish(string symbol)
    {
        decimal last;
        SimulatorSettings settings;
        lock (_lock)
        {
            if (!_prices.TryGetValue(symbol, out last)) return;
            settings = _settings;
        }
        var instrument = _instruments.Find(symbol);
        var tick = TickOf(symbol);
        var half = Math.Max(1, settings.SpreadTicks / 2) * tick;
        var depth = (long)Math.Max(1, instrument?.LotSize ?? 1) * settings.DepthLots;
        _quotes.Update(new Quote
        {
            Symbol = symbol,
            LastPrice = last,
            At = _clock.UtcNow,
            Bid = Math.Max(tick, last - half),
            Ask = last + half,
            BidQuantity = depth,
            AskQuantity = depth,
        });
    }

    private decimal TickOf(string symbol) => _instruments.Find(symbol)?.TickSize is > 0 and var tick ? tick : 0.05m;
}
