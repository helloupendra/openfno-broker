using System.Collections.Concurrent;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Domain.Market;

namespace OpenFno.Broker.Application.Market;

public sealed class QuoteBook : IQuoteBook
{
    private readonly ConcurrentDictionary<string, Quote> _quotes = new(StringComparer.OrdinalIgnoreCase);

    private long _latestTicks = DateTimeOffset.MinValue.UtcTicks;

    public int Count => _quotes.Count;

    /// <summary>The newest quote time seen, or null before the first one.</summary>
    public DateTimeOffset? LatestAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _latestTicks);
            return ticks == DateTimeOffset.MinValue.UtcTicks ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public Quote? Find(string symbol) => _quotes.GetValueOrDefault(symbol);

    /// <summary>Keeps the newest quote; a tick that arrives late does not overwrite a newer one.</summary>
    public void Update(Quote quote)
    {
        _quotes.AddOrUpdate(quote.Symbol, quote, (_, existing) => quote.At >= existing.At ? quote : existing);

        var ticks = quote.At.UtcTicks;
        var seen = Interlocked.Read(ref _latestTicks);
        while (ticks > seen)
        {
            var previous = Interlocked.CompareExchange(ref _latestTicks, ticks, seen);
            if (previous == seen) break;
            seen = previous;
        }
    }
}
