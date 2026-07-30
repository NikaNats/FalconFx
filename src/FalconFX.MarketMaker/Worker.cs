using System.Runtime.CompilerServices;
using Confluent.Kafka;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MarketMaker;

/// <summary>
///     Ultra-low latency, zero-allocation Market Maker worker node.
///     Designed for High-Frequency Trading (HFT) order stream simulation.
/// </summary>
public sealed class Worker : BackgroundService
{
    private const string ServiceName = "MarketMaker";
    private const string TopicName = "orders";
    private const int BatchCount = 100;
    private const int StatsReportInterval = 50_000;

    // Exact-size Buffer Cache for Zero-Alloc Serialization.
    // Protobuf message size varies slightly based on VarInt encoding (typically 20-32 bytes).
    // Array instances per size index are allocated ONCE on first hit and reused forever.
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

        // 1. Validate Infrastructure readiness
        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, TopicName).ConfigureAwait(false);

        _logger.LogWaitingLeaderElection();
        await Task.Delay(3000, stoppingToken).ConfigureAwait(false);

        // 2. Build HFT-Tuned Kafka Producer
        var bootstrapServers = _config.GetConnectionString("kafka");
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            _logger.LogMissingConnectionString();
            throw new InvalidOperationException("Kafka connection string 'kafka' is not configured.");
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,

            // Batching & Throughput
            LingerMs = 5,
            BatchSize = 1024 * 1024, // 1 MB
            BatchNumMessages = 10000,
            QueueBufferingMaxMessages = 1000000,
            QueueBufferingMaxKbytes = 512000,
            CompressionType = CompressionType.Lz4,

            // Durability & Speed Balance
            Acks = Acks.Leader,

            // 🔥 HFT Optimization: Disable delivery report callbacks to eliminate librdkafka event queue allocations
            EnableDeliveryReports = false,

            // Resilience Timeouts
            MessageTimeoutMs = 5000,
            SocketTimeoutMs = 5000
        };

        // Use Null Key to prevent string key serialization overhead & allocation
        using var producer = new ProducerBuilder<Null, byte[]>(producerConfig).Build();

        _logger.LogProducerStarted();

        var orderId = 0L;
        var rng = new XorShift64((ulong)DateTime.UtcNow.Ticks);
        var request = new SubmitOrderRequest();
        var kafkaMessage = new Message<Null, byte[]>();

        try
        {
            // ═══════════════════════════════════════════
            //  HOT LOOP — ZERO ALLOCATIONS INSIDE
            // ═══════════════════════════════════════════
            while (!stoppingToken.IsCancellationRequested)
            {
                for (var i = 0; i < BatchCount; i++)
                {
                    orderId++;

                    request.Id = orderId;
                    request.Side = rng.Next(1, 3); // 1 = Buy, 2 = Sell
                    request.Price = rng.Next(99, 102); // Tight spread [99-101]
                    request.Quantity = 10;

                    // Zero-Alloc Serialization into cached exact-sized buffer
                    var msgSize = request.CalculateSize();
                    var buffer = GetOrCreateBuffer(msgSize);

                    // Direct serialization into Span<byte>
                    request.WriteTo(buffer.AsSpan(0, msgSize));

                    kafkaMessage.Value = buffer;

                    try
                    {
                        // Synchronous queue insertion into librdkafka native C-memory buffer.
                        // Safe to overwrite 'buffer' on next iteration because librdkafka copies payload synchronously during Produce().
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

                if (orderId % StatsReportInterval == 0)
                {
                    _logger.LogOrdersSent(orderId);
                    await Task.Yield(); // Yield CPU briefly to prevent thread starvation
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected gracefully on service shutdown
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
                // Guarantee in-flight orders are delivered to Kafka broker before exit
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
            // Fallback for unexpectedly large messages
            return new byte[size];

        return _bufferCache[size] ??= new byte[size];
    }
}