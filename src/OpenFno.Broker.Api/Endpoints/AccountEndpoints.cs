using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter<SessionFilter>();

        api.MapGet("profile", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.GetAccountAsync(http.Trace().Principal!.ClientId, ct)))
            .RateLimited()
            .WithTags("Account")
            .WithSummary("Account, broker profile and apps")
            .WithDescription("The limits this account trades under (rate limits, static IPs, session cut-off, order types) and its API apps.");

        api.MapGet("funds", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.GetFundsAsync(http.Trace().Principal!.ClientId, ct)))
            .RateLimited()
            .WithTags("Account")
            .WithSummary("Funds and ledger");

        api.MapPut("apps/{appId}/static-ips", async (string appId, StaticIpsRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.ChangeStaticIpsAsync(http.Trace().Principal!.ClientId, appId, request.StaticIps, ct)))
            .RateLimited()
            .WithTags("Account")
            .WithSummary("Change an app's static IPs")
            .WithDescription("Allowed as often as the profile says per calendar week (once, under the exchange rules).");

        api.MapGet("journal", async (HttpContext http, BrokerEngine engine, long? after, int? limit, CancellationToken ct) =>
            {
                var events = await engine.GetJournalAsync(http.Trace().Principal!.ClientId, after ?? 0, limit ?? 200, ct);
                return Results.Ok(events.Select(ApiJson.Redact).ToList());
            })
            .RateLimited()
            .WithTags("Audit")
            .WithSummary("The account's journal")
            .WithDescription("Every event recorded for the account, oldest first, after sequence number ?after. Page with the last seq you received.");

        api.MapGet("requests", (HttpContext http, RequestLog log, int? limit)
                => Results.Ok(log.Recent(http.Trace().Principal!.ClientId, limit ?? 100)))
            .RateLimited()
            .WithTags("Audit")
            .WithSummary("Recent API calls with their latency")
            .WithDescription("This account's latest calls since the broker started, newest first: status, refusal code, and milliseconds spent in authentication, rate limiting, waiting for the engine, deciding and journaling.");

        api.MapGet("instruments", (string symbol, HttpContext http, IInstrumentCatalog instruments)
                => instruments.Find(symbol) is { } instrument
                    ? Results.Ok(instrument)
                    : ApiResults.Error(http, BrokerError.NotFound(ErrorCodes.UnknownSymbol, $"No instrument '{symbol}'.")))
            .RateLimited()
            .WithTags("Market")
            .WithSummary("Instrument details: lot size, tick size, freeze quantity, session");

        api.MapGet("quotes", (string symbol, HttpContext http, IQuoteBook quotes)
                => quotes.Find(symbol) is { } quote
                    ? Results.Ok(quote)
                    : ApiResults.Error(http, BrokerError.NotFound(ErrorCodes.UnknownSymbol, $"No quote for '{symbol}' yet.")))
            .RateLimited()
            .WithTags("Market")
            .WithSummary("The latest quote the simulated exchange has");
    }
}
