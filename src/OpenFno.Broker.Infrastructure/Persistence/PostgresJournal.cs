using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Journal;
using OpenFno.Broker.Domain.Events;

namespace OpenFno.Broker.Infrastructure.Persistence;

/// <summary>
/// The journal in Postgres: one row per event, the event itself as jsonb.
/// The sequence number is the primary key, so a second writer (another broker
/// process on the same database) fails on its first append instead of
/// interleaving its history with this one's.
/// </summary>
public sealed class PostgresJournal : IJournal
{
    private readonly NpgsqlDataSource _db;

    public PostgresJournal(NpgsqlDataSource db) => _db = db;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource db, CancellationToken cancellationToken)
    {
        await using var stream = typeof(PostgresJournal).Assembly.GetManifestResourceStream("schema.sql")
                                 ?? throw new InvalidOperationException("The schema script is not embedded.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using var command = db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(IReadOnlyList<BrokerEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;

        await using var connection = await _db.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var e in events)
        {
            var command = new NpgsqlBatchCommand(
                "insert into journal (seq, type, client_id, occurred_at, body) values ($1, $2, $3, $4, $5)");
            command.Parameters.Add(new NpgsqlParameter { Value = e.Seq });
            command.Parameters.Add(new NpgsqlParameter { Value = BrokerEvent.NameOf(e) });
            command.Parameters.Add(new NpgsqlParameter { Value = e.ClientId });
            command.Parameters.Add(new NpgsqlParameter { Value = e.At.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = JsonSerializer.Serialize(e, JournalJson.Options),
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async IAsyncEnumerable<BrokerEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand("select body::text from journal order by seq");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return Deserialize(reader.GetString(0));
    }

    public async Task<IReadOnlyList<BrokerEvent>> ReadAsync(string clientId, long afterSeq, int limit, CancellationToken cancellationToken)
    {
        await using var command = _db.CreateCommand(
            "select body::text from journal where client_id = $1 and seq > $2 order by seq limit $3");
        command.Parameters.Add(new NpgsqlParameter { Value = clientId });
        command.Parameters.Add(new NpgsqlParameter { Value = afterSeq });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });

        var events = new List<BrokerEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) events.Add(Deserialize(reader.GetString(0)));
        return events;
    }

    private static BrokerEvent Deserialize(string json)
        => JsonSerializer.Deserialize<BrokerEvent>(json, JournalJson.Options)
           ?? throw new InvalidOperationException("A journal row held no event.");
}
