using System.Threading.Channels;
using MatchingEngine.Models;

namespace MatchingEngine;

public class EngineWorker(ILogger<EngineWorker> logger) : BackgroundService
{
    // 1. Input Channel (შემომავალი ორდერები)
    // SingleReader = true (მხოლოდ ძრავა კითხულობს)
    private readonly Channel<Order> _inputChannel = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly OrderBook _orderBook = new(); // 1 მილიონი ორდერის ადგილი

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
        var batchCount = 0;

        // Optional: Create a parent trace for the whole run
        using var activity = Instrumentation.ActivitySource.StartActivity("MatchingLoop");

        while (await reader.WaitToReadAsync(token))
        while (reader.TryRead(out var order))
        {
            _orderBook.ProcessOrder(order, trade =>
            {
                _outputChannel.Writer.TryWrite(trade);

                // 🔥 METRIC 1: Count Trade
                Instrumentation.TradesCreated.Add(1);
            });

            // 🔥 METRIC 2: Count Order
            Instrumentation.OrdersProcessed.Add(1);

            // Performance Optimization:
            // We removed Interlocked.Increment because OTel counters 
            // handle thread safety for us, but if you still need the local long 
            // for your console logs, keep Interlocked as well.
            Interlocked.Increment(ref _ordersProcessed);

            batchCount++;
            if (batchCount >= 5000)
            {
                batchCount = 0;
                // Removed Task.Delay(1) - it was causing 15ms delays on Windows,
                // artificially capping throughput. The Channel reader is already async.
            }
        }
    }

    // --- CONSUMER THREAD (Output) ---
    private async Task ProcessTradesAsync(CancellationToken token)
    {
        var reader = _outputChannel.Reader;

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
            // აქ მოხდება Kafka-ში გაგზავნა მოგვიანებით
            // ჯერ უბრალოდ დავლოგოთ (მაგრამ არა ძალიან ხშირად, რომ არ გავჭედოთ კონსოლი)
            if (trade.Price > 0)
            {
                // _logger.LogInformation($"Trade Executed: {trade.Quantity} @ {trade.Price}");
            }
    }
}