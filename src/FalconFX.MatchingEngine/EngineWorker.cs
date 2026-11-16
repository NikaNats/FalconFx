using System.Collections.Concurrent;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.MatchingEngine.Services;
using FalconFX.Protos;

namespace FalconFX.MatchingEngine;

/// <summary>
///     Ultra-low latency, single-threaded Matching Engine with Pre-Trade Risk Checks.
///     Zero-allocation matching + high-throughput trade publishing.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private const string TradesTopic = "trades";

    private readonly Action<DeliveryReport<long, TradeExecuted>> _deliveryHandler;

    private readonly OrderBook _orderBook;

    private readonly Channel<OrderEnvelope> _orderChannel = Channel.CreateBounded<OrderEnvelope>(
        new BoundedChannelOptions(1_000_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ILogger<EngineWorker> _logger;

    private readonly ConcurrentQueue<TopicPartitionOffset> _processedOffsets = new();

    private readonly PreTradeRiskChecker _riskChecker;
    private readonly IProducer<long, TradeExecuted> _tradeProducer;

    private long _nextTradeId;
    private long _ordersProcessed;

    // Counters are written by the single matching thread and read by the stats loop / OTel
    private long _ordersRejectedByBook;
    private long _ordersRejectedByRisk;
    private long _tradesMatched;

    public EngineWorker(
        ILogger<EngineWorker> logger,
        IProducer<long, TradeExecuted> tradeProducer,
        long? initialTradeId = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tradeProducer = tradeProducer ?? throw new ArgumentNullException(nameof(tradeProducer));

        // PostgreSQL persists trades and deduplicates on the unique ("TradeId") index
        // (INSERT ... ON CONFLICT DO NOTHING), so restarting the counter at 0 would make
        // every trade of a new run collide with the previous run's IDs and be discarded.
        // Seed from wall-clock time (same scheme as MarketMaker order IDs) so IDs stay
        // unique across process restarts while remaining far below long.MaxValue.
        _nextTradeId = initialTradeId
                       ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        // Pre-allocate OrderBook memory pool for 500,000 nodes
        _orderBook = new OrderBook(minPrice: 90, maxPrice: 110, poolSize: 500_000);

        // Pre-Trade Risk Check Configuration (Max Qty: 10k, Max Value: 10M, Max Dev: 10 ticks)
        _riskChecker = new PreTradeRiskChecker(
            maxOrderQuantity: 10_000,
            maxNotionalValue: 10_000_000,
            maxPriceDeviation: 10);

        // Cached delegate avoids per-message closure allocations
        _deliveryHandler = report =>
        {
            if (report.Error.IsError)
                _logger.LogTradeProduceError(report.Error.Reason);
        };
    }

    public long OrdersProcessed => Volatile.Read(ref _ordersProcessed);
    public long OrdersRejectedByRisk => Volatile.Read(ref _ordersRejectedByRisk);
    public long OrdersRejectedByBook => Volatile.Read(ref _ordersRejectedByBook);
    public long TradesMatched => Volatile.Read(ref _tradesMatched);

    // Non-blocking try-write
    public bool TryEnqueueOrder(OrderEnvelope envelope)
    {
        return _orderChannel.Writer.TryWrite(envelope);
    }

    public bool EnqueueOrder(Order order)
    {
        return TryEnqueueOrder(new OrderEnvelope(order));
    }

    public ValueTask EnqueueOrderAsync(OrderEnvelope envelope, CancellationToken token = default)
    {
        return _orderChannel.Writer.WriteAsync(envelope, token);
    }

    public ValueTask EnqueueOrderAsync(Order order, CancellationToken token = default)
    {
        return EnqueueOrderAsync(new OrderEnvelope(order), token);
    }

    public bool TryDequeueProcessedOffset(out TopicPartitionOffset offset)
    {
        if (_processedOffsets.TryDequeue(out var dequeued))
        {
            offset = dequeued;
            return true;
        }

        offset = null!;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogEngineStarting();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var loopToken = linkedCts.Token;

        var engineThread = Task.Factory.StartNew(
            () => RunMatchingLoopAsync(loopToken),
            loopToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var statsThread = Task.Factory.StartNew(
            () => RunStatsLoopAsync(loopToken),
            loopToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var first = await Task.WhenAny(engineThread, statsThread).ConfigureAwait(false);

        // Cancel siblings so a fault in one loop cannot leave the other running detached
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(engineThread, statsThread).ConfigureAwait(false);

        if (first.IsFaulted)
            await first.ConfigureAwait(false);
    }

    private async Task RunMatchingLoopAsync(CancellationToken token)
    {
        var reader = _orderChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            while (reader.TryRead(out var envelope))
            {
                if (envelope.Order is not { } order)
                {
                    // Poison-pill marker: release its offset only now, behind every earlier
                    // order in the channel, so a crash cannot commit past unprocessed messages.
                    if (envelope.KafkaOffset is { } skippedOffset)
                        _processedOffsets.Enqueue(skippedOffset);
                    continue;
                }

                _ordersProcessed++;

                // Reference price fallback: opposite BBO -> same-side BBO -> last trade -> order price
                var referencePrice = ResolveReferencePrice(in order);

                // 1. Ultra-fast Pre-Trade Risk Validation (< 1 μs)
                var riskStatus = _riskChecker.ValidateOrder(in order, referencePrice);
                if (riskStatus != RiskCheckResult.Passed)
                {
                    _ordersRejectedByRisk++;
                    if (envelope.KafkaOffset is { } rejectedOffset)
                        _processedOffsets.Enqueue(rejectedOffset);
                    continue;
                }

                var nowTicks = DateTime.UtcNow.Ticks;

                // 2. Matching Engine Execution
                var result = _orderBook.ProcessOrder(order, trade =>
                {
                    _tradesMatched++;
                    var tradeId = ++_nextTradeId;

                    var tradeProto = new TradeExecuted
                    {
                        Id = tradeId,
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

                    // Finish publishing even if stoppingToken fires mid-match
                    while (true)
                        try
                        {
                            _tradeProducer.Produce(TradesTopic, message, _deliveryHandler);
                            break;
                        }
                        catch (ProduceException<long, TradeExecuted> ex)
                            when (ex.Error.Code == ErrorCode.Local_QueueFull)
                        {
                            _tradeProducer.Poll(TimeSpan.FromMilliseconds(1));
                        }
                        catch (ProduceException<long, TradeExecuted> ex)
                        {
                            // Delivery is best-effort here; the failure is surfaced and the
                            // offset is still stored (deduplicated downstream).
                            _logger.LogTradeProduceError(ex.Error.Reason);
                            break;
                        }
                }, nowTicks);

                if (result != OrderResult.Success)
                    _ordersRejectedByBook++;

                if (envelope.KafkaOffset is { } processedOffset)
                    _processedOffsets.Enqueue(processedOffset);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                _tradeProducer.Flush(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogTradeProduceError($"Flush failed on shutdown: {ex.Message}");
            }
        }
    }

    private long ResolveReferencePrice(in Order order)
    {
        var price = order.Side == OrderSide.Buy
            ? _orderBook.GetBestAskPrice()
            : _orderBook.GetBestBidPrice();

        if (price != -1) return price;

        price = order.Side == OrderSide.Buy
            ? _orderBook.GetBestBidPrice()
            : _orderBook.GetBestAskPrice();

        if (price != -1) return price;

        return _orderBook.LastTradedPrice != -1 ? _orderBook.LastTradedPrice : order.Price;
    }

    private async Task RunStatsLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token).ConfigureAwait(false);
                _logger.LogEngineStats(OrdersProcessed, OrdersRejectedByRisk, OrdersRejectedByBook, TradesMatched);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}

internal static partial class EngineLogExtensions
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Matching Engine Starting...")]
    public static partial void LogEngineStarting(this ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Matching Engine Hot Loop Running...")]
    public static partial void LogEngineLoopRunning(this ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information,
        Message = "STATS: Processed: {Orders:N0} | Risk Rejected: {RiskRejected:N0} | Book Rejected: {BookRejected:N0} | Matches: {Trades:N0}")]
    public static partial void LogEngineStats(
        this ILogger logger, long orders, long riskRejected, long bookRejected, long trades);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Failed to produce trade to Kafka: {Reason}")]
    public static partial void LogTradeProduceError(this ILogger logger, string reason);
}
