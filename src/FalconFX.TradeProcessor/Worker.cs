using System.Data;
using System.Diagnostics;
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

public readonly record struct TradeWorkItem(TradeRecord Record, TopicPartitionOffset Offset);

/// <summary>
///     High-throughput Trade Processor background worker.
///     Consumes executed trade events from Kafka, performs PostgreSQL bulk binary COPY insertion,
///     and broadcasts real-time ticker updates to Redis Pub/Sub.
/// </summary>
public sealed class Worker : BackgroundService
{
    private const int BatchSize = 5000;
    private const string Topic = "trades";
    private readonly IConfiguration _config;
    private readonly IConsumer<Null, byte[]> _consumer;
    private readonly ILogger<Worker> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly IServiceProvider _serviceProvider;

    private readonly Channel<TradeWorkItem> _tradeChannel;

    public Worker(
        ILogger<Worker> logger,
        IServiceProvider serviceProvider,
        IConsumer<Null, byte[]> consumer,
        IConnectionMultiplexer redis,
        IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _tradeChannel = Channel.CreateBounded<TradeWorkItem>(new BoundedChannelOptions(50_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogTradeProcessorStarting();

        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, Topic).ConfigureAwait(false);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
            await db.Database.EnsureCreatedAsync(stoppingToken).ConfigureAwait(false);
        }

        _consumer.Subscribe(Topic);
        _logger.LogKafkaConsumerSubscribed(Topic);

        var consumeTask = Task.Factory.StartNew(
            () => ConsumeKafkaLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var dbTask = Task.Factory.StartNew(
            () => DbBatchWriterLoopAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        await Task.WhenAll(consumeTask, dbTask).ConfigureAwait(false);
    }

    private async Task ConsumeKafkaLoopAsync(CancellationToken token)
    {
        var redisDb = _redis.GetDatabase();

        try
        {
            while (!token.IsCancellationRequested)
                try
                {
                    var result = _consumer.Consume(token);
                    if (result?.Message?.Value == null) continue;

                    var trade = TradeExecuted.Parser.ParseFrom(result.Message.Value);
                    var record = TradeRecord.FromProto(trade);

                    await _tradeChannel.Writer.WriteAsync(new TradeWorkItem(record, result.TopicPartitionOffset), token)
                        .ConfigureAwait(false);

                    _ = UpdateRedisMarketDataAsync(redisDb, trade);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogKafkaConsumeWarning(ex.Error.Reason);
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _tradeChannel.Writer.Complete();
            _consumer.Close();
        }
    }

    private static async Task UpdateRedisMarketDataAsync(IDatabase redisDb, TradeExecuted trade)
    {
        try
        {
            Span<char> span = stackalloc char[trade.Symbol.Length + 1 + 20];
            trade.Symbol.AsSpan().CopyTo(span);
            span[trade.Symbol.Length] = ':';

            trade.Price.TryFormat(span[(trade.Symbol.Length + 1)..], out var charsWritten);
            var redisPayload = span[..(trade.Symbol.Length + 1 + charsWritten)].ToString();

            await redisDb.StringSetAsync($"ticker:{trade.Symbol}", trade.Price, flags: CommandFlags.FireAndForget)
                .ConfigureAwait(false);
            await redisDb.PublishAsync(RedisChannel.Literal("market_updates"), redisPayload, CommandFlags.FireAndForget)
                .ConfigureAwait(false);
        }
        catch
        {
            // Suppress Redis exceptions to isolate the primary PostgreSQL persistence pipeline
        }
    }

    private async Task DbBatchWriterLoopAsync(CancellationToken token)
    {
        var batch = new List<TradeWorkItem>(BatchSize);

        try
        {
            while (await _tradeChannel.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_tradeChannel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                    if (batch.Count >= BatchSize) await FlushBatchBinaryCopyAsync(batch, token).ConfigureAwait(false);
                }

                if (batch.Count > 0) await FlushBatchBinaryCopyAsync(batch, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            while (_tradeChannel.Reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count > 0) await FlushBatchBinaryCopyAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushBatchBinaryCopyAsync(List<TradeWorkItem> batch, CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) await conn.OpenAsync(token).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();

        // PostgreSQL Native Binary Import Protocol (COPY FROM STDIN BINARY)
        await using (var writer = await conn.BeginBinaryImportAsync(
                         "COPY \"Trades\" (\"MakerOrderId\", \"TakerOrderId\", \"Price\", \"Quantity\", \"Symbol\", \"Timestamp\", \"InsertedAt\") FROM STDIN (FORMAT BINARY)",
                         token).ConfigureAwait(false))
        {
            foreach (var r in batch.Select(item => item.Record))
            {
                await writer.StartRowAsync(token).ConfigureAwait(false);
                await writer.WriteAsync(r.MakerOrderId, NpgsqlDbType.Bigint, token).ConfigureAwait(false);
                await writer.WriteAsync(r.TakerOrderId, NpgsqlDbType.Bigint, token).ConfigureAwait(false);
                await writer.WriteAsync(r.Price, NpgsqlDbType.Bigint, token).ConfigureAwait(false);
                await writer.WriteAsync(r.Quantity, NpgsqlDbType.Bigint, token).ConfigureAwait(false);
                await writer.WriteAsync(r.Symbol, NpgsqlDbType.Text, token).ConfigureAwait(false);
                await writer.WriteAsync(r.Timestamp, NpgsqlDbType.Bigint, token).ConfigureAwait(false);
                await writer.WriteAsync(r.InsertedAt, NpgsqlDbType.TimestampTz, token).ConfigureAwait(false);
            }

            await writer.CompleteAsync(token).ConfigureAwait(false);
        }

        sw.Stop();

        // Commit maximum Kafka offset per topic-partition
        var offsetsToCommit = batch
            .GroupBy(x => x.Offset.TopicPartition)
            .Select(g => new TopicPartitionOffset(
                g.Key,
                new Offset(g.Max(x => x.Offset.Offset.Value) + 1)
            ));

        try
        {
            _consumer.Commit(offsetsToCommit);
        }
        catch (Exception ex)
        {
            _logger.LogKafkaCommitError(ex.Message);
        }

        _logger.LogBatchSaved(batch.Count, sw.ElapsedMilliseconds);
        batch.Clear();
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
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