namespace OpenFno.Broker.Application.Live;

/// <summary>
/// Faults the sandbox can inject, so a client's error handling meets them
/// before a real broker's outages do. Changed at runtime from the back office;
/// all off by default.
/// </summary>
public sealed class ChaosSettings
{
    private volatile int _extraAckLatencyMs;
    private volatile int _exchangeRejectPercent;
    private volatile int _lostResponsePercent;
    private volatile int _unavailablePercent;
    private volatile bool _feedPaused;

    /// <summary>Added to every exchange acknowledgement.</summary>
    public int ExtraAckLatencyMs
    {
        get => _extraAckLatencyMs;
        set => _extraAckLatencyMs = Math.Clamp(value, 0, 60_000);
    }

    /// <summary>Share of orders the exchange rejects after receiving them.</summary>
    public int ExchangeRejectPercent
    {
        get => _exchangeRejectPercent;
        set => _exchangeRejectPercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>
    /// Share of successful place, modify and cancel calls answered with 504:
    /// the broker did the work, the client never hears. The client must find
    /// out from the order book, as with a real gateway timeout.
    /// </summary>
    public int LostResponsePercent
    {
        get => _lostResponsePercent;
        set => _lostResponsePercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>Share of order calls refused with 503 before anything is done.</summary>
    public int UnavailablePercent
    {
        get => _unavailablePercent;
        set => _unavailablePercent = Math.Clamp(value, 0, 100);
    }

    /// <summary>Market data stops: quotes go stale and nothing matches.</summary>
    public bool FeedPaused
    {
        get => _feedPaused;
        set => _feedPaused = value;
    }

    public bool Roll(int percent) => percent > 0 && (percent >= 100 || Random.Shared.Next(100) < percent);

    public ChaosSnapshot Snapshot() => new(ExtraAckLatencyMs, ExchangeRejectPercent, LostResponsePercent, UnavailablePercent, FeedPaused);

    public void Apply(ChaosSnapshot settings)
    {
        ExtraAckLatencyMs = settings.ExtraAckLatencyMs;
        ExchangeRejectPercent = settings.ExchangeRejectPercent;
        LostResponsePercent = settings.LostResponsePercent;
        UnavailablePercent = settings.UnavailablePercent;
        FeedPaused = settings.FeedPaused;
    }
}

public sealed record ChaosSnapshot(
    int ExtraAckLatencyMs,
    int ExchangeRejectPercent,
    int LostResponsePercent,
    int UnavailablePercent,
    bool FeedPaused);
