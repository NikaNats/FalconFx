using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;

namespace FalconFX.MatchingEngine;

public sealed class KafkaWorker : BackgroundService
{
    private const string TopicName = "orders";
    private const string ServiceName = "KafkaWorker";

    private readonly ILogger<KafkaWorker> _logger;
    private readonly EngineWorker _engine;
    private readonly IConsumer<Null, byte[]> _consumer;
    private readonly IConfiguration _config;

    public KafkaWorker(
        ILogger<KafkaWorker> logger,
        EngineWorker engine,
        IConsumer<Null, byte[]> consumer,
        IConfiguration config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogKafkaWorkerStarting(ServiceName);

        // 1. დაველოდოთ Broker-ის მზადყოფნას
        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);

        // 2. გავუშვათ Consumer ლუპი Dedicated LongRunning თრედზე
        await Task.Factory.StartNew(
            () => StartConsumerLoop(_consumer, stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private void StartConsumerLoop(IConsumer<Null, byte[]> consumer, CancellationToken token)
    {
        consumer.Subscribe(TopicName);
        _logger.LogConsumerLoopStarted();

        try
        {
            // HOT CONSUMER LOOP
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Block until message arrives or cancellation is requested (Microsecond reaction, 0 CPU spin when idle)
                    var result = consumer.Consume(token);
                    if (result?.Message?.Value == null) continue;

                    // Protobuf Deserialization
                    var protoReq = SubmitOrderRequest.Parser.ParseFrom(result.Message.Value);

                    // Zero-alloc mapping to internal Order struct
                    var order = new Order(
                        protoReq.Id,
                        (OrderSide)protoReq.Side,
                        protoReq.Price,
                        protoReq.Quantity
                    );

                    // Enqueue to Matching Engine
                    _engine.EnqueueOrder(order);
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.IsFatal)
                    {
                        _logger.LogKafkaFatalError(ex.Error.Reason);
                    }
                    else
                    {
                        _logger.LogKafkaTransientError(ex.Error.Reason);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected gracefully on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogKafkaWorkerCriticalError(ex);
        }
        finally
        {
            _logger.LogClosingConsumer();
            try
            {
                // უზრუნველყოფს Kafka Consumer-ის უსაფრთხო გამოთიშვას (Rebalance)
                consumer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogKafkaConsumerCloseError(ex.Message);
            }

            _logger.LogKafkaWorkerStopped(ServiceName);
        }
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
internal static partial class KafkaWorkerLogExtensions
{
    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "🚀 {ServiceName} starting...")]
    public static partial void LogKafkaWorkerStarting(this ILogger logger, string serviceName);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information, Message = "🚀 Kafka Consumer Loop Started. Subscribed to 'orders'.")]
    public static partial void LogConsumerLoopStarted(this ILogger logger);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Kafka transient consume error: {Reason}")]
    public static partial void LogKafkaTransientError(this ILogger logger, string reason);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Kafka fatal error: {Reason}")]
    public static partial void LogKafkaFatalError(this ILogger logger, string reason);

    [LoggerMessage(EventId = 205, Level = LogLevel.Critical, Message = "Unhandled exception in Kafka Consumer loop.")]
    public static partial void LogKafkaWorkerCriticalError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 206, Level = LogLevel.Information, Message = "🧹 Closing Kafka Consumer...")]
    public static partial void LogClosingConsumer(this ILogger logger);

    [LoggerMessage(EventId = 207, Level = LogLevel.Warning, Message = "Error closing Kafka consumer: {Reason}")]
    public static partial void LogKafkaConsumerCloseError(this ILogger logger, string reason);

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "🛑 {ServiceName} stopped gracefully.")]
    public static partial void LogKafkaWorkerStopped(this ILogger logger, string serviceName);
}