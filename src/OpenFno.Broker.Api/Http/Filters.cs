using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Limits;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Http;

/// <summary>Establishes who is calling from the bearer token, or refuses the request.</summary>
public sealed class SessionFilter : IEndpointFilter
{
    private readonly BrokerEngine _engine;

    public SessionFilter(BrokerEngine engine) => _engine = engine;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var trace = http.Trace();
        var started = Stopwatch.GetTimestamp();

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..] : null;
        var principal = _engine.Authenticate(token);
        trace.Auth = Stopwatch.GetElapsedTime(started);

        if (!principal.IsSuccess) return ApiResults.Error(http, principal.Error!);
        trace.Principal = principal.Value;
        return await next(context);
    }
}

/// <summary>
/// Orders are accepted only from one of the app's whitelisted static IPs, as
/// the exchanges require for API orders. Reads work from anywhere.
/// </summary>
public sealed class StaticIpFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var trace = http.Trace();
        var principal = trace.Principal ?? throw new InvalidOperationException("The static-IP check runs after the session check.");

        if (trace.ClientIp is null || !principal.StaticIps.Contains(trace.ClientIp))
        {
            var registered = principal.StaticIps.Count == 0 ? "none" : string.Join(", ", principal.StaticIps);
            return ApiResults.Error(http, new BrokerError(ErrorCodes.StaticIpMismatch,
                $"Orders are accepted only from the app's whitelisted static IP. This request came from {trace.ClientIp ?? "an unknown address"}; registered: {registered}.",
                ErrorKind.Forbidden));
        }
        return await next(context);
    }
}

/// <summary>Counts the request against the client's rate limits, or refuses it with 429 and Retry-After.</summary>
public sealed class RateLimitFilter : IEndpointFilter
{
    private readonly RateLimiter _limiter;
    private readonly IClock _clock;
    private readonly bool _orderOperation;

    public RateLimitFilter(RateLimiter limiter, IClock clock, bool orderOperation)
    {
        _limiter = limiter;
        _clock = clock;
        _orderOperation = orderOperation;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var trace = http.Trace();
        var principal = trace.Principal ?? throw new InvalidOperationException("The rate limit runs after the session check.");

        var started = Stopwatch.GetTimestamp();
        var decision = _limiter.TryAcquire(principal.ClientId, principal.Profile.RateLimits, _orderOperation, _clock.UtcNow);
        trace.RateLimit = Stopwatch.GetElapsedTime(started);

        return decision.Allowed ? await next(context) : Refuse(http, decision);
    }

    public static IResult Refuse(HttpContext http, RateDecision decision)
    {
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString();
        var error = decision.DayBlocked
            ? new BrokerError(ErrorCodes.DayBlocked,
                "The per-minute limit was broken too many times today; API access is blocked until midnight IST.",
                ErrorKind.RateLimited)
            : new BrokerError(ErrorCodes.RateLimited, $"Rate limit reached ({decision.Limit}).", ErrorKind.RateLimited);
        return ApiResults.Error(http, error);
    }
}

/// <summary>Guards the admin API with the configured key; with no key configured the admin API is off.</summary>
public sealed class AdminKeyFilter : IEndpointFilter
{
    public const string Header = "X-Admin-Key";

    private readonly byte[]? _key;

    public AdminKeyFilter(IOptions<BrokerOptions> options)
        => _key = string.IsNullOrEmpty(options.Value.AdminKey) ? null : Encoding.UTF8.GetBytes(options.Value.AdminKey);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (_key is null)
            return ApiResults.Error(http, new BrokerError(ErrorCodes.AdminDisabled,
                "The admin API is off. Set Broker:AdminKey to turn it on.", ErrorKind.Forbidden));

        var given = Encoding.UTF8.GetBytes(http.Request.Headers[Header].ToString());
        if (!CryptographicOperations.FixedTimeEquals(given, _key))
            return ApiResults.Error(http, BrokerError.Unauthorized(ErrorCodes.Unauthenticated, $"Send the admin key in {Header}."));

        return await next(context);
    }
}

public static class FilterExtensions
{
    public static RouteHandlerBuilder RateLimited(this RouteHandlerBuilder builder, bool orderOperation = false)
        => builder.AddEndpointFilterFactory((factory, next) =>
        {
            var services = factory.ApplicationServices;
            var filter = new RateLimitFilter(
                services.GetRequiredService<RateLimiter>(), services.GetRequiredService<IClock>(), orderOperation);
            return invocation => filter.InvokeAsync(invocation, next);
        });

    public static RouteHandlerBuilder FromStaticIp(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<StaticIpFilter>();
}
