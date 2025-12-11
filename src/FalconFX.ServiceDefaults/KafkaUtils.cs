using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FalconFX.ServiceDefaults;

public static class KafkaUtils
{
    public static async Task WaitForBrokerReady(IConfiguration config, ILogger logger, CancellationToken token)
    {
        var configDict = new AdminClientConfig
        {
            BootstrapServers = config.GetConnectionString("kafka"),
            // Fail fast during the check so we can retry
            SocketTimeoutMs = 2000,
            ApiVersionRequestTimeoutMs = 2000
        };

        logger.LogInformation("⏳ waiting for Kafka Broker to be Metadata-Ready...");

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Create a temporary AdminClient just to check health
                using var adminClient = new AdminClientBuilder(configDict).Build();

                // Try to fetch metadata for the cluster. If this succeeds, Kafka is truly ready.
                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(2));

                if (metadata.Brokers.Count > 0)
                {
                    logger.LogInformation($"✅ Kafka Broker is Ready! (Detected {metadata.Brokers.Count} brokers)");
                    return;
                }
            }
            catch (KafkaException)
            {
                // Expected during startup
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Waiting for Kafka... ({ex.Message})");
            }

            await Task.Delay(2000, token);
        }
    }
}