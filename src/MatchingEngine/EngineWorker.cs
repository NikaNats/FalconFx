using System.Threading;
using System.Threading.Channels;
using MatchingEngine.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MatchingEngine;

public class EngineWorker : BackgroundService
{
    private readonly ILogger<EngineWorker> _logger;
    private readonly OrderBook _orderBook;
    
    // 1. Input Channel (შემომავალი ორდერები)
    // SingleReader = true (მხოლოდ ძრავა კითხულობს)
    private readonly Channel<Order> _inputChannel = Channel.CreateUnbounded<Order>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    // 2. Output Channel (შემდგარი გარიგებები)
    // SingleWriter = true (მხოლოდ ძრავა წერს)
    private readonly Channel<Trade> _outputChannel = Channel.CreateUnbounded<Trade>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // დაამატე ეს ორი ცვლადი
    private long _ordersProcessed = 0;
    private long _tradesCreated = 0;

    public EngineWorker(ILogger<EngineWorker> logger)
    {
        _logger = logger;
        _orderBook = new OrderBook(1_000_000); // 1 მილიონი ორდერის ადგილი
    }

    // ეს მეთოდი არის Public API - ამით შემოვა ორდერები გარედან
    public void EnqueueOrder(Order order)
    {
        _inputChannel.Writer.TryWrite(order);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Engine Started. Waiting for orders...");

        // გავუშვათ ცალკე თრედი შედეგების დამუშავებისთვის (მაგ: ლოგირება ან კაფკა)
        _ = Task.Run(() => ProcessTradesAsync(stoppingToken));

        // გავუშვათ მთავარი ძრავის ლუპი
        await RunMatchingEngineAsync(stoppingToken);
    }

    private async Task RunMatchingEngineAsync(CancellationToken token)
    {
        var reader = _inputChannel.Reader;
        int batchCount = 0;

        // Optional: Create a parent trace for the whole run
        using var activity = Instrumentation.ActivitySource.StartActivity("MatchingLoop");

        while (await reader.WaitToReadAsync(token))
        {
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
                    await Task.Delay(1); 
                }
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
                await Task.Delay(1000);
                long orders = Interlocked.Read(ref _ordersProcessed);
                long trades = Interlocked.Read(ref _tradesCreated);
                _logger.LogInformation($"📊 STATS: Processed: {orders:N0} orders | Matches: {trades:N0} trades");
            }
        });

        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var trade))
            {
                // აქ მოხდება Kafka-ში გაგზავნა მოგვიანებით
                // ჯერ უბრალოდ დავლოგოთ (მაგრამ არა ძალიან ხშირად, რომ არ გავჭედოთ კონსოლი)
                if (trade.Price > 0) 
                {
                    // _logger.LogInformation($"Trade Executed: {trade.Quantity} @ {trade.Price}");
                }
            }
        }
    }
}