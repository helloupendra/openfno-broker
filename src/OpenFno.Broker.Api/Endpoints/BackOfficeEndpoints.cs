using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Market;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;

namespace OpenFno.Broker.Api.Endpoints;

/// <summary>Whether an exchange's normal market is open now, and its hours today.</summary>
public sealed record ExchangeStatus(Exchange Exchange, bool Open, TradingSession? Today, string? Holiday);

public sealed record FeedStatus(int Quotes, DateTimeOffset? LatestTickAt);

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
                IClock clock, CancellationToken ct) =>
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

                return Results.Ok(new BackOfficeOverview(now, exchange.AlwaysOpen, exchanges, instruments.Count,
                    new FeedStatus(book?.Count ?? 0, book?.LatestAt), overview.Value, orderLatency));
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
