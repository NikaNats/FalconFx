using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FalconFX.ServiceDefaults;

public static class KafkaUtils
{
    public static async Task WaitForBrokerReady(IConfiguration config, ILogger logger, CancellationToken token)
    {
        var connectionString = config.GetConnectionString("kafka");

        // Configuration specifically for the Health Check
        // Increased timeouts to 10s to handle slow container startup/network
        var configDict = new AdminClientConfig
        {
            BootstrapServers = connectionString,
            SocketTimeoutMs = 10000,
            ApiVersionRequestTimeoutMs = 10000,
            LogConnectionClose = false
        };

        logger.LogInformation("⏳ Checking Kafka availability at {ConnectionString}...", connectionString);

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var adminClient = new AdminClientBuilder(configDict)
                    // Enable error logging to debug connection issues
                    .SetLogHandler((_, msg) =>
                    {
                        if (msg.Level < SyslogLevel.Info)
                            logger.LogDebug($"[Kafka Admin] {msg.Message}");
                    })
                    .Build();

                // Give it 5 seconds to respond
                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));

                if (metadata.Brokers.Count > 0)
                {
                    logger.LogInformation($"✅ Kafka is READY. Found {metadata.Brokers.Count} brokers.");
                    return; // Exit the loop and start the app
                }
            }
            catch (Exception ex)
            {
                // Log failure as warning so we know it's trying (and failing)
                logger.LogWarning($"Waiting for Kafka... ({ex.Message})");
            }

            await Task.Delay(2000, token);
        }
    }

    public static async Task EnsureTopicExistsAsync(
        IConfiguration config,
        ILogger logger,
        string topicName,
        int numPartitions = 1,
        short replicationFactor = 1)
    {
        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = config.GetConnectionString("kafka")
        };

        using var adminClient = new AdminClientBuilder(adminConfig).Build();

        try
        {
            await adminClient.CreateTopicsAsync([
                new TopicSpecification
                {
                    Name = topicName,
                    NumPartitions = numPartitions,
                    ReplicationFactor = replicationFactor
                }
            ]);
            logger.LogInformation("✅ Topic '{TopicName}' created successfully.", topicName);
        }
        catch (CreateTopicsException e)
        {
            if (e.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
                logger.LogInformation("👌 Topic '{TopicName}' already exists.", topicName);
            else
                logger.LogError("❌ Failed to create topic '{TopicName}': {ErrorReason}", topicName,
                    e.Results[0].Error.Reason);
        }
        catch (Exception ex)
        {
            logger.LogError("❌ Error creating topic '{TopicName}': {ExMessage}", topicName, ex.Message);
        }
    }
}