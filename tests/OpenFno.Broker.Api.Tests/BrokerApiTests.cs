using System.Net;
using static OpenFno.Broker.Api.Tests.ApiHarness;

namespace OpenFno.Broker.Api.Tests;

public class BrokerApiTests : IDisposable
{
    private readonly ApiHarness _api = new();

    public void Dispose() => _api.Dispose();

    private static object SbinBuy(decimal price = 800m, string type = "LIMIT") => new
    {
        symbol = "NSE:SBIN-EQ",
        side = "BUY",
        quantity = 10,
        type,
        product = "MIS",
        limitPrice = type == "MARKET" ? (decimal?)null : price,
        tag = "api-test",
    };

    [Fact]
    public async Task An_order_goes_from_placement_to_the_book_to_cancellation()
    {
        var (_, token) = await _api.LoggedInClientAsync(funds: 50_000m);

        var (placed, order) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        Assert.Equal("TRANSIT", order!["status"]!.GetValue<string>());
        Assert.Equal("99999", order["algoId"]!.GetValue<string>());
        var orderId = order["orderId"]!.GetValue<string>();

        var (_, book) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders", token: token);
        Assert.Equal("OPEN", book![0]!["status"]!.GetValue<string>());

        var (_, funds) = await _api.SendAsync(HttpMethod.Get, "/api/v1/funds", token: token);
        Assert.Equal(48_400m, funds!["available"]!.GetValue<decimal>());

        var (cancelled, after) = await _api.SendAsync(HttpMethod.Delete, $"/api/v1/orders/{orderId}", token: token);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("CANCELLED", after!["status"]!.GetValue<string>());

        var (_, detail) = await _api.SendAsync(HttpMethod.Get, $"/api/v1/orders/{orderId}", token: token);
        var events = detail!["history"]!.AsArray().Select(e => e!["event"]!.GetValue<string>());
        Assert.Equal(["placed", "accepted", "cancelled"], events);
    }

    [Fact]
    public async Task Orders_from_an_address_that_is_not_whitelisted_are_refused_but_reads_work()
    {
        var (_, token) = await _api.LoggedInClientAsync();

        var (refused, error) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token, ip: "198.51.100.9");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("STATIC_IP_MISMATCH", Code(error));

        var (read, _) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders", token: token, ip: "198.51.100.9");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_nothing_but_login_answers()
    {
        var (response, body) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHENTICATED", Code(body));

        var (bad, badBody) = await _api.SendAsync(HttpMethod.Get, "/api/v1/orders", token: "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("UNAUTHENTICATED", Code(badBody));
    }

    [Fact]
    public async Task A_market_order_is_an_input_error()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        var (response, body) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(type: "MARKET"), token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MARKET_ORDER_NOT_ALLOWED", Code(body));
    }

