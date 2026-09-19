using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;
using static OpenFno.Broker.Api.Tests.ApiHarness;

namespace OpenFno.Broker.Api.Tests;

public class TradingApiTests : IDisposable
{
    private readonly ApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    private static object Buy(string symbol, int quantity, decimal price, string product = "MIS") => new
    {
        symbol,
        side = "BUY",
        quantity,
        type = "LIMIT",
        product,
        limitPrice = price,
    };

    private Task SetQuoteAsync(string symbol, decimal last)
        => _api.SendAsync(HttpMethod.Put, "/admin/quotes", new { symbol, lastPrice = last, bid = last - 0.05m, ask = last }, admin: true);

    private async Task<JsonNode?> WaitForOrderAsync(string token, string orderId, string status)
    {
        for (var i = 0; i < 50; i++)
        {
            var (_, order) = await _api.SendAsync(HttpMethod.Get, $"/api/v1/orders/{orderId}", token: token);
            if (order?["status"]?.GetValue<string>() == status) return order;
            await Task.Delay(40);
        }
        return null;
    }

    [Fact]
    public async Task A_marketable_order_fills_and_shows_in_trades_positions_and_funds()
    {
        var (_, token) = await _api.LoggedInClientAsync(funds: 50_000m);
        await SetQuoteAsync("NSE:SBIN-EQ", 800m);

        var (_, order) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 10, 800.5m), token);
        var filled = await WaitForOrderAsync(token, order!["orderId"]!.GetValue<string>(), "FILLED");
        Assert.NotNull(filled);
        Assert.Equal(800m, filled!["averagePrice"]!.GetValue<decimal>());

        var (_, trades) = await _api.SendAsync(HttpMethod.Get, "/api/v1/trades", token: token);
        Assert.Single(trades!.AsArray());
        var (_, positions) = await _api.SendAsync(HttpMethod.Get, "/api/v1/positions", token: token);
        Assert.Equal(10, positions![0]!["quantity"]!.GetValue<int>());
        var (_, funds) = await _api.SendAsync(HttpMethod.Get, "/api/v1/funds", token: token);
        Assert.Equal(1_600m, funds!["positionMargin"]!.GetValue<decimal>());
        var (_, note) = await _api.SendAsync(HttpMethod.Get, "/api/v1/contract-notes", token: token);
        Assert.Equal(8_000m, note!["buyValue"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_resting_order_fills_when_a_new_quote_crosses_it()
    {
        var (_, token) = await _api.LoggedInClientAsync(funds: 50_000m);
        await SetQuoteAsync("NSE:SBIN-EQ", 805m);
        var (_, order) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 10, 800m), token);
        var orderId = order!["orderId"]!.GetValue<string>();
        Assert.NotNull(await WaitForOrderAsync(token, orderId, "OPEN"));

        await SetQuoteAsync("NSE:SBIN-EQ", 799.5m);   // the matching service picks this up
        Assert.NotNull(await WaitForOrderAsync(token, orderId, "FILLED"));
    }

    [Fact]
    public async Task A_lost_response_still_places_the_order()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Put, "/admin/chaos", new
        {
            extraAckLatencyMs = 0, exchangeRejectPercent = 0, lostResponsePercent = 100, unavailablePercent = 0, feedPaused = false,
        }, admin: true);

        var (response, body) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 1, 800m), token);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("CHAOS_LOST_RESPONSE", Code(body));

        var (_, book) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders", token: token);
        Assert.Single(book!.AsArray());
    }

    [Fact]
    public async Task An_unavailable_broker_does_nothing()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Put, "/admin/chaos", new
        {
            extraAckLatencyMs = 0, exchangeRejectPercent = 0, lostResponsePercent = 0, unavailablePercent = 100, feedPaused = false,
        }, admin: true);

        var (response, body) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 1, 800m), token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CHAOS_UNAVAILABLE", Code(body));
        var (_, book) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders", token: token);
        Assert.Empty(book!.AsArray());
    }

    [Fact]
    public async Task The_kill_switch_refuses_orders_and_the_client_cannot_lift_it()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        var (_, on) = await _api.SendAsync(HttpMethod.Post, "/api/v1/kill-switch", new { active = true }, token);
        Assert.True(on!["active"]!.GetValue<bool>());

        var (_, order) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 1, 800m), token);
        Assert.Equal("KILL_SWITCH_ACTIVE", order!["rejectionCode"]!.GetValue<string>());

        var (off, error) = await _api.SendAsync(HttpMethod.Post, "/api/v1/kill-switch", new { active = false }, token);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("KILL_SWITCH_ACTIVE", Code(error));
    }

    [Fact]
    public async Task The_stream_sends_the_accounts_events_as_they_happen()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        var socket = await _api.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri($"ws://localhost/api/v1/stream?access_token={Uri.EscapeDataString(token)}"), CancellationToken.None);

        var hello = await ReceiveAsync(socket);
        Assert.Equal("stream.connected", hello!["event"]!.GetValue<string>());

        await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 1, 800m), token);
        var placed = await ReceiveAsync(socket);
        var accepted = await ReceiveAsync(socket);
        Assert.Equal("order.placed", placed!["event"]!.GetValue<string>());
        Assert.Equal("order.accepted", accepted!["event"]!.GetValue<string>());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task The_stream_refuses_a_bad_token()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => _api.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/v1/stream?access_token=nope"), CancellationToken.None));
    }

    [Fact]
    public async Task The_simulator_checks_its_symbols_and_prices()
    {
        var (bad, error) = await _api.SendAsync(HttpMethod.Put, "/admin/simulator",
            new { running = true, symbols = new[] { new { symbol = "NSE:NOPE-EQ" } } }, admin: true);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("UNKNOWN_SYMBOL", Code(error));

        var (ok, status) = await _api.SendAsync(HttpMethod.Put, "/admin/simulator",
            new { running = true, symbols = new[] { new { symbol = "NSE:SBIN-EQ", startPrice = 800m } }, intervalMs = 100 }, admin: true);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(status!["running"]!.GetValue<bool>());

        var (_, quote) = await _api.SendAsync(HttpMethod.Get, "/admin/quotes?symbol=NSE:SBIN-EQ", admin: true);
        Assert.Equal(800m, quote!["lastPrice"]!.GetValue<decimal>());
        Assert.NotNull(quote["bid"]);

        await _api.SendAsync(HttpMethod.Put, "/admin/simulator", new { running = false, symbols = Array.Empty<object>() }, admin: true);
    }

    [Fact]
    public async Task The_back_office_can_close_the_day()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", Buy("NSE:SBIN-EQ", 1, 700m), token);

        var (response, report) = await _api.SendAsync(HttpMethod.Post, "/admin/end-of-day", new { }, admin: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, report!["ordersExpired"]!.GetValue<int>());

        var (_, overview) = await _api.SendAsync(HttpMethod.Get, "/admin/overview", admin: true);
        Assert.Equal("2026-09-18", overview!["broker"]!["lastClosedDate"]!.GetValue<string>());
    }

    private static async Task<JsonNode?> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var text = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, timeout.Token);
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return JsonNode.Parse(text.ToString());
    }
}
