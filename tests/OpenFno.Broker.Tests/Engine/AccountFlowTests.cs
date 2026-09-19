using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Tests.Support;
using static OpenFno.Broker.Tests.Support.EngineHarness;

namespace OpenFno.Broker.Tests.Engine;

public class AccountFlowTests
{
    [Fact]
    public async Task Accounts_get_sequential_client_ids_and_a_protected_totp_secret()
    {
        var h = await StartAsync();
        var first = (await h.Engine.OpenAccountAsync("A", "fyers")).Value;
        var second = (await h.Engine.OpenAccountAsync("B", "FYERS")).Value;

        Assert.Equal("OFB00001", first.ClientId);
        Assert.Equal("OFB00002", second.ClientId);
        var opened = (AccountOpened)(await h.Journal.ReadAsync("OFB00001", 0, 10, default))[0];
        Assert.Equal("protected:" + first.TotpSecret, opened.ProtectedTotpSecret);
    }

    [Theory]
    [InlineData("", "fyers", ErrorCodes.InvalidRequest)]
    [InlineData("A", "zerodha", ErrorCodes.UnknownProfile)]
    public async Task Opening_needs_a_name_and_a_known_profile(string name, string profile, string code)
    {
        var h = await StartAsync();
        Assert.Equal(code, (await h.Engine.OpenAccountAsync(name, profile)).Error?.Code);
    }

    [Fact]
    public async Task Funds_move_in_and_out_but_margin_in_use_cannot_leave()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 10_000m);
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m, ProductType.Cnc));

        Assert.Equal(ErrorCodes.InsufficientFunds, (await h.Engine.WithdrawFundsAsync(account.ClientId, 2_001m, null)).Error?.Code);
        var funds = (await h.Engine.WithdrawFundsAsync(account.ClientId, 2_000m, "to bank")).Value;
        Assert.Equal(0m, funds.Available);
        Assert.Equal(["PAY_IN", "PAY_OUT"], funds.Ledger.Select(l => l.Kind));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.005")]
    public async Task Amounts_are_positive_rupees_and_paise(string amount)
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 0);
        Assert.Equal(ErrorCodes.InvalidAmount, (await h.Engine.AddFundsAsync(account.ClientId, decimal.Parse(amount), null)).Error?.Code);
    }

    [Fact]
    public async Task Login_takes_app_credentials_and_a_fresh_totp_and_lasts_until_six_the_next_morning()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();

        var grant = await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), "203.0.113.7"));

        Assert.True(grant.IsSuccess);
        Assert.Equal(Ist.At(new DateOnly(2026, 9, 19), new TimeOnly(6, 0)), grant.Value.ExpiresAt);
        var principal = h.Engine.Authenticate(grant.Value.AccessToken);
        Assert.Equal(account.ClientId, principal.Value.ClientId);
        Assert.Equal(["203.0.113.7"], principal.Value.StaticIps);

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 19), new TimeOnly(6, 0));
        Assert.Equal(ErrorCodes.SessionExpired, h.Engine.Authenticate(grant.Value.AccessToken).Error?.Code);
    }

    [Fact]
    public async Task A_totp_code_logs_in_once()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var code = h.TotpNow(account);

        Assert.True((await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, code, null))).IsSuccess);
        var replay = await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, code, null));
        Assert.Equal(ErrorCodes.InvalidTotp, replay.Error?.Code);
    }

    [Theory]
    [InlineData("wrong-secret", null)]
    [InlineData(null, "OFB09999")]
    public async Task Wrong_credentials_do_not_say_which_part_was_wrong(string? secret, string? clientId)
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var result = await h.Engine.LoginAsync(new LoginCommand(
            account.AppId, secret ?? account.AppSecret, clientId ?? account.ClientId, h.TotpNow(account), null));
        Assert.Equal(ErrorCodes.InvalidCredentials, result.Error?.Code);
        Assert.Equal(ErrorKind.Unauthorized, result.Error?.Kind);
    }

    [Fact]
    public async Task A_new_login_ends_the_apps_previous_session()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var first = (await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), null))).Value;
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        var second = (await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), null))).Value;

        Assert.Equal(ErrorCodes.Unauthenticated, h.Engine.Authenticate(first.AccessToken).Error?.Code);
        Assert.True(h.Engine.Authenticate(second.AccessToken).IsSuccess);
    }

    [Fact]
    public async Task Logging_out_ends_the_session()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var grant = (await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), null))).Value;

        Assert.True((await h.Engine.LogoutAsync(h.Engine.Authenticate(grant.AccessToken).Value)).Value);
        Assert.False(h.Engine.Authenticate(grant.AccessToken).IsSuccess);
    }

    [Fact]
    public async Task Static_ips_change_once_per_calendar_week()
    {
        var h = await StartAsync(); // Friday
        var account = await h.AccountAsync();

        Assert.True((await h.Engine.ChangeStaticIpsAsync(account.ClientId, account.AppId, ["198.51.100.4"])).IsSuccess);
        var again = await h.Engine.ChangeStaticIpsAsync(account.ClientId, account.AppId, ["198.51.100.5"]);
        Assert.Equal(ErrorCodes.StaticIpChangeLimit, again.Error?.Code);
        Assert.Contains("2026-09-21", again.Error!.Message);

        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 21), new TimeOnly(9, 0)); // Monday
        Assert.True((await h.Engine.ChangeStaticIpsAsync(account.ClientId, account.AppId, ["198.51.100.5"])).IsSuccess);
    }

    [Theory]
    [InlineData(new[] { "not-an-ip" }, ErrorCodes.InvalidIp)]
    [InlineData(new[] { "198.51.100.4", "198.51.100.5" }, ErrorCodes.TooManyIps)]
    [InlineData(new string[0], ErrorCodes.InvalidIp)]
    public async Task Static_ips_are_addresses_within_the_profiles_count(string[] ips, string code)
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        Assert.Equal(code, (await h.Engine.ChangeStaticIpsAsync(account.ClientId, account.AppId, ips)).Error?.Code);
    }

    [Fact]
    public async Task Ipv4_mapped_ipv6_is_stored_as_ipv4()
    {
        var h = await StartAsync();
        var opened = (await h.Engine.OpenAccountAsync("A", "fyers")).Value;
        var app = (await h.Engine.RegisterAppAsync(opened.ClientId, ["::ffff:203.0.113.7"])).Value;
        Assert.Equal(["203.0.113.7"], app.StaticIps);
    }
}

