using Microsoft.Extensions.Options;
using Npgsql;
using OpenFno.Broker.Application.Engine;
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

/// <summary>Expires day orders whose session has closed.</summary>
public sealed class OrderExpiryService : BackgroundService
{
    private readonly BrokerEngine _engine;
    private readonly BrokerOptions _options;
    private readonly ILogger<OrderExpiryService> _logger;

    public OrderExpiryService(BrokerEngine engine, IOptions<BrokerOptions> options, ILogger<OrderExpiryService> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.ExpirySweepSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var expired = await _engine.ExpireOrdersAsync(stoppingToken);
                if (expired > 0) _logger.LogInformation("{Count} day order(s) expired.", expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Order expiry failed.");
            }
        }
    }
}
