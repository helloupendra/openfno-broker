using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Domain.Events;

namespace OpenFno.Broker.Application.Journal;

/// <summary>
/// A journal that lives only as long as the process: for tests and for a quick
/// local run without a database. Events are stored serialized, so everything
/// that passes here also survives the round trip a real journal makes.
/// </summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly List<(long Seq, string ClientId, string Json)> _rows = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get { lock (_lock) return _rows.Count; }
    }

    public Task AppendAsync(IReadOnlyList<BrokerEvent> events, CancellationToken cancellationToken)
    {
        var rows = events.Select(e => (e.Seq, e.ClientId, JsonSerializer.Serialize(e, JournalJson.Options))).ToList();
        lock (_lock)
        {
            var last = _rows.Count == 0 ? 0 : _rows[^1].Seq;
            if (rows.Count > 0 && rows[0].Seq != last + 1)
                throw new InvalidOperationException($"Journal append out of order: expected {last + 1}, got {rows[0].Seq}.");
            _rows.AddRange(rows);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BrokerEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        List<string> snapshot;
        lock (_lock) snapshot = _rows.Select(r => r.Json).ToList();
        foreach (var json in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Deserialize(json);
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<BrokerEvent>> ReadAsync(string clientId, long afterSeq, int limit, CancellationToken cancellationToken)
    {
        List<string> matches;
        lock (_lock)
        {
            matches = _rows.Where(r => r.Seq > afterSeq && r.ClientId == clientId)
                .Take(limit)
                .Select(r => r.Json)
                .ToList();
        }
        return Task.FromResult<IReadOnlyList<BrokerEvent>>(matches.Select(Deserialize).ToList());
    }

    private static BrokerEvent Deserialize(string json)
        => JsonSerializer.Deserialize<BrokerEvent>(json, JournalJson.Options)
           ?? throw new InvalidOperationException("A journal row held no event.");
}
