using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Application.Security;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Http;

/// <summary>What one API request collected on its way through: who, from where, and where its time went.</summary>
public sealed class RequestTrace
{
    public long StartedAt { get; } = Stopwatch.GetTimestamp();
    public DateTimeOffset ReceivedAt { get; init; }
    public string? ClientIp { get; init; }
    public Principal? Principal { get; set; }
    public TimeSpan? Auth { get; set; }
    public TimeSpan? RateLimit { get; set; }
    public CommandTiming? Command { get; set; }
    public string? ErrorCode { get; set; }
    public string? OrderId { get; set; }
    public string? Body { get; set; }

    /// <summary>A timing object for the engine to fill in, attached to this request.</summary>
    public CommandTiming StartCommand() => Command = new CommandTiming();
}

public static class RequestTraceExtensions
{
    public static RequestTrace Trace(this HttpContext context)
        => context.Features.Get<RequestTrace>()
           ?? throw new InvalidOperationException("The request trace middleware is not installed.");
}

/// <summary>
/// Opens a <see cref="RequestTrace"/> for every API call, turns malformed
/// requests into the standard error body, and writes the finished trace to
/// the request log.
/// </summary>
public sealed class RequestTraceMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RequestLog _log;
    private readonly BrokerOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<RequestTraceMiddleware> _logger;

    public RequestTraceMiddleware(
        RequestDelegate next, RequestLog log, IOptions<BrokerOptions> options, IClock clock, ILogger<RequestTraceMiddleware> logger)
    {
        _next = next;
        _log = log;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/admin"))
        {
            await _next(context);
            return;
        }

        var trace = new RequestTrace
        {
            ReceivedAt = _clock.UtcNow,
            ClientIp = ResolveClientIp(context, _options.ClientIpHeader),
        };
        context.Features.Set(trace);

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            await ApiResults.Error(context, new BrokerError(ErrorCodes.InvalidRequest, Describe(ex), ErrorKind.Invalid))
                .ExecuteAsync(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError(ex, "Unhandled error on {Method} {Path}.", context.Request.Method, path);
            trace.ErrorCode = ErrorCodes.InternalError;
            await Results.Json(
                    new ErrorBody(new ErrorDetail(ErrorCodes.InternalError,
                        "The broker failed to handle the request; nothing was recorded unless the journal shows it.")),
                    statusCode: StatusCodes.Status500InternalServerError)
                .ExecuteAsync(context);
        }
        finally
        {
            _log.Write(new RequestLogEntry
            {
                At = trace.ReceivedAt,
                ClientId = trace.Principal?.ClientId,
                AppId = trace.Principal?.AppId,
                ClientIp = trace.ClientIp,
                Method = context.Request.Method,
                Path = path.Value ?? string.Empty,
                Status = context.Response.StatusCode,
                ErrorCode = trace.ErrorCode,
                OrderId = trace.OrderId,
                TotalMs = Stopwatch.GetElapsedTime(trace.StartedAt).TotalMilliseconds,
                AuthMs = trace.Auth?.TotalMilliseconds,
                RateLimitMs = trace.RateLimit?.TotalMilliseconds,
                QueueMs = trace.Command?.QueueWait.TotalMilliseconds,
                DecideMs = trace.Command?.Decide.TotalMilliseconds,
                JournalMs = trace.Command?.Journal.TotalMilliseconds,
                Body = trace.Body,
            });
        }
    }

    /// <summary>What was wrong with a body the framework could not bind, without internal type names.</summary>
    private static string Describe(BadHttpRequestException ex)
    {
        if (ex.InnerException is not JsonException json) return "The request could not be read: " + ex.Message;
        var message = System.Text.RegularExpressions.Regex.Replace(json.Message, @"'?OpenFno\.[\w.]+\.(\w+)'?", "$1");
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut > 0) message = message[..cut];
        var at = json.Path is { Length: > 1 } path ? $" (at {path})" : string.Empty;
        return $"The request body is not valid{at}: {message}";
    }

    /// <summary>
    /// The caller's IP. A proxy header is believed only when the connection
    /// comes from this machine (the tunnel or proxy); from anywhere else it
    /// could be forged, and the socket address is used.
    /// </summary>
    public static string? ResolveClientIp(HttpContext context, string? header)
    {
        var peer = context.Connection.RemoteIpAddress;
        if (!string.IsNullOrWhiteSpace(header)
            && (peer is null || IPAddress.IsLoopback(peer))
            && context.Request.Headers.TryGetValue(header, out var values)
            && Secrets.NormalizeIp(values.ToString().Split(',')[0]) is { } forwarded)
            return forwarded;

        return peer is null ? null : Secrets.NormalizeIp(peer.ToString());
    }
}
