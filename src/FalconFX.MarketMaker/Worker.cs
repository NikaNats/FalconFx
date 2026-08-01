using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MarketMaker;

/// <summary>
///     Ultra-low latency, high-throughput Market Maker worker node.
///     Generates high-frequency synthetic order streams using thread-safe serialization,
///     uninitialized array allocation, and deterministic Kafka key partitioning.
/// </summary>
public sealed class Worker(ILogger<Worker> logger, IConfiguration config) : BackgroundService
{
    private const string ServiceName = "MarketMaker";
    private const string TopicName = "orders";
    private const int BatchCount = 100;
    private const int StatsReportInterval = 50_000;

    private static readonly Meter MarketMakerMeter = new("FalconFX.MarketMaker");

    private static readonly Counter<long> OrdersProducedCounter =
        MarketMakerMeter.CreateCounter<long>("marketmaker.orders.produced", "orders",
            "Total synthetic orders produced");

    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly ILogger<Worker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        if (string.Equals(
                Environment.GetEnvironmentVariable("BENCHMARK_MODE"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("BENCHMARK_MODE=true → MarketMaker staying idle.");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        _logger.LogServiceStarting(ServiceName);

        // 1. Infrastructure Readiness Probes
        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, TopicName).ConfigureAwait(false);

        _logger.LogWaitingLeaderElection();
        await Task.Delay(3000, stoppingToken).ConfigureAwait(false);

        // 2. Build Security-Hardened & HFT-Tuned Kafka Producer Configuration
        var producerConfig = BuildProducerConfig(_config);

        // 3. Construct Producer using Custom Fast Protobuf Serializer and Long Keys
        using var producer = new ProducerBuilder<long, SubmitOrderRequest>(producerConfig)
            .SetKeySerializer(Serializers.Int64)
            .SetValueSerializer(ProtobufSubmitOrderSerializer.Instance)
            .Build();

        _logger.LogProducerStarted();

        var orderId = 0L;
        var rng = new XorShift64((ulong)DateTime.UtcNow.Ticks);
        var request = new SubmitOrderRequest();
        var message = new Message<long, SubmitOrderRequest>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (var i = 0; i < BatchCount; i++)
                {
                    orderId++;

                    request.Id = orderId;
                    request.Side = rng.Next(1, 3);          // 1 = Buy, 2 = Sell
                    request.Price = rng.Next(99, 102);      // Tight spread
                    request.Quantity = 10;

                    message.Key = orderId;
                    message.Value = request;

                    try
                    {
                        producer.Produce(TopicName, message);
                    }
                    catch (ProduceException<long, SubmitOrderRequest> ex)
                    {
                        _logger.LogProduceError(ex.Error.Reason);
                        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                    }
                }

                producer.Poll(TimeSpan.Zero);
                OrdersProducedCounter.Add(BatchCount);

                await Task.Delay(1, stoppingToken).ConfigureAwait(false);

                if (orderId % StatsReportInterval == 0)
                    _logger.LogOrdersSent(orderId);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogFatalError(ex);
            throw;
        }
        finally
        {
            _logger.LogFlushingProducer();
            try
            {
                producer.Flush(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogFlushError(ex.Message);
            }

            _logger.LogServiceStopped(ServiceName);
        }
    }

    private ProducerConfig BuildProducerConfig(IConfiguration config)
    {
        var bootstrapServers = config.GetConnectionString("kafka");
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogMissingConnectionString();
            throw new InvalidOperationException("Kafka connection string 'kafka' is missing or invalid.");
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            LingerMs = 5,
            BatchSize = 512 * 1024,
            MessageMaxBytes = 900_000,
            BatchNumMessages = 10_000,
            QueueBufferingMaxMessages = 1_000_000,
            QueueBufferingMaxKbytes = 512_000,
            CompressionType = CompressionType.Lz4,
            Acks = Acks.Leader,
            EnableDeliveryReports = false,
            MessageTimeoutMs = 5000,
            SocketTimeoutMs = 5000
        };

        // Optional security configuration
        var securityProtocol = config["Kafka:SecurityProtocol"];
        if (Enum.TryParse<SecurityProtocol>(securityProtocol, true, out var protocol))
        {
            producerConfig.SecurityProtocol = protocol;
            producerConfig.SaslMechanism =
                Enum.TryParse<SaslMechanism>(config["Kafka:SaslMechanism"], true, out var sasl)
                    ? sasl
                    : SaslMechanism.ScramSha512;
            producerConfig.SaslUsername = config["Kafka:SaslUsername"];
            producerConfig.SaslPassword = config["Kafka:SaslPassword"];
        }

        return producerConfig;
    }
}

/// <summary>
/// High-performance Protobuf serializer using uninitialized memory.
/// </summary>
internal sealed class ProtobufSubmitOrderSerializer : ISerializer<SubmitOrderRequest>
{
    public static readonly ProtobufSubmitOrderSerializer Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Serialize(SubmitOrderRequest data, SerializationContext context)
    {
        ArgumentNullException.ThrowIfNull(data);

        var size = data.CalculateSize();
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        data.WriteTo(buffer.AsSpan());
        return buffer;
    }
}