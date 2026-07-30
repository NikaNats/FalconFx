using System.Diagnostics;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace FalconFX.TradeProcessor;

public readonly record struct TradeWorkItem(TradeRecord Record, TopicPartitionOffset Offset);

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConsumer<Null, byte[]> _consumer;
    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _config;

    private const int BatchSize = 1000;
    private const string Topic = "trades";

    private readonly Channel<TradeWorkItem> _tradeChannel;

    public Worker(
        ILogger<Worker> logger,
        IServiceProvider serviceProvider,
        IConsumer<Null, byte[]> consumer,
        IConnectionMultiplexer redis,
        IConfiguration config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _consumer = consumer;
        _redis = redis;
        _config = config;

        _tradeChannel = Channel.CreateBounded<TradeWorkItem>(new BoundedChannelOptions(10_000)
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
            {
                try
                {
                    var result = _consumer.Consume(token);
                    if (result?.Message?.Value == null) continue;

                    // Protobuf Deserialization
                    var trade = TradeExecuted.Parser.ParseFrom(result.Message.Value);

                    var record = TradeRecord.FromProto(trade);

                    // Write to In-Memory Channel for DB Batching
                    await _tradeChannel.Writer.WriteAsync(new TradeWorkItem(record, result.TopicPartitionOffset), token).ConfigureAwait(false);

                    // Redis Market Update (FireAndForget - Non Blocking)
                    _ = UpdateRedisMarketDataAsync(redisDb, trade);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogKafkaConsumeWarning(ex.Error.Reason);
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _tradeChannel.Writer.Complete();
            _consumer.Close();
        }
    }

    private static async Task UpdateRedisMarketDataAsync(IDatabase redisDb, TradeExecuted trade)
    {
        // Zero-Alloc String Formatting via Span
        Span<char> span = stackalloc char[trade.Symbol.Length + 1 + 20];
        trade.Symbol.AsSpan().CopyTo(span);
        span[trade.Symbol.Length] = ':';

        trade.Price.TryFormat(span[(trade.Symbol.Length + 1)..], out int charsWritten);
        var redisPayload = span[..(trade.Symbol.Length + 1 + charsWritten)].ToString();

        await redisDb.StringSetAsync($"ticker:{trade.Symbol}", trade.Price, flags: CommandFlags.FireAndForget).ConfigureAwait(false);
        await redisDb.PublishAsync(RedisChannel.Literal("market_updates"), redisPayload, CommandFlags.FireAndForget).ConfigureAwait(false);
    }

    private async Task DbBatchWriterLoopAsync(CancellationToken token)
    {
        var batch = new List<TradeWorkItem>(BatchSize);
        using var flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (!token.IsCancellationRequested)
            {
                var readTask = _tradeChannel.Reader.WaitToReadAsync(token).AsTask();
                var timerTask = flushTimer.WaitForNextTickAsync(token).AsTask();

                var completedTask = await Task.WhenAny(readTask, timerTask).ConfigureAwait(false);

                if (completedTask == readTask && await readTask.ConfigureAwait(false))
                {
                    while (_tradeChannel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        if (batch.Count >= BatchSize)
                        {
                            await FlushBatchAsync(batch, token).ConfigureAwait(false);
                        }
                    }
                }
                else if (completedTask == timerTask && batch.Count > 0)
                {
                    await FlushBatchAsync(batch, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_tradeChannel.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            if (batch.Count > 0)
            {
                await FlushBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushBatchAsync(List<TradeWorkItem> batch, CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();

        // 🚀 EF Core Optimization: Disable Change Tracking for Bulk Add
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        db.Trades.AddRange(batch.Select(x => x.Record));

        var sw = Stopwatch.StartNew();
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        sw.Stop();

        // Kafka Offset Commit
        var offsetsToCommit = batch
            .GroupBy(x => x.Offset.TopicPartition)
            .Select(g => new TopicPartitionOffset(g.Key, g.Max(x => x.Offset.Offset) + 1));

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
    [LoggerMessage(EventId = 301, Level = LogLevel.Information, Message = "🚀 TradeProcessor Worker starting...")]
    public static partial void LogTradeProcessorStarting(this ILogger logger);

    [LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "Trade Processor Started. Listening to topic '{Topic}'...")]
    public static partial void LogKafkaConsumerSubscribed(this ILogger logger, string topic);

    [LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "Kafka Consume Warning: {Reason}. Retrying...")]
    public static partial void LogKafkaConsumeWarning(this ILogger logger, string reason);

    [LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "Failed to commit Kafka offsets: {Message}")]
    public static partial void LogKafkaCommitError(this ILogger logger, string message);

    [LoggerMessage(EventId = 305, Level = LogLevel.Information, Message = "💾 Saved {Count:N0} trades to DB in {Time}ms")]
    public static partial void LogBatchSaved(this ILogger logger, int count, long time);
}