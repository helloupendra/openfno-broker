using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenFno.Broker.Api;
using OpenFno.Broker.Api.Endpoints;
using OpenFno.Broker.Api.Hosting;
using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Journal;
using OpenFno.Broker.Application.Live;
using OpenFno.Broker.Application.Market;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Domain.Limits;
using OpenFno.Broker.Infrastructure.Calendar;
using OpenFno.Broker.Infrastructure.Hosting;
using OpenFno.Broker.Infrastructure.Instruments;
using OpenFno.Broker.Infrastructure.Market;
using OpenFno.Broker.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

var brokerOptions = builder.Configuration.GetSection(BrokerOptions.Section).Get<BrokerOptions>() ?? new BrokerOptions();
var dataDirectory = Path.GetFullPath(brokerOptions.DataDirectory, builder.Environment.ContentRootPath);

builder.Services.Configure<BrokerOptions>(builder.Configuration.GetSection(BrokerOptions.Section));
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();

// Time, market structure and prices.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(builder.Configuration.GetSection(ExchangeSimulationOptions.Section).Get<ExchangeSimulationOptions>()
                              ?? new ExchangeSimulationOptions());
builder.Services.AddSingleton(_ => CalendarLoader.Load(Path.Combine(dataDirectory, "calendar")));
builder.Services.AddSingleton<IInstrumentCatalog>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<InstrumentCatalog>>();
    var lots = InstrumentLoader.LoadCommodityLotSizes(Path.Combine(dataDirectory, "reference", "commodity-lot-sizes.json"));
    var report = InstrumentLoader.Load(Path.Combine(dataDirectory, "instruments"), lots);
    logger.LogInformation(
        "Instruments: {Count} loaded from {Files}; {Freeze} with a freeze quantity; {NoLot} commodities without a lot size. Missing: {Missing}.",
        report.Instruments.Count, string.Join(", ", report.FilesRead), report.WithFreezeQuantity,
        report.CommoditiesWithoutLotSize, report.FilesMissing.Count == 0 ? "none" : string.Join(", ", report.FilesMissing));
    if (report.Instruments.Count == 0)
        logger.LogWarning("No instruments: every order will be refused. Run scripts/fetch-instruments.sh.");
    return new InstrumentCatalog(report.Instruments);
});
builder.Services.AddSingleton<IQuoteBook, QuoteBook>();
builder.Services.AddSingleton<ChaosSettings>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<MarketSimulator>();
builder.Services.AddSingleton<RedisTickFeed>();
builder.Services.Configure<RedisFeedOptions>(builder.Configuration.GetSection(RedisFeedOptions.Section));

// Secrets and scheduling.
builder.Services.AddDataProtection()
    .SetApplicationName("openfno-broker")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<IScheduler, TimerScheduler>();

// Storage.
if (brokerOptions.Storage == StorageKind.Postgres)
{
    if (string.IsNullOrWhiteSpace(brokerOptions.ConnectionString))
        throw new InvalidOperationException("Broker:Storage is Postgres but Broker:ConnectionString is empty.");
    builder.Services.AddSingleton(NpgsqlDataSource.Create(brokerOptions.ConnectionString));
    builder.Services.AddSingleton<IJournal, PostgresJournal>();
    builder.Services.AddSingleton<PostgresRequestLogSink>();
    builder.Services.AddSingleton<IRequestLogSink>(sp => sp.GetRequiredService<PostgresRequestLogSink>());
}
else
{
    builder.Services.AddSingleton<IJournal, InMemoryJournal>();
}
builder.Services.AddSingleton(sp => new RequestLog(sp.GetService<IRequestLogSink>()));

// The broker.
builder.Services.AddSingleton<BrokerEngine>();
builder.Services.AddSingleton<RateLimiter>();

// Hosted services start in this order, all before the server takes requests.
builder.Services.AddHostedService<EngineHost>();
builder.Services.AddHostedService<MatchingService>();
builder.Services.AddHostedService<MarketClockService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RedisTickFeed>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarketSimulator>());
if (brokerOptions.Storage == StorageKind.Postgres)
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PostgresRequestLogSink>());

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseMiddleware<RequestTraceMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();

app.MapGet("/health", (BrokerEngine engine, IInstrumentCatalog instruments, IQuoteBook quotes, IOptions<BrokerOptions> options) =>
        Results.Ok(new
        {
            status = engine.IsStarted ? "ok" : "starting",
            storage = options.Value.Storage.ToString(),
            instruments = instruments.Count,
            quotes = (quotes as QuoteBook)?.Count,
        }))
    .WithTags("Health");

app.MapSessionEndpoints();
app.MapOrderEndpoints();
app.MapAccountEndpoints();
app.MapTradingEndpoints();
app.MapStreamEndpoints();
app.MapAdminEndpoints();
app.MapBackOfficeEndpoints();
app.MapWebConsole();

app.Run();

/// <summary>The entry point, visible to the integration tests' WebApplicationFactory.</summary>
public partial class Program;
