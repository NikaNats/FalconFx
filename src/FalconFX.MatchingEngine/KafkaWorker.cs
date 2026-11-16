using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

public sealed class KafkaWorker : BackgroundService
{
    private const string OrdersTopic = "orders";
    private const string TradesTopic = "trades";
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
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, OrdersTopic).ConfigureAwait(false);
        await KafkaUtils.EnsureTopicExistsAsync(_config, _logger, TradesTopic).ConfigureAwait(false);

        // CRITICAL: .Unwrap() makes ExecuteAsync await the actual async consumer loop.
        // Without it BackgroundService believes startup finished immediately and the loop
        // becomes a detached, unobserved task.
        await Task.Factory.StartNew(
                () => StartConsumerLoopAsync(_consumer, stoppingToken),
                stoppingToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap()
            .ConfigureAwait(false);
    }

    private async Task StartConsumerLoopAsync(IConsumer<Ignore, byte[]> consumer, CancellationToken token)
    {
        consumer.Subscribe(OrdersTopic);
        _logger.LogConsumerLoopStarted();

        try
        {
            while (!token.IsCancellationRequested)
            {
                DrainProcessedOffsets(consumer);

                try
                {
                    var result = consumer.Consume(TimeSpan.FromMilliseconds(10));
                    if (result?.Message?.Value is null) continue;

                    while (result?.Message?.Value is not null && !token.IsCancellationRequested)
                    {
                        SubmitOrderRequest protoReq;
                        try
                        {
                            protoReq = SubmitOrderRequest.Parser.ParseFrom(result.Message.Value);
                        }
                        catch (InvalidProtocolBufferException ex)
                        {
                            // Poison pill: skip it instead of taking the whole consumer down.
                            // The skip marker goes through the engine's FIFO channel so its
                            // offset is stored only after all earlier orders were processed.
                            _logger.LogKafkaBadMessage(ex.Message);
                            var skipEnvelope = OrderEnvelope.Skip(result.TopicPartitionOffset);
                            if (!_engine.TryEnqueueOrder(skipEnvelope))
                                await _engine.EnqueueOrderAsync(skipEnvelope, token).ConfigureAwait(false);
                            result = consumer.Consume(TimeSpan.Zero);
                            continue;
                        }

                        var order = new Order(
                            protoReq.Id,
                            (OrderSide)protoReq.Side,
                            protoReq.Price,
                            protoReq.Quantity);

                        // The offset travels with the order; it is only stored once the
                        // engine has matched it and handed the trades to the producer.
                        var envelope = new OrderEnvelope(order, result.TopicPartitionOffset);

                        if (!_engine.TryEnqueueOrder(envelope))
                            await _engine.EnqueueOrderAsync(envelope, token).ConfigureAwait(false);

                        result = consumer.Consume(TimeSpan.Zero);
                    }
                }
                catch (ConsumeException ex)
                {
                    if (ex.Error.IsFatal)
                    {
                        _logger.LogKafkaFatalError(ex.Error.Reason);
                        throw; // surface fatal error to the host
                    }

                    _logger.LogKafkaTransientError(ex.Error.Reason);
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            DrainProcessedOffsets(consumer);

            try
            {
                consumer.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogKafkaTransientError($"Final offset commit failed: {ex.Message}");
            }

            try
            {
                consumer.Close();
            }
            catch
            {
                // Ignore close errors during teardown
            }

            _logger.LogKafkaWorkerStopped(ServiceName);
        }
    }

    private void DrainProcessedOffsets(IConsumer<Ignore, byte[]> consumer)
    {
        while (_engine.TryDequeueProcessedOffset(out var tpo))
        {
            try
            {
                // StoreOffset expects the next offset to read (offset + 1)
                consumer.StoreOffset(new TopicPartitionOffset(tpo.TopicPartition, tpo.Offset + 1));
            }
            catch (KafkaException ex)
            {
                _logger.LogKafkaTransientError($"StoreOffset failed: {ex.Error.Reason}");
            }
        }
    }
}

internal static partial class KafkaWorkerLogExtensions
{
    [LoggerMessage(EventId = 201, Level = LogLevel.Information, Message = "🚀 {ServiceName} starting...")]
    public static partial void LogKafkaWorkerStarting(this ILogger logger, string serviceName);

    [LoggerMessage(EventId = 202, Level = LogLevel.Information,
        Message = "🚀 Kafka Consumer Loop Started. Subscribed to 'orders'.")]
    public static partial void LogConsumerLoopStarted(this ILogger logger);

    [LoggerMessage(EventId = 203, Level = LogLevel.Warning, Message = "Kafka transient consume error: {Reason}")]
    public static partial void LogKafkaTransientError(this ILogger logger, string reason);

    [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Kafka fatal error: {Reason}")]
    public static partial void LogKafkaFatalError(this ILogger logger, string reason);

    [LoggerMessage(EventId = 205, Level = LogLevel.Critical, Message = "Unhandled exception in Kafka Consumer loop.")]
    public static partial void LogKafkaWorkerCriticalError(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 206, Level = LogLevel.Error,
        Message = "Discarding unparsable order message: {Reason}")]
    public static partial void LogKafkaBadMessage(this ILogger logger, string reason);

    [LoggerMessage(EventId = 208, Level = LogLevel.Information, Message = "🛑 {ServiceName} stopped gracefully.")]
    public static partial void LogKafkaWorkerStopped(this ILogger logger, string serviceName);
}
