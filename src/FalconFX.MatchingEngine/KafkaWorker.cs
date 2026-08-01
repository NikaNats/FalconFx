using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;

namespace FalconFX.MatchingEngine;

public sealed class KafkaWorker : BackgroundService
{
    private const string TopicName = "orders";
    private const string ServiceName = "KafkaWorker";

    private readonly IConfiguration _config;
    private readonly IConsumer<Ignore, byte[]> _consumer;
    private readonly EngineWorker _engine;
    private readonly ILogger<KafkaWorker> _logger;

    public KafkaWorker(
        ILogger<KafkaWorker> logger,
        EngineWorker engine,
        IConsumer<Ignore, byte[]> consumer,
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

        await KafkaUtils.WaitForBrokerReady(_config, _logger, stoppingToken).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, TopicName).ConfigureAwait(false);

        await Task.Delay(2000, stoppingToken).ConfigureAwait(false);

        await Task.Factory.StartNew(
            () => StartConsumerLoop(_consumer, stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).ConfigureAwait(false);
    }

    private async Task StartConsumerLoop(IConsumer<Ignore, byte[]> consumer, CancellationToken token)
    {
        consumer.Subscribe(TopicName);
        _logger.LogConsumerLoopStarted();

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(TimeSpan.FromMilliseconds(10));
                    if (result?.Message?.Value == null) continue;

                    while (result?.Message?.Value != null && !token.IsCancellationRequested)
                    {
                        var protoReq = SubmitOrderRequest.Parser.ParseFrom(result.Message.Value);
                        var order = new Order(
                            protoReq.Id,
                            (OrderSide)protoReq.Side,
                            protoReq.Price,
                            protoReq.Quantity
                        );

                        if (!_engine.EnqueueOrder(order))
                        {
                            await _engine.EnqueueOrderAsync(order, token).ConfigureAwait(false);
                        }

                        result = consumer.Consume(TimeSpan.Zero);
                    }
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.IsFatal)
                        _logger.LogKafkaFatalError(ex.Error.Reason);
                    else
                        _logger.LogKafkaTransientError(ex.Error.Reason);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { consumer.Close(); } catch { }
            _logger.LogKafkaWorkerStopped(ServiceName);
        }
    }
}

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

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "🛑 {ServiceName} stopped gracefully.")]
    public static partial void LogKafkaWorkerStopped(this ILogger logger, string serviceName);
}