using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

/// <summary>
///     Ultra-low latency, single-threaded core Matching Engine worker.
///     Processes inbound order channels and executes zero-allocation price-time priority matching.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private const string TradeTopic = "trades";
    private const int StatsReportIntervalMs = 1000;

    // Inbound order queue: SingleReader = true optimization for single-threaded matching hot path
    private readonly Channel<Order> _inputChannel = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ILogger<EngineWorker> _logger;

    // Pre-allocated OrderBook with 10M order node capacity (Intrusive Doubly-Linked List)
    private readonly OrderBook _orderBook = new(10_000_000);

    // Outbound trade execution queue: SingleReader = true, SingleWriter = true
    private readonly Channel<Trade> _outputChannel = Channel.CreateUnbounded<Trade>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private readonly IProducer<Null, byte[]> _producer;

    // Lock-free atomic execution counters (Mutated exclusively on the single matching thread)
    private long _ordersProcessed;
    private long _tradesCreated;

    public EngineWorker(ILogger<EngineWorker> logger, IProducer<Null, byte[]> producer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    }

    /// <summary>
    ///     Enqueues an incoming order into the high-speed execution channel.
    ///     Thread-safe, non-blocking lock-free operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueOrder(in Order order)
    {
        _inputChannel.Writer.TryWrite(order);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogEngineStarting();

        Instrumentation.RegisterObservableMetrics(
            () => Volatile.Read(ref _ordersProcessed),
            () => Volatile.Read(ref _tradesCreated)
        );

        // Spawn trade dispatch consumer thread
        Task.Factory.StartNew(
            () => ProcessTradesAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Spawn metric reporting thread
        Task.Factory.StartNew(
            () => ReportStatsAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Run the single-threaded matching engine loop on a dedicated OS thread
        return Task.Factory.StartNew(
            () => RunMatchingEngineAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    // ═══════════════════════════════════════════
    //  HOT LOOP — SINGLE-THREADED MATCHING ENGINE
    // ═══════════════════════════════════════════
    private async Task RunMatchingEngineAsync(CancellationToken token)
    {
        var reader = _inputChannel.Reader;
        _logger.LogEngineLoopRunning();

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            // Fast inner loop: drains channel buffer without CPU yield
        while (reader.TryRead(out var order))
        {
            _orderBook.ProcessOrder(order, trade =>
            {
                _outputChannel.Writer.TryWrite(trade);

                // Direct single-threaded increment eliminates Interlocked overhead
                _tradesCreated++;
            });

            _ordersProcessed++;
        }
    }

    // ═══════════════════════════════════════════
    //  OUTPUT THREAD — KAFKA PRODUCER
    // ═══════════════════════════════════════════
    private async Task ProcessTradesAsync(CancellationToken token)
    {
        var reader = _outputChannel.Reader;
        var protoTrade = new TradeExecuted();

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        while (reader.TryRead(out var trade))
        {
            // Mutate reusable Protobuf object in-place
            protoTrade.Id = trade.Timestamp;
            protoTrade.MakerOrderId = trade.MakerOrderId;
            protoTrade.TakerOrderId = trade.TakerOrderId;
            protoTrade.Price = trade.Price;
            protoTrade.Quantity = trade.Quantity;
            protoTrade.Timestamp = trade.Timestamp;
            protoTrade.Symbol = "EURUSD";

            // Exact-sized byte payload per message ensures librdkafka memory safety
            var msgSize = protoTrade.CalculateSize();
            var payload = new byte[msgSize];
            protoTrade.WriteTo(payload.AsSpan());

            // Fire-and-forget produce to Kafka
            try
            {
                _producer.Produce(TradeTopic, new Message<Null, byte[]> { Value = payload });
            }
            catch (ProduceException<Null, byte[]> ex)
            {
                _logger.LogTradeProduceError(ex.Error.Reason);
            }
        }
    }

    // ═══════════════════════════════════════════
    //  METRICS REPORTING TASK
    // ═══════════════════════════════════════════
    private async Task ReportStatsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(StatsReportIntervalMs, token).ConfigureAwait(false);

            var orders = Volatile.Read(ref _ordersProcessed);
            var trades = Volatile.Read(ref _tradesCreated);

            _logger.LogEngineStats(orders, trades);
        }
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
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