public class ReplayTests
{
    [Fact]
    public async Task A_restart_rebuilds_the_same_accounts_orders_funds_and_sessions()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 300_000m);
        var grant = (await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), null))).Value;
        var open = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml))).Value;
        var rejected = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml))).Value;
        var cancelled = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.Scheduler.RunAllAsync();
        await h.Engine.CancelOrderAsync(account.ClientId, cancelled.OrderId);
        await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, open.OrderId, null, null, null, 24_990m, null));

        var restarted = await h.RestartAsync();

        var orders = (await restarted.GetOrdersAsync(account.ClientId, null)).Value.ToDictionary(o => o.OrderId);
        Assert.Equal(OrderStatus.Open, orders[open.OrderId].Status);
        Assert.Equal(24_990m, orders[open.OrderId].LimitPrice);
        Assert.Equal(OrderStatus.Rejected, orders[rejected.OrderId].Status);
        Assert.Equal(OrderStatus.Cancelled, orders[cancelled.OrderId].Status);
        Assert.Equal(
            (await h.Engine.GetFundsAsync(account.ClientId)).Value.Available,
            (await restarted.GetFundsAsync(account.ClientId)).Value.Available);
        Assert.True(restarted.Authenticate(grant.AccessToken).IsSuccess);

        // The next ids continue where the journal left off.
        Assert.Equal("OFB00002", (await restarted.OpenAccountAsync("Next", "fyers")).Value.ClientId);
    }

    [Fact]
    public async Task Orders_caught_in_transit_by_a_restart_are_acknowledged_on_start()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync();
        var placed = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;

        // The process stops before the exchange answers: its timer dies with it.
        h.Scheduler.Clear();
        var restarted = await h.RestartAsync();
        Assert.Equal(1, h.Scheduler.Pending);

        await h.Scheduler.RunAllAsync();
        Assert.Equal(OrderStatus.Open, (await restarted.GetOrderAsync(account.ClientId, placed.OrderId)).Value.Status);
    }

    [Fact]
    public async Task Every_event_type_survives_the_journal_round_trip()
    {
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 500_000m);
        await h.Engine.WithdrawFundsAsync(account.ClientId, 1m, null);
        await h.Engine.ChangeStaticIpsAsync(account.ClientId, account.AppId, ["203.0.113.8"]);
        var grant = (await h.Engine.LoginAsync(new LoginCommand(account.AppId, account.AppSecret, account.ClientId, h.TotpNow(account), null))).Value;
        await h.Engine.LogoutAsync(h.Engine.Authenticate(grant.AccessToken).Value);
        var order = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 10, 800m))).Value;
        await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Sell, 10, 800m, ProductType.Cnc));
        await h.Scheduler.RunAllAsync();
        await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, order.OrderId, null, null, null, 801m, null));
        await h.Engine.ModifyOrderAsync(new ModifyOrderCommand(account.ClientId, order.OrderId, 1_000_000, null, null, null, null));
        await h.Engine.CancelOrderAsync(account.ClientId, order.OrderId);
        var expiring = (await h.PlaceAsync(account, "NSE:SBIN-EQ", Limit(OrderSide.Buy, 1, 800m))).Value;
        await h.Scheduler.RunAllAsync();
        h.Clock.UtcNow = Ist.At(new DateOnly(2026, 9, 18), new TimeOnly(16, 0));
        await h.Engine.ExpireOrdersAsync();

        var events = await h.Journal.ReadAsync(account.ClientId, 0, 1000, default);
        var names = events.Select(BrokerEvent.NameOf).ToHashSet();
        Assert.Equal(14, names.Count);
        Assert.Equal(events.Select(e => e.Seq), Enumerable.Range(1, events.Count).Select(i => (long)i));
        Assert.Equal(OrderStatus.Expired, (await h.Engine.GetOrderAsync(account.ClientId, expiring.OrderId)).Value.Status);
    }
}
