using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MarketMaker;

public class Worker(
    ILogger<Worker> logger,
    IConfiguration config) : BackgroundService
{
    private const string TopicName = "orders";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 MarketMaker Worker starting...");

        // 1. BLOCK HERE until port is open and Broker responds
        await KafkaUtils.WaitForBrokerReady(config, logger, stoppingToken);

        // 2. Ensure Topic Exists
        await KafkaUtils.EnsureTopicExistsAsync(config, logger, TopicName);

        // 🔥 CRITICAL: Wait for Leader Election for the new topic
        // Without this, the first batch of messages hits "Unknown Topic or Partition"
        logger.LogInformation("⏳ Waiting for Topic Leader Election...");
        await Task.Delay(3000, stoppingToken);

        // 3. Create producer
        var producerConfig = new ProducerConfig
        {
            BootstrapServers = config.GetConnectionString("kafka"),
            LingerMs = 5,
            BatchSize = 1024 * 1024,
            CompressionType = CompressionType.Lz4,

            // 🔥 Use Leader Acks to ensure the broker actually accepted the order
            Acks = Acks.Leader,

            MessageTimeoutMs = 5000,
            SocketTimeoutMs = 5000
        };

        using var producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        logger.LogInformation("🚀 Starting Producer...");

        var orderId = 0L;
        var message = new Message<string, byte[]> { Key = "EURUSD" };

        // Reuse Protobuf object to reduce GC pressure
        var request = new SubmitOrderRequest();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Batch of 100 orders
            for (var i = 0; i < 100; i++)
            {
                orderId++;

                request.Id = orderId;
                request.Side = Random.Shared.Next(1, 3);
                // Tight spread [99-101] to ensure matches happen frequently
                request.Price = Random.Shared.Next(99, 102);
                request.Quantity = 10;

                message.Value = request.ToByteArray();

                try
                {
                    producer.Produce(TopicName, message);
                }
                catch (ProduceException<string, byte[]> e)
                {
                    // Log specifically to see if queue is full or broker is down
                    logger.LogWarning($"Produce error: {e.Error.Reason}. Retrying...");
                    await Task.Delay(500, stoppingToken);
                }
            }

            producer.Poll(TimeSpan.Zero);

            // Log more frequently during debug (every 50k instead of 500k)
            if (orderId % 50_000 == 0)
            {
                logger.LogInformation($"🔥 Sent {orderId:N0} orders");
                await Task.Delay(10, stoppingToken); // Yield CPU
            }
        }
    }
}