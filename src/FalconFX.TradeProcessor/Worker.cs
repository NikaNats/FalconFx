using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace FalconFX.TradeProcessor;

public readonly record struct TradeWorkItem(TradeRecord? Record, TopicPartitionOffset Offset)
{
    public static TradeWorkItem Skip(TopicPartitionOffset offset) => new(null, offset);
}

public sealed class Worker : BackgroundService
{
    private const int BatchSize = 10_000;
    private const string Topic = "trades";

    private static readonly Meter Meter = new("FalconFX.TradeProcessor");

    private static readonly Counter<long> TradesPersisted = Meter.CreateCounter<long>(
        "tradeprocessor.trades.persisted", "trades", "Total trades persisted to PostgreSQL");

    private static readonly Counter<long> TickerUpdates = Meter.CreateCounter<long>(
        "tradeprocessor.ticker.updates", "updates", "Total ticker updates published to Redis");

    private readonly IConfiguration _config;
    private readonly IConsumer<Ignore, byte[]> _consumer;

    private readonly ILogger<Worker> _logger;
    private readonly Channel<TopicPartitionOffset> _offsetStoreChannel;
    private readonly IConnectionMultiplexer _redis;
    private readonly Channel<TradeExecuted> _redisChannel;
    private readonly IServiceProvider _serviceProvider;

    private readonly Channel<TradeWorkItem> _tradeChannel;

