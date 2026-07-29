using System.Diagnostics;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using FalconFX.TradeProcessor.Data;
using StackExchange.Redis;

namespace FalconFX.TradeProcessor;

public readonly record struct TradeWorkItem(TradeRecord Record, TopicPartitionOffset Offset);

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly IConnectionMultiplexer _redis;
    private readonly IConfiguration _config;

    private const int BatchSize = 1000;
    private const string Topic = "trades";

    private readonly Channel<TradeWorkItem> _tradeChannel;

    public Worker(
        ILogger<Worker> logger,
        IServiceProvider serviceProvider,
        IConsumer<string, byte[]> consumer,
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
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, Topic);

        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        _consumer.Subscribe(Topic);
        _logger.LogInformation("Trade Processor Started. Listening...");

        var consumeTask = Task.Run(() => ConsumeKafkaLoopAsync(stoppingToken), stoppingToken);
        var dbTask = Task.Run(() => DbBatchWriterLoopAsync(stoppingToken), stoppingToken);

        await Task.WhenAll(consumeTask, dbTask);
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
                    if (result?.Message == null) continue;

                    var trade = TradeExecuted.Parser.ParseFrom(result.Message.Value);

                    var record = new TradeRecord
                    {
                        MakerOrderId = trade.MakerOrderId,
                        TakerOrderId = trade.TakerOrderId,
                        Price = trade.Price,
                        Quantity = trade.Quantity,
                        Symbol = trade.Symbol,
                        Timestamp = trade.Timestamp
                    };

                    await _tradeChannel.Writer.WriteAsync(new TradeWorkItem(record, result.TopicPartitionOffset), token);

                    int priceDigits = GetFormattedLength(trade.Price);
                    string redisPayload = string.Create(trade.Symbol.Length + 1 + priceDigits, (trade.Symbol, trade.Price),
                        (span, state) =>
                        {
                            state.Symbol.AsSpan().CopyTo(span);
                            span[state.Symbol.Length] = ':';
                            state.Price.TryFormat(span[(state.Symbol.Length + 1)..], out _);
                        });

                    await redisDb.StringSetAsync($"ticker:{trade.Symbol}", trade.Price, flags: CommandFlags.FireAndForget);
                    await redisDb.PublishAsync(RedisChannel.Literal("market_updates"), redisPayload, CommandFlags.FireAndForget);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogWarning("Kafka Consume Warning: {Reason}. Retrying...", ex.Error.Reason);
                    await Task.Delay(500, token);
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

                var completedTask = await Task.WhenAny(readTask, timerTask);

                if (completedTask == readTask && await readTask)
                {
                    while (_tradeChannel.Reader.TryRead(out var item))
                    {
                        batch.Add(item);
                        if (batch.Count >= BatchSize)
                        {
                            await FlushBatchAsync(batch, token);
                        }
                    }
                }
                else if (completedTask == timerTask && batch.Count > 0)
                {
                    await FlushBatchAsync(batch, token);
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
                await FlushBatchAsync(batch, CancellationToken.None);
            }
        }
    }

    private async Task FlushBatchAsync(List<TradeWorkItem> batch, CancellationToken token)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();

        db.Trades.AddRange(batch.Select(x => x.Record));

        var sw = Stopwatch.StartNew();
        await db.SaveChangesAsync(token);
        sw.Stop();

        var offsetsToCommit = batch
            .GroupBy(x => x.Offset.TopicPartition)
            .Select(g => new TopicPartitionOffset(g.Key, g.Max(x => x.Offset.Offset) + 1));

        try
        {
            _consumer.Commit(offsetsToCommit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to commit Kafka offsets: {Message}", ex.Message);
        }

        _logger.LogInformation("Saved {Count} trades in {Time}ms", batch.Count, sw.ElapsedMilliseconds);
        batch.Clear();
    }

    private static int GetFormattedLength(long value)
    {
        if (value == 0) return 1;
        int count = value < 0 ? 1 : 0;
        long v = Math.Abs(value);
        while (v > 0)
        {
            count++;
            v /= 10;
        }
        return count;
    }
}