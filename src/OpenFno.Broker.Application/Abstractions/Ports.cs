using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Market;

namespace OpenFno.Broker.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// The append-only event store. An append either stores every event of the
/// batch or none of them; the engine applies a batch to memory only after the
/// append returns.
/// </summary>
public interface IJournal
{
    Task AppendAsync(IReadOnlyList<BrokerEvent> events, CancellationToken cancellationToken);

    IAsyncEnumerable<BrokerEvent> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>One client's events after <paramref name="afterSeq"/>, oldest first.</summary>
    Task<IReadOnlyList<BrokerEvent>> ReadAsync(string clientId, long afterSeq, int limit, CancellationToken cancellationToken);
}

public interface IInstrumentCatalog
{
    Instrument? Find(string symbol);
    int Count { get; }
}

/// <summary>The latest quote per symbol, written by the market-data feed and read by the engine.</summary>
public interface IQuoteBook
{
    Quote? Find(string symbol);
    void Update(Quote quote);

    /// <summary>Raised after a quote replaces an older one: the cue for matching.</summary>
    event Action<Quote>? Updated;
}

/// <summary>Encrypts secrets the broker must be able to read back (TOTP seeds) before they reach the journal.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

/// <summary>Runs work after a delay: how the simulated exchange answers some time after an order is sent.</summary>
public interface IScheduler
{
    void Schedule(TimeSpan delay, Func<CancellationToken, Task> work);
}
