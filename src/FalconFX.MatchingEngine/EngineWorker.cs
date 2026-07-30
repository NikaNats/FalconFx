using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using FalconFX.ServiceDefaults;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

public sealed class EngineWorker : BackgroundService
{
    private const string TradeTopic = "trades";
    private const int StatsReportIntervalMs = 1000;

    private readonly ILogger<EngineWorker> _logger;
    private readonly IProducer<Null, byte[]> _producer;

    // 1. Input Channel (შემომავალი ორდერები)
    // SingleReader = true (მხოლოდ Matching Engine თრედი კითხულობს)
    private readonly Channel<Order> _inputChannel = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // 2. Output Channel (შემდგარი გარიგებები)
    // SingleReader = true, SingleWriter = true (მხოლოდ Engine წერს, მხოლოდ 1 Consumer კითხულობს)
    private readonly Channel<Trade> _outputChannel = Channel.CreateUnbounded<Trade>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // OrderBook 10 მილიონი ორდერის ტევადობით (Intrusive Linked List)
    private readonly OrderBook _orderBook = new(10_000_000);

    // სტატისტიკის მრიცხველები (Plain long - Interlocked-ის გარეშე hot path-ში)
    private long _ordersProcessed;
    private long _tradesCreated;

    // Zero-Alloc Serialization Buffer ქეში გარიგებებისთვის
    private readonly byte[][] _bufferCache = new byte[128][];

    public EngineWorker(ILogger<EngineWorker> logger, IProducer<Null, byte[]> producer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    }

    /// <summary>
    /// Public API — ორდერების მიღება gRPC/Network-იდან.
    /// Thread-safe, High-Throughput Enqueue.
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
        getOrdersProcessed: () => Volatile.Read(ref _ordersProcessed),
        getTradesCreated: () => Volatile.Read(ref _tradesCreated)
    );

        // გავუშვათ Consumer თრედი Kafka-ში Trades გაგზავნისთვის (LongRunning)
        Task.Factory.StartNew(
            () => ProcessTradesAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // გავუშვათ სტატისტიკის ლოგერი
        Task.Factory.StartNew(
            () => ReportStatsAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // გავუშვათ მთავარი Matching Engine ლუპი Dedicated LongRunning თრედზე
        return Task.Factory.StartNew(
            () => RunMatchingEngineAsync(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    // ═══════════════════════════════════════════
    //  HOT LOOP — SINGLE THREADED MATCHING ENGINE
    // ═══════════════════════════════════════════
    private async Task RunMatchingEngineAsync(CancellationToken token)
    {
        var reader = _inputChannel.Reader;
        _logger.LogEngineLoopRunning();

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            // FAST INNER LOOP: კითხულობს ბუფერს CPU Yield-ის გარეშე
            while (reader.TryRead(out var order))
            {
                _orderBook.ProcessOrder(order, trade =>
                {
                    _outputChannel.Writer.TryWrite(trade);

                    // არანაირი Interlocked! ჩვეულებრივი ინკრემენტი single-thread-ში
                    _tradesCreated++;
                });

                _ordersProcessed++;
            }
        }
    }

    // ═══════════════════════════════════════════
    //  OUTPUT THREAD — KAFKA PRODUCER (Zero-Alloc)
    // ═══════════════════════════════════════════
    private async Task ProcessTradesAsync(CancellationToken token)
    {
        var reader = _outputChannel.Reader;
        var protoTrade = new TradeExecuted();
        var kafkaMessage = new Message<Null, byte[]>();

        while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var trade))
            {
                // 1. Mutate reusable Protobuf object in-place
                protoTrade.Id = trade.Timestamp; // ან Snowflake ID
                protoTrade.MakerOrderId = trade.MakerOrderId;
                protoTrade.TakerOrderId = trade.TakerOrderId;
                protoTrade.Price = trade.Price;
                protoTrade.Quantity = trade.Quantity;
                protoTrade.Timestamp = trade.Timestamp;
                protoTrade.Symbol = "EURUSD";

                // 2. Zero-Alloc Serialization into cached buffer
                var msgSize = protoTrade.CalculateSize();
                var buffer = GetOrCreateBuffer(msgSize);
                protoTrade.WriteTo(buffer.AsSpan(0, msgSize));

                kafkaMessage.Value = buffer;

                // 3. Fire-and-forget produce to Kafka
                try
                {
                    _producer.Produce(TradeTopic, kafkaMessage);
                }
                catch (ProduceException<Null, byte[]> ex)
                {
                    _logger.LogTradeProduceError(ex.Error.Reason);
                }
            }
        }
    }

    // ═══════════════════════════════════════════
    //  STATS REPORTING TASK
    // ═══════════════════════════════════════════
    private async Task ReportStatsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(StatsReportIntervalMs, token).ConfigureAwait(false);

            // Volatile.Read უზრუნველყოფს სხვა თრედიდან ცვლადის უახლესი მნიშვნელობის წაკითხვას Lock-ის გარეშე
            var orders = Volatile.Read(ref _ordersProcessed);
            var trades = Volatile.Read(ref _tradesCreated);

            _logger.LogEngineStats(orders, trades);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetOrCreateBuffer(int size)
    {
        if ((uint)size >= (uint)_bufferCache.Length)
        {
            return new byte[size];
        }

        return _bufferCache[size] ??= new byte[size];
    }
}

// ═══════════════════════════════════════════
//  ZERO-ALLOCATION LOGGING EXTENSIONS
// ═══════════════════════════════════════════
internal static partial class EngineLogExtensions
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "🚀 Matching Engine Starting...")]
    public static partial void LogEngineStarting(this ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "⚡ Matching Engine Hot Loop Running...")]
    public static partial void LogEngineLoopRunning(this ILogger logger);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "📊 STATS: Processed: {Orders:N0} orders | Matches: {Trades:N0} trades")]
    public static partial void LogEngineStats(this ILogger logger, long orders, long trades);

    [LoggerMessage(EventId = 104, Level = LogLevel.Warning, Message = "Failed to produce trade to Kafka: {Reason}")]
    public static partial void LogTradeProduceError(this ILogger logger, string reason);
}