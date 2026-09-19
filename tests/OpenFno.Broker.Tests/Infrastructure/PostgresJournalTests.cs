using Npgsql;
using OpenFno.Broker.Application.Requests;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Infrastructure.Persistence;
using OpenFno.Broker.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using static OpenFno.Broker.Tests.Support.EngineHarness;

namespace OpenFno.Broker.Tests.Infrastructure;

/// <summary>
/// Runs against a real Postgres when BROKER_TEST_POSTGRES holds a connection
/// string to an empty, throwaway database (CI starts one); otherwise each test
/// returns at once. The tables are dropped first, so never point it at data
/// you want to keep.
/// </summary>
public class PostgresJournalTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BROKER_TEST_POSTGRES");

    private static string ToJson(BrokerEvent e) => System.Text.Json.JsonSerializer.Serialize(e, Application.Journal.JournalJson.Options);

    private static async Task<NpgsqlDataSource?> FreshDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return null;
        var db = NpgsqlDataSource.Create(ConnectionString);
        await using (var drop = db.CreateCommand("drop table if exists journal; drop table if exists request_log;"))
            await drop.ExecuteNonQueryAsync();
        await PostgresJournal.EnsureSchemaAsync(db, default);
        await PostgresJournal.EnsureSchemaAsync(db, default); // twice: the schema script is idempotent
        return db;
    }

    [Fact]
    public async Task Events_written_by_the_engine_replay_from_postgres()
    {
        await using var db = await FreshDatabaseAsync();
        if (db is null) return;

        // Write through a real engine, then copy the in-memory history into Postgres
        // and replay it from there.
        var h = await StartAsync();
        var account = await h.AccountAsync(funds: 300_000m);
        var order = (await h.PlaceAsync(account, "NSE:NIFTY26SEPFUT", Limit(OrderSide.Buy, 65, 25_000m, ProductType.Nrml))).Value;
        await h.Scheduler.RunAllAsync();
        await h.Engine.ModifyOrderAsync(new(account.ClientId, order.OrderId, null, OrderType.StopLimit, null, 25_110m, 25_100m));

        var journal = new PostgresJournal(db);
        var events = new List<BrokerEvent>();
        await foreach (var e in h.Journal.ReadAllAsync(default)) events.Add(e);
        await journal.AppendAsync(events, default);

        var replayed = new List<BrokerEvent>();
        await foreach (var e in journal.ReadAllAsync(default)) replayed.Add(e);

        // Compared as JSON: records holding lists compare those lists by reference.
        Assert.Equal(events.Select(ToJson), replayed.Select(ToJson));
        var mine = await journal.ReadAsync(account.ClientId, afterSeq: 2, limit: 3, default);
        Assert.Equal([3L, 4L, 5L], mine.Select(e => e.Seq));
    }

    [Fact]
    public async Task A_second_writer_with_the_same_sequence_numbers_is_refused()
    {
        await using var db = await FreshDatabaseAsync();
        if (db is null) return;

        var journal = new PostgresJournal(db);
        var first = new FundsAdded { Seq = 1, At = Fixtures.TradingMorning, ClientId = "OFB00001", Amount = 1m, Reference = "a" };
        await journal.AppendAsync([first], default);

        await Assert.ThrowsAsync<PostgresException>(() => journal.AppendAsync([first with { Reference = "b" }], default));
    }

    [Fact]
    public async Task Request_log_entries_reach_the_table()
    {
        await using var db = await FreshDatabaseAsync();
        if (db is null) return;

        var sink = new PostgresRequestLogSink(db, NullLogger<PostgresRequestLogSink>.Instance);
        await sink.StartAsync(default);
        sink.Enqueue(new RequestLogEntry
        {
            At = Fixtures.TradingMorning, ClientId = "OFB00001", Method = "POST", Path = "/api/v1/orders",
            Status = 200, TotalMs = 3.2, AuthMs = null, JournalMs = 1.1,
        });
        sink.Enqueue(new RequestLogEntry
        {
            At = Fixtures.TradingMorning, Method = "POST", Path = "/api/v1/session", Status = 401, ErrorCode = "INVALID_TOTP", TotalMs = 1,
        });

        for (var i = 0; i < 50; i++)
        {
            await using var count = db.CreateCommand("select count(*) from request_log");
            if ((long)(await count.ExecuteScalarAsync())! == 2) break;
            await Task.Delay(100);
        }
        await sink.StopAsync(default);

        await using var check = db.CreateCommand("select count(*), count(auth_ms), max(journal_ms) from request_log");
        await using var reader = await check.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(1.1, reader.GetDouble(2));
    }
}