    public Worker(
        ILogger<Worker> logger,
        IServiceProvider serviceProvider,
        IConsumer<Ignore, byte[]> consumer,
        IConnectionMultiplexer redis,
        IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _tradeChannel = Channel.CreateBounded<TradeWorkItem>(new BoundedChannelOptions(500_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        _redisChannel = Channel.CreateBounded<TradeExecuted>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        _offsetStoreChannel = Channel.CreateUnbounded<TopicPartitionOffset>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogTradeProcessorStarting();

        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, Topic).ConfigureAwait(false);

        await EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);

        _consumer.Subscribe(Topic);
        _logger.LogKafkaConsumerSubscribed(Topic);

        // Linked CTS ensures if ANY loop faults, the sibling loops cancel immediately
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var loopToken = linkedCts.Token;

        var consumeTask = Task.Factory.StartNew(
            () => ConsumeKafkaLoopAsync(loopToken),
            loopToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var dbTask = Task.Factory.StartNew(
            () => DbBatchWriterLoopAsync(loopToken),
            loopToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var redisTask = Task.Factory.StartNew(
            () => RedisPublisherLoopAsync(loopToken),
            loopToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        var tasks = new[] { consumeTask, dbTask, redisTask };
        var firstCompleted = await Task.WhenAny(tasks).ConfigureAwait(false);

        // Cancel siblings if one exited unexpectedly (or on normal shutdown)
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (firstCompleted.IsFaulted)
            await firstCompleted.ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
                                             CREATE TABLE IF NOT EXISTS "Trades" (
                                                 "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                                                 "TradeId" bigint NOT NULL,
                                                 "MakerOrderId" bigint NOT NULL,
                                                 "TakerOrderId" bigint NOT NULL,
                                                 "Price" bigint NOT NULL,
                                                 "Quantity" bigint NOT NULL,
                                                 "Symbol" text NOT NULL,
                                                 "Timestamp" bigint NOT NULL,
                                                 "InsertedAt" timestamp with time zone NOT NULL
                                             );
                                             -- Upgrade path for volumes created before TradeId existed: add the
                                             -- column, backfill deterministic ids from the identity column, then
                                             -- enforce NOT NULL before the unique index is created.
                                             ALTER TABLE "Trades" ADD COLUMN IF NOT EXISTS "TradeId" bigint;
                                             UPDATE "Trades" SET "TradeId" = "Id" WHERE "TradeId" IS NULL;
                                             ALTER TABLE "Trades" ALTER COLUMN "TradeId" SET NOT NULL;
                                             CREATE UNIQUE INDEX IF NOT EXISTS "IX_Trades_TradeId" ON "Trades" ("TradeId");
                                             CREATE INDEX IF NOT EXISTS "IX_Trades_Symbol_Timestamp" ON "Trades" ("Symbol", "Timestamp");
                                             CREATE INDEX IF NOT EXISTS "IX_Trades_Timestamp" ON "Trades" ("Timestamp");
                                             """, stoppingToken).ConfigureAwait(false);
    }

    private async Task ConsumeKafkaLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                DrainAndStoreOffsets();

                try
                {
                    var result = _consumer.Consume(TimeSpan.FromMilliseconds(10));
                    if (result?.Message?.Value is null) continue;

                    while (result?.Message?.Value is not null && !token.IsCancellationRequested)
                    {
                        TradeExecuted trade;
                        try
                        {
                            trade = TradeExecuted.Parser.ParseFrom(result.Message.Value);
                        }
                        catch (Google.Protobuf.InvalidProtocolBufferException ex)
                        {
                            _logger.LogKafkaConsumeWarning($"Discarding unparsable trade: {ex.Message}");

                            // Route the skip through the batch channel so its offset is released
                            // only after every earlier trade has been committed. Storing it here
                            // would let a crash commit past still-queued valid trades.
                            var skipItem = TradeWorkItem.Skip(result.TopicPartitionOffset);
                            if (!_tradeChannel.Writer.TryWrite(skipItem))
                                await _tradeChannel.Writer.WriteAsync(skipItem, token).ConfigureAwait(false);

                            result = _consumer.Consume(TimeSpan.Zero);
                            continue;
                        }

                        var record = TradeRecord.FromProto(trade);
                        var workItem = new TradeWorkItem(record, result.TopicPartitionOffset);

                        if (!_tradeChannel.Writer.TryWrite(workItem))
                            await _tradeChannel.Writer.WriteAsync(workItem, token).ConfigureAwait(false);

                        _redisChannel.Writer.TryWrite(trade);

                        result = _consumer.Consume(TimeSpan.Zero);
                    }
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.IsFatal) throw;

                    _logger.LogKafkaConsumeWarning(ex.Error.Reason);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            // Completing the channels lets the DB writer flush everything already
            // accepted before the final offset commit happens.
            _tradeChannel.Writer.TryComplete();
            _redisChannel.Writer.TryComplete();

            DrainAndStoreOffsets();
            try
            {
                _consumer.Commit();
            }
            catch
            {
                // Best-effort final commit
            }

            try
            {
                _consumer.Close();
            }
            catch
            {
            }
        }
    }

    private void DrainAndStoreOffsets()
    {
        while (_offsetStoreChannel.Reader.TryRead(out var tpo))
            try
            {
                // StoreOffset expects the next offset to read (offset + 1)
                _consumer.StoreOffset(new TopicPartitionOffset(tpo.TopicPartition, tpo.Offset + 1));
            }
            catch (Exception ex)
            {
                _logger.LogKafkaCommitError(ex.Message);
            }
    }

    private async Task DbBatchWriterLoopAsync(CancellationToken token)
    {
        var batch = new List<TradeWorkItem>(BatchSize);
        var partitionOffsets = new Dictionary<TopicPartition, Offset>();
        var dataSource = _serviceProvider.GetRequiredService<NpgsqlDataSource>();

        try
        {
            // Drain until the channel is completed so in-flight items are flushed on graceful shutdown
            while (await _tradeChannel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (batch.Count < BatchSize && _tradeChannel.Reader.TryRead(out var item))
                    batch.Add(item);

                if (batch.Count == 0) continue;

                var hasRecords = false;
                foreach (var item in batch)
                    if (item.Record is not null)
                    {
                        hasRecords = true;
                        break;
                    }

                if (!hasRecords)
                {
                    // Poison-pill-only batch: nothing to persist, release offsets in order.
                    foreach (var item in batch)
                        _offsetStoreChannel.Writer.TryWrite(item.Offset);

                    batch.Clear();
                    continue;
                }

                // Bound the flush so teardown cannot hang forever if the database is gone
                using var flushTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var batchToken = token.IsCancellationRequested ? flushTimeoutCts.Token : token;

                try
                {
                    await PersistBatchWithRetryAsync(dataSource, batch, partitionOffsets, batchToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Offsets stay uncommitted, so Kafka replays this batch after restart
                    // and the TradeId unique index makes the replay idempotent.
                    _logger.LogWarning(
                        "Shutdown interrupted a {Count:N0} trade batch; offsets will be replayed on restart.",
                        batch.Count);
                    batch.Clear();
                    partitionOffsets.Clear();
                    break;
                }

                batch.Clear();
                partitionOffsets.Clear();
            }
        }
        finally
        {
            _offsetStoreChannel.Writer.TryComplete();
        }
    }

    private async Task PersistBatchWithRetryAsync(
        NpgsqlDataSource dataSource,
        List<TradeWorkItem> batch,
        Dictionary<TopicPartition, Offset> partitionOffsets,
        CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        const int maxRetries = 5;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
            try
            {
                var sw = Stopwatch.StartNew();

                await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                var recordCount = 0;

                // Staging table enables idempotent deduplication while preserving binary COPY speed
                await using (var createTemp = new NpgsqlCommand("""
                                                               CREATE TEMP TABLE IF NOT EXISTS "_TradesStaging" (
                                                                   "TradeId" bigint NOT NULL,
                                                                   "MakerOrderId" bigint NOT NULL,
                                                                   "TakerOrderId" bigint NOT NULL,
                                                                   "Price" bigint NOT NULL,
                                                                   "Quantity" bigint NOT NULL,
                                                                   "Symbol" text NOT NULL,
                                                                   "Timestamp" bigint NOT NULL,
                                                                   "InsertedAt" timestamp with time zone NOT NULL
                                                               ) ON COMMIT DELETE ROWS;
                                                               """, conn, tx))
                {
                    await createTemp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var writer = await conn.BeginBinaryImportAsync("""
                                                                            COPY "_TradesStaging" ("TradeId", "MakerOrderId", "TakerOrderId", "Price", "Quantity", "Symbol", "Timestamp", "InsertedAt")
                                                                            FROM STDIN (FORMAT BINARY)
                                                                            """, ct).ConfigureAwait(false))
                {
                    foreach (var item in batch)
                    {
                        var r = item.Record;
                        if (r is null)
                        {
                            // Poison-pill marker: share this transaction's commit boundary so
                            // its offset is released together with the records before it.
                            partitionOffsets[item.Offset.TopicPartition] = item.Offset.Offset;
                            continue;
                        }

                        recordCount++;
                        var utcInsertedAt = r.InsertedAt.Kind == DateTimeKind.Utc
                            ? r.InsertedAt
                            : DateTime.SpecifyKind(r.InsertedAt, DateTimeKind.Utc);

                        await writer.StartRowAsync(ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.TradeId, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.MakerOrderId, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.TakerOrderId, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.Price, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.Quantity, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.Symbol, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                        await writer.WriteAsync(r.Timestamp, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                        await writer.WriteAsync(utcInsertedAt, NpgsqlDbType.TimestampTz, ct).ConfigureAwait(false);

                        partitionOffsets[item.Offset.TopicPartition] = item.Offset.Offset;
                    }

                    await writer.CompleteAsync(ct).ConfigureAwait(false);
                }

                await using (var mergeCmd = new NpgsqlCommand("""
                                                              INSERT INTO "Trades" ("TradeId", "MakerOrderId", "TakerOrderId", "Price", "Quantity", "Symbol", "Timestamp", "InsertedAt")
                                                              SELECT "TradeId", "MakerOrderId", "TakerOrderId", "Price", "Quantity", "Symbol", "Timestamp", "InsertedAt"
                                                              FROM "_TradesStaging"
                                                              ON CONFLICT ("TradeId") DO NOTHING;
                                                              """, conn, tx))
                {
                    await mergeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                sw.Stop();

                // Offsets are only released after the transaction committed: a crash before
                // this point replays the batch, and ON CONFLICT makes that replay harmless.
                foreach (var (tp, offset) in partitionOffsets)
                    _offsetStoreChannel.Writer.TryWrite(new TopicPartitionOffset(tp, offset));

                TradesPersisted.Add(recordCount);
                _logger.LogBatchSaved(recordCount, sw.ElapsedMilliseconds);
                return;
            }
            catch (Exception ex) when (ex is SocketException or NpgsqlException && attempt < maxRetries)
            {
                _logger.LogWarning(
                    "Database write failed (Attempt {Attempt}/{Max}). Retrying in {Delay}ms... Error: {Msg}",
                    attempt, maxRetries, retryDelay.TotalMilliseconds, ex.Message);

                await Task.Delay(retryDelay, ct).ConfigureAwait(false);
                retryDelay *= 2;
            }

        _logger.LogCritical("Failed to persist trades batch after {Max} retries.", maxRetries);
        throw new InvalidOperationException("Postgres persistence failed permanently.");
    }

    private async Task RedisPublisherLoopAsync(CancellationToken token)
    {
        var redisDb = _redis.GetDatabase();
        var latestBySymbol = new Dictionary<string, TradeExecuted>(StringComparer.Ordinal);

        try
        {
            while (await _redisChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                latestBySymbol.Clear();

                // Keep only the newest trade per symbol: a fast symbol can no longer starve
                // every other symbol out of the ticker update.
                while (_redisChannel.Reader.TryRead(out var trade))
                    latestBySymbol[trade.Symbol] = trade;

                foreach (var (_, trade) in latestBySymbol)
                    try
                    {
                        var payload = FormatRedisPayload(trade.Symbol, trade.Price);

                        await redisDb.StringSetAsync(
                            $"ticker:{trade.Symbol}",
                            trade.Price,
                            flags: CommandFlags.FireAndForget).ConfigureAwait(false);

                        await redisDb.PublishAsync(
                            RedisChannel.Literal("market_updates"),
                            payload,
                            CommandFlags.FireAndForget).ConfigureAwait(false);

                        TickerUpdates.Add(1);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Redis ticker update failed for {Symbol}: {Message}",
                            trade.Symbol, ex.Message);
                    }

                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    ///     Builds "SYMBOL:price" with a single allocation. No symbol-sized stackalloc,
    ///     so no symbol length can ever overflow the stack.
    /// </summary>
    internal static string FormatRedisPayload(string symbol, long price)
    {
        Span<char> priceBuffer = stackalloc char[21];
        price.TryFormat(priceBuffer, out var priceLength);

        return string.Create(symbol.Length + 1 + priceLength, (symbol, price), static (span, state) =>
        {
            state.symbol.AsSpan().CopyTo(span);
            span[state.symbol.Length] = ':';
            state.price.TryFormat(span[(state.symbol.Length + 1)..], out _);
        });
    }
}

internal static partial class TradeProcessorLogExtensions
{
    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "TradeProcessor Worker starting...")]
    public static partial void LogTradeProcessorStarting(this ILogger logger);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information,
        Message = "Trade Processor Started. Listening to topic '{Topic}'...")]
    public static partial void LogKafkaConsumerSubscribed(this ILogger logger, string topic);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Kafka Consume Warning: {Reason}. Retrying...")]
    public static partial void LogKafkaConsumeWarning(this ILogger logger, string reason);

    [LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "Failed to commit Kafka offsets: {Message}")]
    public static partial void LogKafkaCommitError(this ILogger logger, string message);

    [LoggerMessage(EventId = 305, Level = LogLevel.Information,
        Message = "Saved {Count:N0} trades to database in {Time}ms")]
    public static partial void LogBatchSaved(this ILogger logger, int count, long time);
}