    [Fact]
    public async Task A_risk_rejection_is_an_order_with_status_rejected()
    {
        var (_, token) = await _api.LoggedInClientAsync(funds: 1_000m);
        var (response, order) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("REJECTED", order!["status"]!.GetValue<string>());
        Assert.Equal("INSUFFICIENT_FUNDS", order["rejectionCode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"side":"BUY","quantity":10,"type":"LIMIT","product":"MIS","limitPrice":800}""")]
    [InlineData("""{"symbol":"NSE:SBIN-EQ","side":0,"quantity":10,"type":"LIMIT","product":"MIS","limitPrice":800}""")]
    [InlineData("""{"symbol":"NSE:SBIN-EQ","side":"BUY","quantity":10,"type":"LIMIT","product":"MIS","limitPrice":"eight hundred"}""")]
    [InlineData("""{"symbol":"NSE:SBIN-EQ","side":"HOLD","quantity":10,"type":"LIMIT","product":"MIS","limitPrice":800}""")]
    [InlineData("""not json""")]
    public async Task A_body_that_cannot_be_read_gets_the_standard_error(string json)
    {
        var (_, token) = await _api.LoggedInClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(IpHeader, StaticIp);

        var response = await _api.CreateClient().SendAsync(request);
        var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INVALID_REQUEST", Code(body));
        Assert.DoesNotContain("OpenFno.", body!["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_eleventh_order_operation_in_a_second_is_refused_with_retry_after()
    {
        var (_, token) = await _api.LoggedInClientAsync(funds: 1_000_000m);
        _api.FreezeClock = true;
        _api.Clock.Advance(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 10; i++)
        {
            var (ok, body) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);
            Assert.True(ok.IsSuccessStatusCode, Text(body));
        }
        var (limited, error) = await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("RATE_LIMITED", Code(error));
        Assert.Equal("1", limited.Headers.GetValues("Retry-After").Single());
    }

    [Fact]
    public async Task The_journal_shows_every_event_but_no_secret()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);

        var (_, journal) = await _api.SendAsync(HttpMethod.Get, "/api/v1/journal", token: token);
        var text = Text(journal);
        var names = journal!.AsArray().Select(e => e!["event"]!.GetValue<string>()).ToList();

        Assert.Contains("account.opened", names);
        Assert.Contains("order.placed", names);
        Assert.Contains("order.accepted", names);
        Assert.DoesNotContain("protected:", text);
        Assert.DoesNotContain("CfDJ8", text); // data-protection payload prefix
        Assert.Contains("(hidden)", text);
    }

    [Fact]
    public async Task The_request_log_shows_where_an_orders_time_went()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);

        var (_, requests) = await _api.SendAsync(HttpMethod.Get, "/api/v1/requests?limit=5", token: token);
        var place = requests!.AsArray().First(r => r!["method"]!.GetValue<string>() == "POST" && r["path"]!.GetValue<string>() == "/api/v1/orders")!;

        Assert.Equal(200, place["status"]!.GetValue<int>());
        Assert.NotNull(place["orderId"]);
        Assert.NotNull(place["authMs"]);
        Assert.NotNull(place["rateLimitMs"]);
        Assert.NotNull(place["journalMs"]);
        Assert.Contains("NSE:SBIN-EQ", place["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_logins_reach_the_back_office_request_log_without_their_body()
    {
        await _api.SendAsync(HttpMethod.Post, "/api/v1/session", new { appId = "APP-X", appSecret = "s", clientId = "OFB00001", totp = "123456" });

        var (_, requests) = await _api.SendAsync(HttpMethod.Get, "/admin/requests", admin: true);
        var login = requests!.AsArray().First(r => r!["path"]!.GetValue<string>() == "/api/v1/session")!;
        Assert.Equal(401, login["status"]!.GetValue<int>());
        Assert.Equal("INVALID_CREDENTIALS", login["errorCode"]!.GetValue<string>());
        Assert.Null(login["body"]);
    }

    [Fact]
    public async Task The_admin_api_needs_its_key()
    {
        var (response, body) = await _api.SendAsync(HttpMethod.Get, "/admin/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHENTICATED", Code(body));
    }

    [Fact]
    public async Task The_overview_reports_markets_and_todays_orders()
    {
        var (_, token) = await _api.LoggedInClientAsync();
        await _api.SendAsync(HttpMethod.Post, "/api/v1/orders", SbinBuy(), token);

        var (_, overview) = await _api.SendAsync(HttpMethod.Get, "/admin/overview", admin: true);

        var nse = overview!["exchanges"]!.AsArray().First(e => e!["exchange"]!.GetValue<string>() == "NSE")!;
        Assert.True(nse["open"]!.GetValue<bool>());
        Assert.Equal(1, overview["broker"]!["ordersToday"]!["OPEN"]!.GetValue<int>());
        Assert.Equal(6, overview["instruments"]!.GetValue<int>());
        Assert.Equal(1, overview["orderRequests"]!["samples"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_openapi_document_is_served()
    {
        var (response, document) = await _api.SendAsync(HttpMethod.Get, "/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(document!["paths"]!["/api/v1/orders"]);
    }
}
