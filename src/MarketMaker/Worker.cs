using Confluent.Kafka;
using Confluent.Kafka.Admin;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;
// Required for Topic Management

namespace MarketMaker;

public class Worker(
    ILogger<Worker> logger,
    IConfiguration config) : BackgroundService
{
    private const string TopicName = "orders";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. BLOCK HERE until port is open. No more "Broker down" logs.
        await KafkaUtils.WaitForBrokerReady(config, logger, stoppingToken);

        // 2. Now safe to use Admin Client
        await EnsureTopicExists(stoppingToken);

        // 3. Create producer AFTER Kafka is ready
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config.GetConnectionString("kafka"),
            // High throughput settings
            LingerMs = 0, // Send immediately
            BatchSize = 10 * 1024 * 1024, // 10MB batch
            CompressionType = CompressionType.Lz4,
            Acks = Acks.None, // Fire and forget for max speed
            MessageTimeoutMs = 30000,
            SocketTimeoutMs = 60000,
            ApiVersionRequestTimeoutMs = 10000
        };

        using var producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        logger.LogInformation("🚀 Starting UNLEASHED Producer...");

        // ... rest of your loop ...
        var orderId = 0L;
        var message = new Message<string, byte[]> { Key = "EURUSD" };

        while (!stoppingToken.IsCancellationRequested)
        {
            // Batch of 10,000 orders before even checking the clock
            for (var i = 0; i < 10000; i++)
            {
                orderId++;
                var request = new SubmitOrderRequest
                {
                    Id = orderId,
                    Side = Random.Shared.Next(1, 3),
                    // Old: 90 to 110 (Spread is too wide)
                    // New: 98 to 102 (Tight spread = More matches = Smaller Book)
                    Price = Random.Shared.Next(98, 102),
                    Quantity = 10
                };

                message.Value = request.ToByteArray();

                try
                {
                    // Fire and forget!
                    producer.Produce(TopicName, message);
                }
                catch (ProduceException<string, byte[]> e)
                {
                    if (e.Error.Code == ErrorCode.Local_QueueFull)
                    {
                        // Queue full? Poll to clear space, then retry immediately
                        producer.Poll(TimeSpan.FromMilliseconds(5));
                        i--; // Retry this index
                    }
                    else
                    {
                        await Task.Delay(100, stoppingToken);
                    }
                }
            }

            // Efficient Polling: 0 blocking time
            producer.Poll(TimeSpan.Zero);

            if (orderId % 500_000 == 0)
                logger.LogInformation($"🔥 Sent {orderId:N0} orders");

            // REMOVED: await Task.Delay(1); 
            // We run as fast as the CPU allows.
        }
    }

    private async Task EnsureTopicExists(CancellationToken token)
    {
        var configDict = new AdminClientConfig { BootstrapServers = config.GetConnectionString("kafka") };
        using var adminClient = new AdminClientBuilder(configDict).Build();

        // No while loop needed here anymore, usually one try is enough after WaitForBrokerReady
        // But keeping a simple retry for safety is fine.
        try
        {
            await adminClient.CreateTopicsAsync(new TopicSpecification[]
            {
                new() { Name = TopicName, NumPartitions = 1, ReplicationFactor = 1 }
            });

            logger.LogInformation($"✅ Topic '{TopicName}' created.");
        }
        catch (CreateTopicsException e)
        {
            if (e.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
            {
                logger.LogInformation($"✅ Topic '{TopicName}' exists.");
                return;
            }

            logger.LogWarning($"Retrying topic creation: {e.Results[0].Error.Reason}");
            await Task.Delay(1000, token);
            // For simplicity, just retry once more
            await adminClient.CreateTopicsAsync(new TopicSpecification[]
            {
                new() { Name = TopicName, NumPartitions = 1, ReplicationFactor = 1 }
            });
        }
    }
}