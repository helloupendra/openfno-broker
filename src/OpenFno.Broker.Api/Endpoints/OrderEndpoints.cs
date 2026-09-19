using System.Globalization;
using System.Text.Json;
using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/orders").WithTags("Orders").AddEndpointFilter<SessionFilter>();

        orders.MapGet("", ListAsync)
            .RateLimited()
            .WithSummary("Order book")
            .WithDescription("Orders of one IST trading day (today unless ?date=yyyy-MM-dd), newest first.");

        orders.MapGet("{orderId}", GetAsync)
            .RateLimited()
            .WithSummary("One order with its history")
            .WithDescription("Every step the order went through, with the time of each: placed, acknowledged, modified, refused changes, cancelled, expired.");

        orders.MapPost("", PlaceAsync)
            .FromStaticIp()
            .RateLimited(orderOperation: true)
            .WithSummary("Place an order")
            .WithDescription("An invalid order is refused with 400 and no order is created. An order that fails the broker's risk checks is created with status REJECTED and the reason. A passing order is TRANSIT until the simulated exchange acknowledges it.");

        orders.MapPatch("{orderId}", ModifyAsync)
            .FromStaticIp()
            .RateLimited(orderOperation: true)
            .WithSummary("Modify a working order");

        orders.MapDelete("{orderId}", CancelAsync)
            .FromStaticIp()
            .RateLimited(orderOperation: true)
            .WithSummary("Cancel a working order");
    }

    private static async Task<IResult> ListAsync(HttpContext http, BrokerEngine engine, string? date, CancellationToken cancellationToken)
    {
        DateOnly? day = null;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return ApiResults.Error(http, BrokerError.Invalid(ErrorCodes.InvalidRequest, "date is yyyy-MM-dd."));
            day = parsed;
        }
        return ApiResults.From(http, await engine.GetOrdersAsync(http.Trace().Principal!.ClientId, day, cancellationToken));
    }

    private static async Task<IResult> GetAsync(string orderId, HttpContext http, BrokerEngine engine, CancellationToken cancellationToken)
        => ApiResults.From(http, await engine.GetOrderAsync(http.Trace().Principal!.ClientId, orderId, cancellationToken));

    private static async Task<IResult> PlaceAsync(PlaceOrderRequest request, HttpContext http, BrokerEngine engine, CancellationToken cancellationToken)
    {
        var trace = http.Trace();
        var principal = trace.Principal!;
        trace.Body = JsonSerializer.Serialize(request, ApiJson.Options);

        var result = await engine.PlaceOrderAsync(
            new PlaceOrderCommand(principal.ClientId, principal.AppId, request.Symbol, request.ToIntent(), request.Tag, trace.ClientIp),
            trace.StartCommand(),
            cancellationToken);
        if (result.IsSuccess) trace.OrderId = result.Value.OrderId;
        return ApiResults.From(http, result);
    }

    private static async Task<IResult> ModifyAsync(
        string orderId, ModifyOrderRequest request, HttpContext http, BrokerEngine engine, CancellationToken cancellationToken)
    {
        var trace = http.Trace();
        trace.Body = JsonSerializer.Serialize(request, ApiJson.Options);
        trace.OrderId = orderId;

        var command = new ModifyOrderCommand(trace.Principal!.ClientId, orderId,
            request.Quantity, request.Type, request.Validity, request.LimitPrice, request.TriggerPrice);
        return ApiResults.From(http, await engine.ModifyOrderAsync(command, trace.StartCommand(), cancellationToken));
    }

    private static async Task<IResult> CancelAsync(string orderId, HttpContext http, BrokerEngine engine, CancellationToken cancellationToken)
    {
        var trace = http.Trace();
        trace.OrderId = orderId;
        return ApiResults.From(http,
            await engine.CancelOrderAsync(trace.Principal!.ClientId, orderId, trace.StartCommand(), cancellationToken));
    }
}
