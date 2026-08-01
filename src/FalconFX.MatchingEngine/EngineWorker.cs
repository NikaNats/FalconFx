using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

/// <summary>
///     Ultra-low latency, single-threaded Matching Engine.
///     Zero-allocation matching + high-throughput trade publishing.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private const string ServiceName = "MatchingEngine";
    private const string TradesTopic = "trades";

    private readonly ILogger<EngineWorker> _logger;
    private readonly OrderBook _orderBook;

    private readonly Channel<Order> _orderChannel = Channel.CreateBounded<Order>(new BoundedChannelOptions(1_000_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly IProducer<long, TradeExecuted> _tradeProducer;

    private long _ordersProcessed;
    private long _tradesMatched;

    public EngineWorker(ILogger<EngineWorker> logger, IProducer<long, TradeExecuted> tradeProducer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tradeProducer = tradeProducer ?? throw new ArgumentNullException(nameof(tradeProducer));

        // Pre-allocate OrderBook memory pool for 500,000 nodes
        _orderBook = new OrderBook(500_000);
    }

    // Non-blocking try-write
    public bool EnqueueOrder(Order order)
    {
        if (_orderChannel.Writer.TryWrite(order))
        {
            Interlocked.Increment(ref _ordersProcessed);
            return true;
        }

        return false;
    }

    public async ValueTask EnqueueOrderAsync(Order order, CancellationToken token = default)
    {
        await _orderChannel.Writer.WriteAsync(order, token).ConfigureAwait(false);
        Interlocked.Increment(ref _ordersProcessed);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 {ServiceName} Starting...", ServiceName);

        var engineThread = Task.Factory.StartNew(
            () => RunMatchingLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var statsThread = Task.Factory.StartNew(
            () => RunStatsLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        await Task.WhenAll(engineThread, statsThread).ConfigureAwait(false);
    }

    private async Task RunMatchingLoop(CancellationToken token)
    {
        var reader = _orderChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            while (reader.TryRead(out var order))
                _orderBook.ProcessOrder(order, trade =>
                {
                    Interlocked.Increment(ref _tradesMatched);

                    var tradeProto = new TradeExecuted
                    {
                        MakerOrderId = trade.MakerOrderId,
                        TakerOrderId = trade.TakerOrderId,
                        Price = trade.Price,
                        Quantity = trade.Quantity,
                        Symbol = "EURUSD",
                        Timestamp = trade.Timestamp
                    };

                    var message = new Message<long, TradeExecuted>
                    {
                        Key = trade.MakerOrderId,
                        Value = tradeProto
                    };

                    while (!token.IsCancellationRequested)
                        try
                        {
                            _tradeProducer.Produce(TradesTopic, message);
                            break;
                        }
                        catch (ProduceException<long, TradeExecuted> ex) when (ex.Error.Code ==
                                                                               ErrorCode.Local_QueueFull)
                        {
                            _tradeProducer.Poll(TimeSpan.FromMilliseconds(1));
                        }
                });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunStatsLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                _logger.LogInformation("STATS: Processed: {Orders:N0} orders | Matches: {Trades:N0} trades",
                    Interlocked.Read(ref _ordersProcessed), Interlocked.Read(ref _tradesMatched));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
///     High-performance Protobuf serializer using uninitialized buffers.
/// </summary>
public sealed class TradeExecutedSerializer : ISerializer<TradeExecuted>
{
    public static readonly TradeExecutedSerializer Instance = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] Serialize(TradeExecuted data, SerializationContext context)
    {
        var size = data.CalculateSize();
        var buffer = GC.AllocateUninitializedArray<byte>(size);
        data.WriteTo(buffer);
        return buffer;
    }
}

internal static partial class EngineLogExtensions
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Matching Engine Starting...")]
    public static partial void LogEngineStarting(this ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Matching Engine Hot Loop Running...")]
    public static partial void LogEngineLoopRunning(this ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "STATS: Processed: {Orders:N0} orders | Matches: {Trades:N0} trades")]
    public static partial void LogEngineStats(this ILogger logger, long orders, long trades);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Failed to produce trade to Kafka: {Reason}")]
    public static partial void LogTradeProduceError(this ILogger logger, string reason);
}