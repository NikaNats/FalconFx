using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;

namespace FalconFX.MatchingEngine;

public class KafkaWorker(
    ILogger<KafkaWorker> logger,
    EngineWorker engine,
    IConsumer<string, byte[]> consumer, // Inject the pre-configured consumer
    IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // USE THE NEW UTILS
        await KafkaUtils.WaitForBrokerReady(config, logger, stoppingToken);

        // Run the consumer loop with the injected consumer
        await Task.Factory.StartNew(() => StartConsumerLoop(consumer, stoppingToken), TaskCreationOptions.LongRunning);
    }

    private void StartConsumerLoop(IConsumer<string, byte[]> consumer, CancellationToken token)
    {
        // Subscribe is lazy; it won't crash if topic doesn't exist yet
        consumer.Subscribe("orders");
        logger.LogInformation("🚀 Consumer Loop Started");

        while (!token.IsCancellationRequested)
            try
            {
                var result = consumer.Consume(10); // Fast consume
                if (result?.Message == null) continue;

                var protoReq = SubmitOrderRequest.Parser.ParseFrom(result.Message.Value);
                engine.EnqueueOrder(new Order(protoReq.Id, (OrderSide)protoReq.Side, protoReq.Price,
                    protoReq.Quantity));
            }
            catch (ConsumeException e)
            {
                // Only log fatal errors, ignore temporary startups
                if (e.Error.IsFatal) logger.LogError($"Kafka Error: {e.Error.Reason}");
            }
            catch (OperationCanceledException)
            {
                break;
            }

        consumer.Close();
    }
}