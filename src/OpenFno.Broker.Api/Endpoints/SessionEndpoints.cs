using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Limits;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

public static class SessionEndpoints
{
    /// <summary>Login attempts per caller IP, generous for a person and useless for guessing six-digit codes.</summary>
    private static readonly RateLimitPolicy LoginPolicy = new()
    {
        OrderOpsPerSecond = 0,
        RequestsPerSecond = 2,
        RequestsPerMinute = 10,
        RequestsPerDay = 500,
        MinuteBreachesAllowedPerDay = 5,
    };

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var session = app.MapGroup("/api/v1/session").WithTags("Session");

        session.MapPost("", LoginAsync)
            .WithSummary("Daily login")
            .WithDescription("Exchanges the app ID, app secret, client ID and a fresh TOTP code for an access token that lasts until the profile's daily cut-off (06:00 IST for FYERS). A new login ends the app's previous session.");

        session.MapDelete("", LogoutAsync)
            .AddEndpointFilter<SessionFilter>()
            .RateLimited()
            .WithSummary("Log out");
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, HttpContext http, BrokerEngine engine, RateLimiter limiter, IClock clock, CancellationToken cancellationToken)
    {
        var trace = http.Trace();
        var decision = limiter.TryAcquire("login:" + (trace.ClientIp ?? "unknown"), LoginPolicy, false, clock.UtcNow);
        if (!decision.Allowed) return RateLimitFilter.Refuse(http, decision);

        var result = await engine.LoginAsync(
            new LoginCommand(request.AppId, request.AppSecret, request.ClientId, request.Totp, trace.ClientIp), cancellationToken);
        if (result.IsSuccess && engine.Authenticate(result.Value.AccessToken) is { IsSuccess: true } principal)
            trace.Principal = principal.Value;
        return ApiResults.From(http, result);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, BrokerEngine engine, CancellationToken cancellationToken)
    {
        var result = await engine.LogoutAsync(http.Trace().Principal!, cancellationToken);
        return result.IsSuccess ? Results.NoContent() : ApiResults.Error(http, result.Error!);
    }
}
