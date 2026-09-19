using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenFno.Broker.Domain.Events;

namespace OpenFno.Broker.Application.Live;

/// <summary>
/// Hands every committed event to live subscribers (the WebSocket streams).
/// A subscriber that falls behind loses its oldest events rather than slowing
/// the engine; the journal still has them.
/// </summary>
public sealed class EventHub
{
    private const int Capacity = 2_000;
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public int Subscribers => _subscribers.Count;

    /// <param name="clientId">Only this account's events; null for every event.</param>
    public Subscription Subscribe(string? clientId)
    {
        var channel = Channel.CreateBounded<BrokerEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        var id = Guid.NewGuid();
        _subscribers[id] = new Subscriber(clientId, channel.Writer);
        return new Subscription(channel.Reader, () =>
        {
            if (_subscribers.TryRemove(id, out var gone)) gone.Writer.TryComplete();
        });
    }

    public void Publish(IReadOnlyList<BrokerEvent> events)
    {
        if (events.Count == 0 || _subscribers.IsEmpty) return;
        foreach (var subscriber in _subscribers.Values)
            foreach (var e in events)
                if (subscriber.ClientId is null || subscriber.ClientId == e.ClientId)
                    subscriber.Writer.TryWrite(e);
    }

    private sealed record Subscriber(string? ClientId, ChannelWriter<BrokerEvent> Writer);
}

public sealed class Subscription : IDisposable
{
    private readonly Action _unsubscribe;

    public Subscription(ChannelReader<BrokerEvent> reader, Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    public ChannelReader<BrokerEvent> Reader { get; }

    public void Dispose() => _unsubscribe();
}
