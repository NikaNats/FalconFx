using System.Diagnostics;
using FalconFX.MatchingEngine;
using FalconFX.MatchingEngine.Models;
using FalconFX.Protos;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace FalconFX.Benchmarks.Benchmarks;

/// <summary>
///     In-memory async pipeline benchmark measuring end-to-end execution speed:
///     Protobuf Deserialization -> System.Threading.Channels -> EngineWorker Async Thread -> Trade Serialization.
/// </summary>
public class RealPipelineBenchmark
{
    public static async Task RunAsync(int totalOrders = 500_000)
    {
        Console.WriteLine("\n=================================================");
        Console.WriteLine($"Starting Internal Async Pipeline Benchmark ({totalOrders:N0} orders)...");
        Console.WriteLine("=================================================");

        // Arrange a lightweight counting producer and the core Matching Engine worker
        var expectedTrades = totalOrders / 2; // alternating buy/sell @ same price
        long observedTrades = 0;
        var allTradesProduced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var kafkaProducer = new CountingProducer(() =>
        {
            if (Interlocked.Increment(ref observedTrades) >= expectedTrades)
                allTradesProduced.TrySetResult();
        });
        var engineWorker = new EngineWorker(NullLogger<EngineWorker>.Instance, kafkaProducer);

        using var cts = new CancellationTokenSource();
        await engineWorker.StartAsync(cts.Token).ConfigureAwait(false);

        // Prepare Protobuf payload bytes
        var protoOrderBuy = new SubmitOrderRequest { Id = 1, Side = 1, Price = 100, Quantity = 10 };
        var protoOrderSell = new SubmitOrderRequest { Id = 2, Side = 2, Price = 100, Quantity = 10 };

        var buyBytes = protoOrderBuy.ToByteArray();
        var sellBytes = protoOrderSell.ToByteArray();

        var sw = Stopwatch.StartNew();

        // Act: Protobuf Parse -> System.Threading.Channels -> Async Engine Worker Loop
        for (var i = 0; i < totalOrders / 2; i++)
        {
            var buyReq = SubmitOrderRequest.Parser.ParseFrom(buyBytes);
            engineWorker.EnqueueOrder(new Order(buyReq.Id, (OrderSide)buyReq.Side, buyReq.Price, buyReq.Quantity));

            var sellReq = SubmitOrderRequest.Parser.ParseFrom(sellBytes);
            engineWorker.EnqueueOrder(new Order(sellReq.Id, (OrderSide)sellReq.Side, sellReq.Price, sellReq.Quantity));
        }

        // The producer callback completes the benchmark: this measures real work only.
        await allTradesProduced.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        sw.Stop();

        await engineWorker.StopAsync(cts.Token).ConfigureAwait(false);

        var elapsedSeconds = sw.Elapsed.TotalSeconds;
        var throughput = totalOrders / elapsedSeconds;
        var amortizedMicrosPerOrder = elapsedSeconds * 1_000_000 / totalOrders;

        Console.WriteLine("\nInternal Async Pipeline Benchmark Results:");
        Console.WriteLine($"Total Elapsed Execution Time: {elapsedSeconds:F3} seconds");
        Console.WriteLine($"Pipeline Throughput: {throughput:N0} orders/sec");
        Console.WriteLine($"Amortized Time / Order: {amortizedMicrosPerOrder:F3} microseconds/order");
        Console.WriteLine("=================================================\n");
    }
}
