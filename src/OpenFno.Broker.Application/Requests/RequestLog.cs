using System.Collections.Concurrent;

namespace OpenFno.Broker.Application.Requests;

/// <summary>
/// One API call as the broker saw it: who, from where, what came back, and
/// where the time went. This is how a client learns what reached the broker,
/// what was refused and how long each step took.
/// </summary>
public sealed record RequestLogEntry
{
    public required DateTimeOffset At { get; init; }
    public string? ClientId { get; init; }
    public string? AppId { get; init; }
    public string? ClientIp { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required int Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? OrderId { get; init; }

    /// <summary>From the first byte in to the response out, inside the broker.</summary>
    public required double TotalMs { get; init; }

    public double? AuthMs { get; init; }
    public double? RateLimitMs { get; init; }
    public double? QueueMs { get; init; }
    public double? DecideMs { get; init; }
    public double? JournalMs { get; init; }

    /// <summary>The request body of order calls, as sent. Login bodies are never kept.</summary>
    public string? Body { get; init; }
}

/// <summary>Where request-log entries go to be kept for good (a database table).</summary>
public interface IRequestLogSink
{
    void Enqueue(RequestLogEntry entry);
}

/// <summary>
/// Keeps each client's most recent requests in memory for the API to show, and
/// hands every entry to the durable sink when there is one.
/// </summary>
public sealed class RequestLog
{
    public const int KeptPerClient = 500;
    public const int KeptOverall = 2000;

    private readonly IRequestLogSink? _sink;
    private readonly ConcurrentDictionary<string, Queue<RequestLogEntry>> _recent = new(StringComparer.Ordinal);
    private readonly Queue<RequestLogEntry> _all = new();

    public RequestLog(IRequestLogSink? sink = null) => _sink = sink;

    public void Write(RequestLogEntry entry)
    {
        _sink?.Enqueue(entry);

        lock (_all)
        {
            _all.Enqueue(entry);
            while (_all.Count > KeptOverall) _all.Dequeue();
        }
        if (entry.ClientId is null) return;

        var queue = _recent.GetOrAdd(entry.ClientId, _ => new Queue<RequestLogEntry>());
        lock (queue)
        {
            queue.Enqueue(entry);
            while (queue.Count > KeptPerClient) queue.Dequeue();
        }
    }

    /// <summary>The client's latest requests, newest first.</summary>
    public IReadOnlyList<RequestLogEntry> Recent(string clientId, int limit)
    {
        if (!_recent.TryGetValue(clientId, out var queue)) return [];
        lock (queue) return queue.Reverse().Take(Math.Clamp(limit, 1, KeptPerClient)).ToList();
    }

    /// <summary>Every client's latest requests, including ones no session could be tied to (failed logins), newest first.</summary>
    public IReadOnlyList<RequestLogEntry> RecentAll(int limit)
    {
        lock (_all) return _all.Reverse().Take(Math.Clamp(limit, 1, KeptOverall)).ToList();
    }
}
