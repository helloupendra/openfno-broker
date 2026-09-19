using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Application.Journal;
using OpenFno.Broker.Application.Market;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Security;

namespace OpenFno.Broker.Tests.Support;

/// <summary>A started engine over an in-memory journal, a fake clock and a hand-cranked exchange.</summary>
public sealed class EngineHarness
{
    public FakeClock Clock { get; } = new(Fixtures.TradingMorning);
    public ManualScheduler Scheduler { get; } = new();
    public InMemoryJournal Journal { get; }
    public QuoteBook Quotes { get; } = new();
    public ExchangeSimulationOptions Options { get; } = new() { AckLatencyMs = 0, AckJitterMs = 0 };
    public BrokerEngine Engine { get; private set; } = null!;

    private EngineHarness(InMemoryJournal journal) => Journal = journal;

    public static async Task<EngineHarness> StartAsync(InMemoryJournal? journal = null)
    {
        var harness = new EngineHarness(journal ?? new InMemoryJournal());
        harness.Engine = harness.Build();
        await harness.Engine.StartAsync();
        return harness;
    }

    /// <summary>A second engine over the same journal, as after a restart.</summary>
    public async Task<BrokerEngine> RestartAsync()
    {
        var engine = Build();
        await engine.StartAsync();
        return engine;
    }

    private BrokerEngine Build() => new(
        Journal, Clock, new InstrumentCatalog(Fixtures.All), Quotes, Fixtures.Calendar(),
        new PrefixProtector(), Scheduler, Options);

    public void Quote(string symbol, decimal last, decimal? previousClose = null)
        => Quotes.Update(new Quote { Symbol = symbol, LastPrice = last, At = Clock.UtcNow, PreviousClose = previousClose });

    /// <summary>An account with money in it and an app whitelisted for 203.0.113.7.</summary>
    public async Task<TestAccount> AccountAsync(decimal funds = 100_000m)
    {
        var opened = (await Engine.OpenAccountAsync("Test Trader", "fyers")).Value;
        if (funds > 0) Assert.True((await Engine.AddFundsAsync(opened.ClientId, funds, "test")).IsSuccess);
        var app = (await Engine.RegisterAppAsync(opened.ClientId, ["203.0.113.7"])).Value;
        return new TestAccount(opened.ClientId, opened.TotpSecret, app.AppId, app.AppSecret);
    }

    public string TotpNow(TestAccount account, int stepOffset = 0)
        => Totp.Code(Base32.Decode(account.TotpSecret), Totp.StepAt(Clock.UtcNow) + stepOffset);

    public Task<Result<OrderView>> PlaceAsync(TestAccount account, string symbol, OrderIntent intent, string? tag = null)
        => Engine.PlaceOrderAsync(new PlaceOrderCommand(account.ClientId, account.AppId, symbol, intent, tag, "203.0.113.7"));

    public static OrderIntent Limit(OrderSide side, int quantity, decimal price, ProductType product = ProductType.Mis)
        => new(side, quantity, OrderType.Limit, product, Validity.Day, price, null);
}

public sealed record TestAccount(string ClientId, string TotpSecret, string AppId, string AppSecret);
