using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Infrastructure.Persistence;

namespace OpenFno.Broker.Api.Hosting;

/// <summary>
/// Prepares storage and replays the journal before the server takes requests
/// (hosted services start before the web server does).
/// </summary>
public sealed class EngineHost : IHostedService
{
    private readonly BrokerEngine _engine;
    private readonly IServiceProvider _services;
    private readonly ILogger<EngineHost> _logger;

    public EngineHost(BrokerEngine engine, IServiceProvider services, ILogger<EngineHost> logger)
    {
        _engine = engine;
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_services.GetService<NpgsqlDataSource>() is { } db)
            await PostgresJournal.EnsureSchemaAsync(db, cancellationToken);

        var started = DateTimeOffset.UtcNow;
        var replayed = await _engine.StartAsync(cancellationToken);
        _logger.LogInformation("Broker engine ready: {Events} journal events replayed in {Ms:0} ms.",
            replayed, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The broker's clock: every few seconds it expires day orders whose session
/// has closed, squares off intraday positions past the square-off time, and
/// settles the trading day once it is over.
/// </summary>
public sealed class MarketClockService : BackgroundService
{
    private readonly BrokerEngine _engine;
    private readonly BrokerOptions _options;
    private readonly ILogger<MarketClockService> _logger;

    public MarketClockService(BrokerEngine engine, IOptions<BrokerOptions> options, ILogger<MarketClockService> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.ClockSweepSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await _engine.ExpireOrdersAsync(stoppingToken);
                if (expired > 0) _logger.LogInformation("{Count} day order(s) expired.", expired);

                var squaredOff = await _engine.SquareOffIntradayAsync(stoppingToken);
                if (squaredOff > 0) _logger.LogInformation("Auto square-off: {Count} order(s) cancelled or position(s) closed.", squaredOff);

                if (await _engine.CloseTradingDayIfDueAsync(stoppingToken) is { } closed)
                    _logger.LogInformation("Trading day {Date} settled: {Orders} order(s) expired, {Accounts} account(s) settled.",
                        closed.TradingDate, closed.OrdersExpired, closed.AccountsSettled);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The market clock sweep failed.");
            }
        }
    }
}

/// <summary>
/// Matches working orders when their symbol's quote changes. Ticks for
/// symbols without working orders cost one dictionary lookup; bursts for one
/// symbol collapse into a single match.
/// </summary>
public sealed class MatchingService : BackgroundService
{
    private readonly BrokerEngine _engine;
    private readonly IQuoteBook _quotes;
    private readonly ILogger<MatchingService> _logger;
    private readonly Channel<string> _due = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);

    public MatchingService(BrokerEngine engine, IQuoteBook quotes, ILogger<MatchingService> logger)
    {
        _engine = engine;
        _quotes = quotes;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _quotes.Updated += OnQuote;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _quotes.Updated -= OnQuote;
        return base.StopAsync(cancellationToken);
    }

    private void OnQuote(Quote quote)
    {
        if (_engine.HasLiveOrders(quote.Symbol) && _queued.TryAdd(quote.Symbol, 0))
            _due.Writer.TryWrite(quote.Symbol);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var symbol in _due.Reader.ReadAllAsync(stoppingToken))
        {
            _queued.TryRemove(symbol, out _);
            try
            {
                await _engine.MatchSymbolAsync(symbol, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Matching {Symbol} failed.", symbol);
            }
        }
    }
}
