using System.Globalization;
using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

public sealed record KillSwitchRequest(bool Active, bool SquareOff = false);

/// <summary>What happened after the orders: trades, positions, holdings, contract notes, and the kill switch.</summary>
public static class TradingEndpoints
{
    public static void MapTradingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").AddEndpointFilter<SessionFilter>().WithTags("Trading");

        api.MapGet("trades", async (HttpContext http, BrokerEngine engine, string? date, CancellationToken ct) =>
                ParseDate(http, date, out var day) ?? ApiResults.From(http, await engine.GetTradesAsync(http.Trace().Principal!.ClientId, day, ct)))
            .RateLimited()
            .WithSummary("Tradebook")
            .WithDescription("Fills of one IST trading day (today unless ?date=yyyy-MM-dd), newest first, each with its charges.");

        api.MapGet("positions", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.GetPositionsAsync(http.Trace().Principal!.ClientId, ct)))
            .RateLimited()
            .WithSummary("Positions, marked at the latest prices");

        api.MapGet("holdings", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.GetHoldingsAsync(http.Trace().Principal!.ClientId, ct)))
            .RateLimited()
            .WithSummary("Settled delivery holdings");

        api.MapGet("contract-notes", async (HttpContext http, BrokerEngine engine, string? date, CancellationToken ct) =>
                ParseDate(http, date, out var day) ?? ApiResults.From(http, await engine.GetContractNoteAsync(http.Trace().Principal!.ClientId, day, ct)))
            .RateLimited()
            .WithSummary("A day's contract note: trades and charges");

        api.MapGet("kill-switch", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.GetKillSwitchAsync(http.Trace().Principal!.ClientId, ct)))
            .RateLimited()
            .WithSummary("Whether the account's kill switch is on");

        api.MapPost("kill-switch", async (KillSwitchRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.SetKillSwitchAsync(http.Trace().Principal!.ClientId, request.Active, "client", request.SquareOff, ct)))
            .RateLimited()
            .WithSummary("Turn the kill switch on (or off, from the next trading day)")
            .WithDescription("On: every working order is cancelled, open positions are closed if squareOff is true, and no order is accepted until the next trading day.");
    }

    public static IResult? ParseDate(HttpContext http, string? date, out DateOnly? day)
    {
        day = null;
        if (date is null) return null;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return ApiResults.Error(http, BrokerError.Invalid(ErrorCodes.InvalidRequest, "date is yyyy-MM-dd."));
        day = parsed;
        return null;
    }
}
