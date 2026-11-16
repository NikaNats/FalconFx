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
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Kafka connection string 'kafka' is missing or invalid.");

        var adminConfig = new AdminClientConfig
        {
            BootstrapServers = connectionString,
            SocketTimeoutMs = 10_000,
            ApiVersionRequestTimeoutMs = 10_000,
            LogConnectionClose = false
        };

        logger.LogInformation("⏳ Checking Kafka availability at {ConnectionString}...", connectionString);

        // Build the admin client once: retries only re-issue GetMetadata, they do not
        // recreate connections or leak native handles.
        using var adminClient = new AdminClientBuilder(adminConfig)
            .SetLogHandler((_, msg) =>
            {
                if (msg.Level < SyslogLevel.Info)
                    logger.LogDebug("[Kafka Admin] {Message}", msg.Message);
            })
            .Build();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var metadata = adminClient.GetMetadata(TimeSpan.FromSeconds(5));
                if (metadata.Brokers.Count > 0)
                {
                    logger.LogInformation("✅ Kafka is READY. Found {Count} brokers.", metadata.Brokers.Count);
                    return;
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Waiting for Kafka... ({Message})", ex.Message);
            }

            await Task.Delay(2000, token).ConfigureAwait(false);
        }
    }

    public static async Task EnsureTopicExistsAsync(
        IConfiguration config,
        ILogger logger,
        string topicName,
        int numPartitions = 1,
        short replicationFactor = 1)
    {
        var connectionString = config.GetConnectionString("kafka");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Kafka connection string 'kafka' is missing or invalid.");

        var adminConfig = new AdminClientConfig { BootstrapServers = connectionString };
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

            await adminClient.CreateTopicsAsync([topicSpec]).ConfigureAwait(false);
            logger.LogInformation("✅ Topic '{TopicName}' created successfully with 10MB payload limit.", topicName);
        }
        catch (CreateTopicsException e) when (e.Results[0].Error.Code == ErrorCode.TopicAlreadyExists)
        {
            logger.LogInformation("👌 Topic '{TopicName}' already exists.", topicName);
        }
        catch (CreateTopicsException e)
        {
            logger.LogError("❌ Failed to create topic '{TopicName}': {ErrorReason}",
                topicName, e.Results[0].Error.Reason);
            throw;
        }
    }
}
