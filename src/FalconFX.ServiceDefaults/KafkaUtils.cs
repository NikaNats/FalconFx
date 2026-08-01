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
                    .SetLogHandler((_, msg) =>
                    {
                        if (msg.Level < SyslogLevel.Info)
                            logger.LogDebug("[Kafka Admin] {Message}", msg.Message);
                    })
                    .Build();

                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));

                if (metadata.Brokers.Count > 0)
                {
                    logger.LogInformation("✅ Kafka is READY. Found {Count} brokers.", metadata.Brokers.Count);
                    return;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Waiting for Kafka... ({Message})", ex.Message);
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
            var topicSpec = new TopicSpecification
            {
                Name = topicName,
                NumPartitions = numPartitions,
                ReplicationFactor = replicationFactor,
                Configs = new Dictionary<string, string>
                {
                    { "max.message.bytes", "10485760" }, // 10 MB
                    { "compression.type", "lz4" }
                }
            };

            await adminClient.CreateTopicsAsync(new[] { topicSpec });
            logger.LogInformation("✅ Topic '{TopicName}' created successfully with 10MB payload limit.", topicName);
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