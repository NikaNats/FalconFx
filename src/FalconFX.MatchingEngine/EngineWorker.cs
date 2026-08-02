using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.MatchingEngine.Services;
using FalconFX.Protos;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

/// <summary>
///     Ultra-low latency, single-threaded Matching Engine with Pre-Trade Risk Checks.
///     Zero-allocation matching + high-throughput trade publishing.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private const string ServiceName = "MatchingEngine";
    private const string TradesTopic = "trades";

    private readonly ILogger<EngineWorker> _logger;
    private readonly OrderBook _orderBook;
    private readonly PreTradeRiskChecker _riskChecker;

    private readonly Channel<Order> _orderChannel = Channel.CreateBounded<Order>(new BoundedChannelOptions(1_000_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly IProducer<long, TradeExecuted> _tradeProducer;

    private long _ordersProcessed;
    private long _ordersRejectedByRisk;
    private long _tradesMatched;

    public EngineWorker(ILogger<EngineWorker> logger, IProducer<long, TradeExecuted> tradeProducer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tradeProducer = tradeProducer ?? throw new ArgumentNullException(nameof(tradeProducer));

        // Pre-allocate OrderBook memory pool for 500,000 nodes
        _orderBook = new OrderBook(500_000);

        // Pre-Trade Risk Check Configuration (Max Qty: 10k, Max Value: 10M, Max Dev: 50 ticks)
        _riskChecker = new PreTradeRiskChecker(
            maxOrderQuantity: 10_000,
            maxNotionalValue: 10_000_000,
            maxPriceDeviation: 50);
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
        _logger.LogEngineStarting();

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
                {
                    // Retrieve active market price from opposite book side (Ask for Buy, Bid for Sell)
                    long currentMarketPrice = order.Side == OrderSide.Buy
                        ? _orderBook.GetBestAskPrice()
                        : _orderBook.GetBestBidPrice();

                    // Fall back to incoming order price if the opposite side of the book is empty
                    if (currentMarketPrice == -1)
                        currentMarketPrice = order.Price;

                    // 1. Ultra-fast Pre-Trade Risk Validation (< 1 μs)
                    var riskStatus = _riskChecker.ValidateOrder(in order, currentMarketPrice);
                    if (riskStatus != RiskCheckResult.Passed)
                    {
                        Interlocked.Increment(ref _ordersRejectedByRisk);
                        continue; // Skip order processing if rejected by risk engine
                    }

                    // 2. Matching Engine Execution
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
                _logger.LogEngineStats(
                    Interlocked.Read(ref _ordersProcessed),
                    Interlocked.Read(ref _ordersRejectedByRisk),
                    Interlocked.Read(ref _tradesMatched));
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
        Message = "STATS: Processed: {Orders:N0} orders | Risk Rejected: {Rejected:N0} orders | Matches: {Trades:N0} trades")]
    public static partial void LogEngineStats(this ILogger logger, long orders, long rejected, long trades);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Failed to produce trade to Kafka: {Reason}")]
    public static partial void LogTradeProduceError(this ILogger logger, string reason);
}