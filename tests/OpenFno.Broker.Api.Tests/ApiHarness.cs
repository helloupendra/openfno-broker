using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Market;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Security;
using OpenFno.Broker.Tests.Support;

namespace OpenFno.Broker.Api.Tests;

/// <summary>Runs acknowledgements at once, so a test sees the exchange's answer as soon as the request returns.</summary>
public sealed class InlineScheduler : IScheduler
{
    public void Schedule(TimeSpan delay, Func<CancellationToken, Task> work) => work(CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// The whole broker over HTTP, in memory, on a fake clock set to a trading
/// morning. Every call moves the clock one second on, so tests stay inside the
/// per-second rate limit unless they freeze it on purpose.
/// </summary>
public sealed class ApiHarness : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";
    public const string IpHeader = "X-Test-Client-Ip";
    public const string StaticIp = "203.0.113.7";

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "openfno-broker-tests", Guid.NewGuid().ToString("N"));

    public FakeClock Clock { get; } = new(Fixtures.TradingMorning);

    public bool FreezeClock { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        builder.UseEnvironment("Testing");
        builder.UseSetting("Broker:AdminKey", AdminKey);
        builder.UseSetting("Broker:ClientIpHeader", IpHeader);
        builder.UseSetting("Broker:DataDirectory", _dataDirectory);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IInstrumentCatalog>(new InstrumentCatalog(Fixtures.All));
            services.AddSingleton(Fixtures.Calendar());
            services.AddSingleton<IScheduler, InlineScheduler>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }

    public async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(
        HttpMethod method, string path, object? body = null, string? token = null, string? ip = StaticIp, bool admin = false)
    {
        if (!FreezeClock) Clock.Advance(TimeSpan.FromSeconds(1));

        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (ip is not null) request.Headers.Add(IpHeader, ip);
        if (admin) request.Headers.Add("X-Admin-Key", AdminKey);

        var response = await CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    public void Quote(string symbol, decimal last)
        => Services.GetRequiredService<IQuoteBook>().Update(new Quote { Symbol = symbol, LastPrice = last, At = Clock.UtcNow });

    /// <summary>Opens a funded account with an app on <see cref="StaticIp"/> and logs it in; returns the access token.</summary>
    public async Task<(string ClientId, string Token)> LoggedInClientAsync(decimal funds = 500_000m)
    {
        var (_, account) = await SendAsync(HttpMethod.Post, "/admin/accounts", new { name = "Api Trader" }, admin: true);
        var clientId = account!["clientId"]!.GetValue<string>();
        var secret = account["totpSecret"]!.GetValue<string>();
        await SendAsync(HttpMethod.Post, $"/admin/accounts/{clientId}/funds", new { amount = funds }, admin: true);
        var (_, app) = await SendAsync(HttpMethod.Post, $"/admin/accounts/{clientId}/apps", new { staticIps = new[] { StaticIp } }, admin: true);

        var code = Totp.Code(Base32.Decode(secret), Totp.StepAt(Clock.UtcNow));
        var (response, session) = await SendAsync(HttpMethod.Post, "/api/v1/session", new
        {
            appId = app!["appId"]!.GetValue<string>(),
            appSecret = app["appSecret"]!.GetValue<string>(),
            clientId,
            totp = code,
        });
        Assert.True(response.IsSuccessStatusCode, session?.ToJsonString());
        return (clientId, session!["accessToken"]!.GetValue<string>());
    }

    public static string Code(JsonNode? body) => body?["error"]?["code"]?.GetValue<string>() ?? "(no error code)";

    public static string Text(JsonNode? node) => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
}
