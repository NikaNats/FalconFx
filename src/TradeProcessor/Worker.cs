using System.Diagnostics;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using StackExchange.Redis;

// Import Utils

public class Worker(
    ILogger<Worker> logger,
    IServiceProvider serviceProvider,
    IConsumer<string, byte[]> consumer,
    IConnectionMultiplexer redis,
    IConfiguration config) : BackgroundService // <--- Inject Config
{
    private const int BatchSize = 1000;
    private const string Topic = "trades";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Wait for Broker
        await KafkaUtils.WaitForBrokerReady(config, logger, stoppingToken);

        // 2. 🔥 FIX: Explicitly Create the Topic before Subscribing
        await KafkaUtils.EnsureTopicExistsAsync(config, logger, Topic);

        // 3. Ensure DB Created
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        // 4. Now it is safe to Subscribe
        consumer.Subscribe(Topic);

        var dbBatch = new List<TradeRecord>(BatchSize);
        var redisDb = redis.GetDatabase();

        logger.LogInformation("💾 Trade Processor Started. Listening...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]> result = null;

                try
                {
                    // 5. 🔥 FIX: Add Resilience around Consume()
                    // If a rebalance happens, Consume might throw temporarily.
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    // Ignore "Unknown topic" errors if they persist briefly, but log them.
                    logger.LogWarning($"Kafka Consume Warning: {ex.Error.Reason}. Retrying...");
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                if (result?.Message == null) continue;

                // Deserialize
                var trade = TradeExecuted.Parser.ParseFrom(result.Message.Value);

                // Add to Batch
                dbBatch.Add(new TradeRecord
                {
                    // Id = trade.Id, // Let DB generate ID if using Identity, or use trade.Id
                    MakerOrderId = trade.MakerOrderId,
                    TakerOrderId = trade.TakerOrderId,
                    Price = trade.Price,
                    Quantity = trade.Quantity,
                    Symbol = trade.Symbol,
                    Timestamp = trade.Timestamp
                });

                // Update Real-time Ticker in Redis (Fire & Forget)
                // Key: "ticker:EURUSD", Value: LastPrice
                // Flags: FireAndForget speeds up the loop as we don't wait for Redis response
                redisDb.StringSetAsync($"ticker:{trade.Symbol}", trade.Price, flags: CommandFlags.FireAndForget);

                // If Batch Full -> Flush to Postgres
                if (dbBatch.Count >= BatchSize) await FlushBatchAsync(dbBatch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Flush remaining on exit
            if (dbBatch.Count > 0) await FlushBatchAsync(dbBatch, CancellationToken.None);
            consumer.Close();
        }
    }

    private async Task FlushBatchAsync(List<TradeRecord> batch, CancellationToken token)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeDbContext>();

        // EF Core automatically batches these inserts into a single COPY command or INSERT block
        db.Trades.AddRange(batch);

        var sw = Stopwatch.StartNew();
        await db.SaveChangesAsync(token);
        sw.Stop();

        logger.LogInformation($"💾 Saved {batch.Count} trades in {sw.ElapsedMilliseconds}ms");
        batch.Clear();
    }
}