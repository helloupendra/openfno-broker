using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Live;
using OpenFno.Broker.Domain.Market;
using StackExchange.Redis;

namespace OpenFno.Broker.Infrastructure.Market;

public sealed class RedisFeedOptions
{
    public const string Section = "MarketData:Redis";

    /// <summary>StackExchange.Redis configuration, such as <c>localhost:6380</c>. Empty turns the feed off.</summary>
    public string? Configuration { get; set; }

    /// <summary>The stream the OpenFNO platform's feeds publish normalized ticks to.</summary>
    public string Stream { get; set; } = "market:ticks";

    public int BatchSize { get; set; } = 1000;
    public int IdlePollMs { get; set; } = 100;
}

/// <summary>
/// The simulated exchange's market data: ticks from the OpenFNO platform's
/// Redis stream, read from the moment the broker starts (history is not
/// replayed; a quote is only as old as the last tick). Each entry carries the
/// tick as JSON in its <c>payload</c> field.
/// </summary>
public sealed class RedisTickFeed : BackgroundService
{
    private readonly RedisFeedOptions _options;
    private readonly IQuoteBook _quotes;
    private readonly IClock _clock;
    private readonly ChaosSettings _chaos;
    private readonly ILogger<RedisTickFeed> _logger;

    public RedisTickFeed(IOptions<RedisFeedOptions> options, IQuoteBook quotes, IClock clock, ChaosSettings chaos, ILogger<RedisTickFeed> logger)
    {
        _options = options.Value;
        _quotes = quotes;
        _clock = clock;
        _chaos = chaos;
        _logger = logger;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_options.Configuration);

    public long TicksRead { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Configuration))
        {
            _logger.LogInformation("No Redis feed configured; quotes come only from the admin API.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis tick feed failed; reconnecting in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ReadAsync(CancellationToken stoppingToken)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_options.Configuration!);
        var db = redis.GetDatabase();

        // Start at the stream's end: the broker trades on live prices only.
        RedisValue position = "0-0";
        if (await db.KeyExistsAsync(_options.Stream))
            position = (await db.StreamInfoAsync(_options.Stream)).LastGeneratedId;
        _logger.LogInformation("Reading ticks from {Stream} after {Position}.", _options.Stream, position);

        while (!stoppingToken.IsCancellationRequested)
        {
            var entries = await db.StreamReadAsync(_options.Stream, position, _options.BatchSize);
            if (entries.Length == 0)
            {
                await Task.Delay(_options.IdlePollMs, stoppingToken);
                continue;
            }

            foreach (var entry in entries)
            {
                position = entry.Id;
                if (_chaos.FeedPaused) continue;
                var payload = entry["payload"];
                if (payload.IsNullOrEmpty) continue;
                if (ParseTick(payload.ToString(), _clock.UtcNow) is { } quote)
                {
                    _quotes.Update(quote);
                    TicksRead++;
                }
            }
        }
    }

    /// <summary>A quote from one normalized tick, or null when it has no symbol or no usable last price.</summary>
    public static Quote? ParseTick(string json, DateTimeOffset receivedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var symbol = String(root, "symbol");
            var last = Number(root, "lastTradedPrice");
            if (string.IsNullOrWhiteSpace(symbol) || last is not > 0) return null;

            var at = Time(root, "exchangeTimestampUtc") ?? Time(root, "receivedUtc") ?? receivedAt;
            return new Quote
            {
                Symbol = symbol,
                LastPrice = last.Value,
                At = at,
                Bid = Number(root, "bidPrice") is > 0 and var bid ? bid : null,
                Ask = Number(root, "askPrice") is > 0 and var ask ? ask : null,
                BidQuantity = Number(root, "bidSize") is > 0 and var bidSize ? (long)bidSize : null,
                AskQuantity = Number(root, "askSize") is > 0 and var askSize ? (long)askSize : null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? Number(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static DateTimeOffset? Time(JsonElement root, string name)
        => String(root, name) is { } text
           && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
