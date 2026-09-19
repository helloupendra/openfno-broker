using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Live;
using OpenFno.Broker.Application.Market;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Infrastructure.Market;

namespace OpenFno.Broker.Api.Endpoints;

/// <summary>Whether an exchange's normal market is open now, and its hours today.</summary>
public sealed record ExchangeStatus(Exchange Exchange, bool Open, TradingSession? Today, string? Holiday);

public sealed record FeedStatus(int Quotes, DateTimeOffset? LatestTickAt, bool RedisConfigured, long RedisTicks, bool SimulatorRunning, bool Paused);

public sealed record EndOfDayRequest(DateOnly? TradingDate = null);

public sealed record BackOfficeOverview(
    DateTimeOffset Now,
    bool AlwaysOpen,
    IReadOnlyList<ExchangeStatus> Exchanges,
    int Instruments,
    FeedStatus Feed,
    BrokerOverview Broker,
    LatencySummary? OrderRequests);

/// <summary>
/// The read side of the back office: what the web console shows. Everything a
/// client can read about its own account, for any account, plus totals.
/// </summary>
public static class BackOfficeEndpoints
{
    /// <summary>The normal market hours shown on the overview; instruments carry their own.</summary>
    private static readonly (Exchange Exchange, TradingSession Session)[] Markets =
    [
        (Exchange.Nse, TradingSession.NseCash),
        (Exchange.Bse, TradingSession.NseCash),
        (Exchange.Mcx, new TradingSession(new TimeOnly(9, 0), new TimeOnly(23, 30))),
    ];

    public static void MapBackOfficeEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Back office").AddEndpointFilter<AdminKeyFilter>();

        admin.MapGet("overview", async (HttpContext http, BrokerEngine engine, ExchangeCalendar calendar,
                IInstrumentCatalog instruments, IQuoteBook quotes, RequestLog requests, ExchangeSimulationOptions exchange,
                RedisTickFeed redis, MarketSimulator simulator, ChaosSettings chaos, IClock clock, CancellationToken ct) =>
            {
                var overview = await engine.GetOverviewAsync(ct);
                if (!overview.IsSuccess) return ApiResults.Error(http, overview.Error!);

                var now = clock.UtcNow;
                var date = Ist.DateOf(now);
                var exchanges = Markets.Select(m =>
                {
                    var window = calendar.WindowFor(m.Exchange, m.Session, date);
                    return new ExchangeStatus(m.Exchange, window is { } w && w.Contains(Ist.TimeOf(now)), window,
                        calendar.HolidayOn(m.Exchange, date)?.Name);
                }).ToList();

                var book = quotes as QuoteBook;
                var orderLatency = LatencySummary.Of(requests.RecentAll(RequestLog.KeptOverall)
                    .Where(r => r.Method == "POST" && r.Path == "/api/v1/orders" && r.Status == StatusCodes.Status200OK)
                    .Select(r => r.TotalMs));

                var feed = new FeedStatus(book?.Count ?? 0, book?.LatestAt, redis.Configured, redis.TicksRead,
                    simulator.Status().Running, chaos.FeedPaused);
                return Results.Ok(new BackOfficeOverview(now, exchange.AlwaysOpen, exchanges, instruments.Count,
                    feed, overview.Value, orderLatency));
            })
            .WithSummary("Market, feed and today's totals");

        admin.MapGet("requests", (RequestLog log, int? limit) => Results.Ok(log.RecentAll(limit ?? 200)))
            .WithSummary("Latest API calls from every client, failed logins included");

        admin.MapGet("accounts/{clientId}/orders", async (string clientId, string? date, HttpContext http, BrokerEngine engine, CancellationToken ct) =>
            {
                DateOnly? day = null;
                if (date is not null)
                {
                    if (!DateOnly.TryParse(date, out var parsed))
                        return ApiResults.Error(http, BrokerError.Invalid(ErrorCodes.InvalidRequest, "date is yyyy-MM-dd."));
                    day = parsed;
                }
                return ApiResults.From(http, await engine.GetOrdersAsync(clientId, day, ct));
            });

        admin.MapGet("accounts/{clientId}/orders/{orderId}", async (string clientId, string orderId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetOrderAsync(clientId, orderId, ct)));

        admin.MapGet("accounts/{clientId}/funds", async (string clientId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetFundsAsync(clientId, ct)));

