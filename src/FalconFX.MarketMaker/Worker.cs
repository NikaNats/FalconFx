using System.Runtime.CompilerServices;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MarketMaker;

/// <summary>
///     Ultra-low latency, zero-allocation Market Maker worker node.
///     Generates high-frequency synthetic order streams for matching engine load simulation.
/// </summary>
public sealed class Worker : BackgroundService
{
    private const string ServiceName = "MarketMaker";
    private const string TopicName = "orders";
    private const int BatchCount = 100;
    private const int StatsReportInterval = 50_000;

    // Pre-allocated exact-size buffer cache to eliminate GC pressure during serialization
    private readonly byte[][] _bufferCache = new byte[128][];
    private readonly IConfiguration _config;
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger, IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogServiceStarting(ServiceName);

        // 1. Validate Infrastructure Readiness
        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, TopicName).ConfigureAwait(false);

        _logger.LogWaitingLeaderElection();
        await Task.Delay(3000, stoppingToken).ConfigureAwait(false);

        // 2. Build HFT-Tuned Kafka Producer Configuration
        var bootstrapServers = _config.GetConnectionString("kafka");
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogMissingConnectionString();
            throw new InvalidOperationException("Kafka connection string 'kafka' is not configured.");
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,

            // Batching & Throughput Optimization
            LingerMs = 5,
            BatchSize = 512 * 1024, // 512 KB batch size
            MessageMaxBytes = 900_000, // Safely below Kafka broker default 1MB limit
            BatchNumMessages = 10000,
            QueueBufferingMaxMessages = 1_000_000,
            QueueBufferingMaxKbytes = 512_000,
            CompressionType = CompressionType.Lz4,

            // Durability & Speed Balance
            Acks = Acks.Leader,

            // HFT Optimization: Disable delivery callbacks to eliminate librdkafka event queue allocations
            EnableDeliveryReports = false,

            // Resilience Timeouts
            MessageTimeoutMs = 5000,
            SocketTimeoutMs = 5000
        };

        using var producer = new ProducerBuilder<Null, byte[]>(producerConfig).Build();

        _logger.LogProducerStarted();

        var orderId = 0L;
        var rng = new XorShift64((ulong)DateTime.UtcNow.Ticks);
        var request = new SubmitOrderRequest();
        var kafkaMessage = new Message<Null, byte[]>();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                for (var i = 0; i < BatchCount; i++)
                {
                    orderId++;

                    request.Id = orderId;
                    request.Side = rng.Next(1, 3); // 1 = Buy, 2 = Sell
                    request.Price = rng.Next(99, 102); // Tight spread [99-101]
                    request.Quantity = 10;

                    // Zero-Alloc Serialization into pre-allocated exact-sized buffer
                    var msgSize = request.CalculateSize();
                    var buffer = GetOrCreateBuffer(msgSize);

                    // Direct serialization into Span<byte>
                    request.WriteTo(buffer.AsSpan(0, msgSize));

                    kafkaMessage.Value = buffer;

                    try
                    {
                        producer.Produce(TopicName, kafkaMessage);
                    }
                    catch (ProduceException<Null, byte[]> ex)
                    {
                        _logger.LogProduceError(ex.Error.Reason);
                        await Task.Delay(100, stoppingToken).ConfigureAwait(false);
                    }
                }

                // Poll native librdkafka queue events
                producer.Poll(TimeSpan.Zero);

                // Throttle execution slightly to prevent unbounded Kafka consumer lag during continuous stream generation
                await Task.Delay(1, stoppingToken).ConfigureAwait(false);

                if (orderId % StatsReportInterval == 0) _logger.LogOrdersSent(orderId);
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown on cancellation
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

    /// <summary>
    ///     Returns a pre-allocated exact-sized byte array for the requested size.
    ///     Eliminates GC allocations in the hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetOrCreateBuffer(int size)
    {
        if ((uint)size >= (uint)_bufferCache.Length)
            return new byte[size];

        return _bufferCache[size] ??= new byte[size];
    }
}