using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Live;

namespace OpenFno.Broker.Api.Endpoints;

/// <summary>
/// Live events over WebSocket, as a broker's order socket sends them: every
/// order, trade, funds and kill-switch event the moment it is recorded.
/// Browsers cannot set headers on a WebSocket, so the token (or admin key)
/// travels in the query string.
/// </summary>
public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/api/v1/stream", async (HttpContext http, BrokerEngine engine, EventHub hub, string? access_token) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return ApiResults.Error(http, BrokerError.Invalid("INVALID_REQUEST", "Connect with a WebSocket client."));
            var principal = engine.Authenticate(access_token);
            if (!principal.IsSuccess) return ApiResults.Error(http, principal.Error!);

            http.Trace().Principal = principal.Value;
            await StreamAsync(http, hub.Subscribe(principal.Value.ClientId), principal.Value.ClientId);
            return Results.Empty;
        }).WithTags("Streams").WithSummary("This account's events, live (WebSocket; ?access_token=)");

        app.Map("/admin/stream", async (HttpContext http, EventHub hub, IOptions<BrokerOptions> options, string? key) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return ApiResults.Error(http, BrokerError.Invalid("INVALID_REQUEST", "Connect with a WebSocket client."));
            var expected = options.Value.AdminKey;
            if (string.IsNullOrEmpty(expected)
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key ?? string.Empty), Encoding.UTF8.GetBytes(expected)))
                return ApiResults.Error(http, BrokerError.Unauthorized("UNAUTHENTICATED", "Pass the admin key as ?key=."));

            await StreamAsync(http, hub.Subscribe(null), null);
            return Results.Empty;
        }).WithTags("Streams").WithSummary("Every account's events, live (WebSocket; ?key=)");
    }

    private static async Task StreamAsync(HttpContext http, Subscription subscription, string? clientId)
    {
        using var _ = subscription;
        using var socket = await http.WebSockets.AcceptWebSocketAsync();

        // The stream ends when the client closes the socket or the broker stops,
        // not on the request's abort token, which some hosts fire at the upgrade.
        var lifetime = http.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);

        var receiving = Task.Run(async () =>
        {
            var buffer = new byte[256];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, ended.Token);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            finally
            {
                ended.Cancel();
            }
        }, CancellationToken.None);

        try
        {
            await SendAsync(socket, JsonSerializer.SerializeToUtf8Bytes(new { @event = "stream.connected", clientId }, ApiJson.Options), ended.Token);
            await foreach (var e in subscription.Reader.ReadAllAsync(ended.Token))
                await SendAsync(socket, JsonSerializer.SerializeToUtf8Bytes(ApiJson.Redact(e), ApiJson.Options), ended.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }
        await receiving;
    }

    private static Task SendAsync(WebSocket socket, byte[] json, CancellationToken cancellationToken)
        => socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}
