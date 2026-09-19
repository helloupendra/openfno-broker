using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenFno.Broker.Application.Requests;

namespace OpenFno.Broker.Infrastructure.Persistence;

/// <summary>
/// Writes request-log entries to Postgres in batches, off the request path. If
/// the database falls behind, the oldest waiting entries are dropped rather
/// than letting memory grow or requests wait; the drop is logged.
/// </summary>
public sealed class PostgresRequestLogSink : BackgroundService, IRequestLogSink
{
    private const int Capacity = 20_000;
    private const int BatchSize = 500;

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<PostgresRequestLogSink> _logger;
    private readonly Channel<RequestLogEntry> _queue;
    private long _dropped;

    public PostgresRequestLogSink(NpgsqlDataSource db, ILogger<PostgresRequestLogSink> logger)
    {
        _db = db;
        _logger = logger;
        _queue = Channel.CreateBounded<RequestLogEntry>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
    }

    public void Enqueue(RequestLogEntry entry) => _queue.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RequestLogEntry>(BatchSize);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var entry)) batch.Add(entry);
                await WriteAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: write what is already queued.
            while (_queue.Reader.TryRead(out var entry)) batch.Add(entry);
            await WriteAsync(batch, CancellationToken.None);
        }
    }

    private async Task WriteAsync(List<RequestLogEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0) _logger.LogWarning("Request log fell behind; {Dropped} entries were dropped.", dropped);

        try
        {
            await using var connection = await _db.OpenConnectionAsync(cancellationToken);
            await using var writer = await connection.BeginBinaryImportAsync(
                "copy request_log (at, client_id, app_id, client_ip, method, path, status, error_code, order_id, " +
                "total_ms, auth_ms, rate_ms, queue_ms, decide_ms, journal_ms, body) from stdin (format binary)",
                cancellationToken);
            foreach (var e in batch)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(e.At.ToUniversalTime(), NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(e.ClientId, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.AppId, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.ClientIp, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.Method, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.Path, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.Status, NpgsqlTypes.NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(e.ErrorCode, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.OrderId, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
                await writer.WriteAsync(e.TotalMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.AuthMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.RateLimitMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.QueueMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.DecideMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.JournalMs, NpgsqlTypes.NpgsqlDbType.Double, cancellationToken);
                await writer.WriteAsync(e.Body, NpgsqlTypes.NpgsqlDbType.Text, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not write {Count} request-log entries.", batch.Count);
        }
    }
}