        admin.MapGet("accounts/{clientId}/journal", async (string clientId, long? after, int? limit, BrokerEngine engine, CancellationToken ct) =>
        {
            var events = await engine.GetJournalAsync(clientId, after ?? 0, limit ?? 500, ct);
            return Results.Ok(events.Select(ApiJson.Redact).ToList());
        });

        admin.MapGet("accounts/{clientId}/requests", (string clientId, int? limit, RequestLog log)
            => Results.Ok(log.Recent(clientId, limit ?? 200)));

        admin.MapGet("accounts/{clientId}/trades", async (string clientId, string? date, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => TradingEndpoints.ParseDate(http, date, out var day) ?? ApiResults.From(http, await engine.GetTradesAsync(clientId, day, ct)));

        admin.MapGet("accounts/{clientId}/positions", async (string clientId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetPositionsAsync(clientId, ct)));

        admin.MapGet("accounts/{clientId}/holdings", async (string clientId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetHoldingsAsync(clientId, ct)));

        admin.MapGet("accounts/{clientId}/contract-notes", async (string clientId, string? date, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => TradingEndpoints.ParseDate(http, date, out var day) ?? ApiResults.From(http, await engine.GetContractNoteAsync(clientId, day, ct)));

        admin.MapGet("accounts/{clientId}/kill-switch", async (string clientId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetKillSwitchAsync(clientId, ct)));

        admin.MapPost("accounts/{clientId}/kill-switch", async (string clientId, KillSwitchRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.SetKillSwitchAsync(clientId, request.Active, "admin", request.SquareOff, ct)))
            .WithSummary("Turn an account's kill switch on or off; the back office may turn it off at any time");

        admin.MapGet("chaos", (ChaosSettings chaos) => Results.Ok(chaos.Snapshot()))
            .WithTags("Sandbox")
            .WithSummary("The faults chaos mode injects");

        admin.MapPut("chaos", (ChaosSnapshot settings, ChaosSettings chaos) =>
            {
                chaos.Apply(settings);
                return Results.Ok(chaos.Snapshot());
            })
            .WithTags("Sandbox")
            .WithSummary("Set the faults: extra acknowledgement latency, exchange rejections, lost responses, 503s, a paused feed");

        admin.MapGet("simulator", (MarketSimulator simulator) => Results.Ok(simulator.Status()))
            .WithTags("Sandbox")
            .WithSummary("The offline market simulator");

        admin.MapPut("simulator", (SimulatorSettings settings, HttpContext http, MarketSimulator simulator)
                => ApiResults.From(http, simulator.Configure(settings)))
            .WithTags("Sandbox")
            .WithSummary("Start, stop or reconfigure the offline market: a random walk per symbol with a bid, an ask and depth");

        admin.MapPost("end-of-day", async (EndOfDayRequest request, HttpContext http, BrokerEngine engine, IClock clock, CancellationToken ct)
                => ApiResults.From(http, await engine.CloseTradingDayAsync(request.TradingDate ?? Ist.DateOf(clock.UtcNow), ct)))
            .WithTags("Sandbox")
            .WithSummary("Close a trading day now: expire orders, close intraday positions, settle expiries, futures and delivery, post P&L and charges");

        admin.MapGet("profiles", () => Results.Ok(BrokerProfiles.All.Values.Select(ProfileView.From).ToList()))
            .WithSummary("The broker profiles accounts can trade under");

        admin.MapGet("calendar", (ExchangeCalendar calendar) => Results.Ok(new
            {
                holidays = calendar.Holidays.ToList(),
                specialSessions = calendar.SpecialSessions.ToList(),
            }))
            .WithSummary("Holidays and special sessions the simulated exchanges keep");

        admin.MapGet("instruments", (string symbol, HttpContext http, IInstrumentCatalog instruments)
            => instruments.Find(symbol) is { } instrument
                ? Results.Ok(instrument)
                : ApiResults.Error(http, BrokerError.NotFound(ErrorCodes.UnknownSymbol, $"No instrument '{symbol}'.")));

        admin.MapGet("quotes", (string symbol, HttpContext http, IQuoteBook quotes)
            => quotes.Find(symbol) is { } quote
                ? Results.Ok(quote)
                : ApiResults.Error(http, BrokerError.NotFound(ErrorCodes.UnknownSymbol, $"No quote for '{symbol}' yet.")));
    }
}
