using System.Diagnostics;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Live;
using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Application.Engine;

/// <summary>
/// The broker: accounts, sessions, funds and orders, behind one lock.
/// </summary>
/// <remarks>
/// Every command runs the same way, one at a time:
/// <list type="number">
/// <item>decide, against the current state, which events happen (or why none can);</item>
/// <item>write those events to the journal;</item>
/// <item>apply them to memory, and only then answer.</item>
/// </list>
/// A command whose events fail to reach the journal changes nothing. A single
/// writer keeps the rules simple and the history exactly ordered; the
/// simulator's load (a few clients, tens of orders a second at most) is far
/// below what one lock handles.
/// </remarks>
public sealed partial class BrokerEngine
{
    private readonly IJournal _journal;
    private readonly IClock _clock;
    private readonly IInstrumentCatalog _instruments;
    private readonly IQuoteBook _quotes;
    private readonly ExchangeCalendar _calendar;
    private readonly ISecretProtector _protector;
    private readonly IScheduler _scheduler;
    private readonly ExchangeSimulationOptions _options;
    private readonly EventHub _hub;
    private readonly ChaosSettings _chaos;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BrokerState _state = new();
    private volatile bool _started;

    public BrokerEngine(
        IJournal journal,
        IClock clock,
        IInstrumentCatalog instruments,
        IQuoteBook quotes,
        ExchangeCalendar calendar,
        ISecretProtector protector,
        IScheduler scheduler,
        ExchangeSimulationOptions options,
        EventHub hub,
        ChaosSettings chaos)
    {
        _journal = journal;
        _clock = clock;
        _instruments = instruments;
        _quotes = quotes;
        _calendar = calendar;
        _protector = protector;
        _scheduler = scheduler;
        _options = options;
        _hub = hub;
        _chaos = chaos;
    }

    public bool IsStarted => _started;

    /// <summary>Whether any order on the symbol could fill; asked on every tick, without the lock.</summary>
    public bool HasLiveOrders(string symbol) => _state.LiveSymbolCounts.ContainsKey(symbol);

    /// <summary>
    /// Rebuilds the state from the journal. Orders that were on their way to
    /// the exchange when the process stopped are acknowledged now.
    /// </summary>
    /// <returns>How many events were replayed.</returns>
    public async Task<long> StartAsync(CancellationToken cancellationToken = default)
    {
        List<string> inTransit;
        long replayed = 0;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started) throw new InvalidOperationException("The engine is already started.");
            await foreach (var e in _journal.ReadAllAsync(cancellationToken))
            {
                _state.Apply(e);
                replayed++;
            }
            _started = true;
            inTransit = _state.LiveOrderIds
                .Where(id => _state.Orders[id].Status == OrderStatus.Transit)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var orderId in inTransit) ScheduleAck(orderId, TimeSpan.Zero);
        return replayed;
    }

    /// <summary>What a command decided: the events to record and how to answer once they are applied.</summary>
    private sealed class Outcome<T>
    {
        private Outcome(IReadOnlyList<BrokerEvent> events, Func<BrokerState, Result<T>> answer, Action? afterCommit)
        {
            Events = events;
            Answer = answer;
            AfterCommit = afterCommit;
        }

        public IReadOnlyList<BrokerEvent> Events { get; }
        public Func<BrokerState, Result<T>> Answer { get; }

        /// <summary>Runs after the lock is released, for work that re-enters the engine later (exchange acknowledgements).</summary>
        public Action? AfterCommit { get; }

        public static Outcome<T> Of(BrokerEvent e, Func<BrokerState, Result<T>> answer, Action? afterCommit = null)
            => new([e], answer, afterCommit);

        public static Outcome<T> Of(IReadOnlyList<BrokerEvent> events, Func<BrokerState, Result<T>> answer, Action? afterCommit = null)
            => new(events, answer, afterCommit);

        public static Outcome<T> Nothing(T value) => new([], _ => value, null);

        public static implicit operator Outcome<T>(BrokerError error) => new([], _ => error, null);
    }

    private async Task<Result<T>> RunAsync<T>(
        Func<DateTimeOffset, Outcome<T>> decide,
        CommandTiming? timing,
        CancellationToken cancellationToken)
    {
        var waitStarted = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(cancellationToken);

        Result<T> answer;
        Action? afterCommit;
        List<BrokerEvent>? committed = null;
        try
        {
            if (timing is not null) timing.QueueWait = CommandTiming.Since(waitStarted);
            if (!_started)
                return new BrokerError(ErrorCodes.Unavailable, "The broker is still starting.", ErrorKind.Unavailable);

            var decideStarted = Stopwatch.GetTimestamp();
            var now = _clock.UtcNow;
            var outcome = decide(now);
            if (timing is not null) timing.Decide = CommandTiming.Since(decideStarted);

            if (outcome.Events.Count > 0)
            {
                var seq = _state.LastSeq;
                var stamped = outcome.Events.Select(e => e with { Seq = ++seq, At = now }).ToList();

                // Once decided, the command is recorded even if the caller has gone away.
                var journalStarted = Stopwatch.GetTimestamp();
                await _journal.AppendAsync(stamped, CancellationToken.None);
                if (timing is not null) timing.Journal = CommandTiming.Since(journalStarted);

                foreach (var e in stamped) _state.Apply(e);
                committed = stamped;
            }

            answer = outcome.Answer(_state);
            afterCommit = outcome.AfterCommit;
        }
        finally
        {
            _gate.Release();
        }

        if (committed is not null) _hub.Publish(committed);
        afterCommit?.Invoke();
        return answer;
    }

    /// <summary>Runs a read against the state under the lock, so it never sees half a command.</summary>
    private async Task<Result<T>> ReadAsync<T>(Func<DateTimeOffset, Result<T>> read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _started
                ? read(_clock.UtcNow)
                : new BrokerError(ErrorCodes.Unavailable, "The broker is still starting.", ErrorKind.Unavailable);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BrokerError AccountMissing(string clientId)
        => BrokerError.NotFound(ErrorCodes.AccountNotFound, $"No account {clientId}.");
}
