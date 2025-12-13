using System.Threading.Channels;
using Confluent.Kafka;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Google.Protobuf;

namespace FalconFX.MatchingEngine;

public class EngineWorker(ILogger<EngineWorker> logger, IProducer<string, byte[]> producer) : BackgroundService
{
    private const string TradeTopic = "trades";

    // 1. Input Channel (შემომავალი ორდერები)
    // SingleReader = true (მხოლოდ ძრავა კითხულობს)
    private readonly Channel<Order> _inputChannel = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // FIX: Increase pool size from 1,000,000 to 10,000,000
    private readonly OrderBook _orderBook = new(10_000_000); // 10 მილიონი ორდერის ადგილი

    // 2. Output Channel (შემდგარი გარიგებები)
    // SingleWriter = true (მხოლოდ ძრავა წერს)
    private readonly Channel<Trade> _outputChannel = Channel.CreateUnbounded<Trade>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // დაამატე ეს ორი ცვლადი
    private long _ordersProcessed;
    private long _tradesCreated;

    // ეს მეთოდი არის Public API - ამით შემოვა ორდერები გარედან
    public void EnqueueOrder(Order order)
    {
        _inputChannel.Writer.TryWrite(order);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("🚀 Engine Started. Waiting for orders...");

        // გავუშვათ ცალკე თრედი შედეგების დამუშავებისთვის (მაგ: ლოგირება ან კაფკა)
        _ = Task.Run(() => ProcessTradesAsync(stoppingToken), stoppingToken);

        // გავუშვათ მთავარი ძრავის ლუპი
        await RunMatchingEngineAsync(stoppingToken);
    }

    private async Task RunMatchingEngineAsync(CancellationToken token)
    {
        var reader = _inputChannel.Reader;
        logger.LogInformation("⚡ Engine loop running...");

        while (await reader.WaitToReadAsync(token))
            // FAST LOOP: Consumes everything currently in the channel buffer 
            // without awaiting until the buffer is empty.
        while (reader.TryRead(out var order))
        {
            _orderBook.ProcessOrder(order, trade =>
            {
                _outputChannel.Writer.TryWrite(trade);
                // Reduce Interlocked calls for speed (approximate stats are fine for HFT)
                Instrumentation.TradesCreated.Add(1);
                Interlocked.Increment(ref _tradesCreated);
            });

            Instrumentation.OrdersProcessed.Add(1);
            Interlocked.Increment(ref _ordersProcessed);
        }
    }

    // --- CONSUMER THREAD (Output) ---
    private async Task ProcessTradesAsync(CancellationToken token)
    {
        var reader = _outputChannel.Reader;
        var tradeMsg = new Message<string, byte[]> { Key = "EURUSD" }; // Reuse object

        // ყოველ 1 წამში ერთხელ დავბეჭდოთ სტატისტიკა
        var reportingTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                var orders = Interlocked.Read(ref _ordersProcessed);
                var trades = Interlocked.Read(ref _tradesCreated);
                logger.LogInformation(
                    "📊 STATS: Processed: {Orders:N0} orders | Matches: {Trades:N0} trades", orders, trades);
            }
        }, token);

        while (await reader.WaitToReadAsync(token))
        while (reader.TryRead(out var trade))
        {
            // 1. Map Internal Struct -> Protobuf Class
            // (Allocates memory, but unavoidable at the edge of the system for I/O)
            var protoTrade = new TradeExecuted
            {
                Id = DateTime.UtcNow.Ticks, // Just a placeholder ID
                MakerOrderId = trade.MakerOrderId,
                TakerOrderId = trade.TakerOrderId,
                Price = (long)trade.Price,
                Quantity = (long)trade.Quantity,
                Timestamp = trade.Timestamp,
                Symbol = "EURUSD"
            };

            // 2. Serialize
            tradeMsg.Value = protoTrade.ToByteArray();

            // 3. Produce to Kafka (Non-blocking / Async)
            producer.Produce(TradeTopic, tradeMsg);

            // Optional: Handle error callback if needed, but keep it light
        }
    }
}